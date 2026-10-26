using System;
using System.Collections;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace QuestMarkerApp.API
{
    /// <summary>
    /// Push client using long-poll / event stream against the Python bridge server.
    /// Endpoint: GET {server}/api/events?since={ts}  (JSON coordinates when new data arrives)
    /// Falls back gracefully if the server does not support it.
    /// True WebSocket requires a native plugin on Quest; long-poll is portable.
    /// </summary>
    public class WebSocketPushClient : MonoBehaviour
    {
        [SerializeField] private string serverUrl = "http://192.168.1.100:5000";
        [SerializeField] private string apiKey = "";
        [SerializeField] private float pollTimeoutSeconds = 25f;
        [SerializeField] private float retryDelaySeconds = 2f;
        [Tooltip("Upper bound of the exponential back-off between failed polls")]
        [SerializeField] private float maxRetryDelaySeconds = 30f;
        [SerializeField] private bool logEvents = true;

        public event Action<CoordinateResponse> OnCoordinatesReceived;
        /// <summary>Raised on the first successful poll after Connect or after a run of failures.</summary>
        public event Action OnConnected;
        public event Action<string> OnError;

        private bool running;
        private Coroutine loop;
        private long sinceTimestamp;
        private UnityWebRequest current;
        private int consecutiveFailures;
        private bool connected;
        private bool restartPoll;

        public void Connect(string url = null, string key = null)
        {
            if (!string.IsNullOrEmpty(url)) serverUrl = url.Trim().TrimEnd('/');
            if (!string.IsNullOrEmpty(key)) apiKey = key;
            // 0 = "everything": the first poll returns the current snapshot immediately,
            // and from then on we advance using server-issued timestamps (no clock skew).
            sinceTimestamp = 0;
            consecutiveFailures = 0;
            connected = false;
            if (running)
            {
                // A new URL/key takes effect now, not after the pending 25 s poll returns.
                if (current != null) { restartPoll = true; current.Abort(); }
                return;
            }
            running = true;
            if (isActiveAndEnabled) loop = StartCoroutine(LongPollLoop());
        }

        public void Disconnect()
        {
            running = false;
            StopLoop();
        }

        /// <summary>
        /// Stops the coroutine and the request it was waiting on. StopCoroutine alone never
        /// runs the iterator's using/finally, so the UnityWebRequest (and its socket) leaked.
        /// </summary>
        private void StopLoop()
        {
            if (loop != null)
            {
                StopCoroutine(loop);
                loop = null;
            }
            if (current != null)
            {
                try { current.Abort(); } catch { /* already finished */ }
                current.Dispose();
                current = null;
            }
            connected = false;
            restartPoll = false;
        }

        // Unity stops coroutines when the component or its GameObject is disabled: resume.
        private void OnEnable()
        {
            if (running && loop == null) loop = StartCoroutine(LongPollLoop());
        }

        private void OnDisable() => StopLoop();

        private void OnDestroy() => Disconnect();

        /// <summary>retryDelaySeconds doubled per consecutive failure, capped, with ±20 % jitter.</summary>
        private float RetryDelay()
        {
            float baseDelay = Mathf.Max(0.5f, retryDelaySeconds);
            float cap = Mathf.Max(baseDelay, maxRetryDelaySeconds);
            float delay = baseDelay * Mathf.Pow(2f, Mathf.Clamp(consecutiveFailures - 1, 0, 6));
            // Jitter so several headsets do not hammer a restarted bridge in lock-step.
            return Mathf.Min(cap, delay) * UnityEngine.Random.Range(0.8f, 1.2f);
        }

        private IEnumerator LongPollLoop()
        {
            if (logEvents) Debug.Log($"[Push] Long-poll started → {serverUrl}/api/events");

            while (running)
            {
                float started = Time.realtimeSinceStartup;
                float wait = 0f;
                bool gotEvent = false;
                // Invariant culture: a Greek locale would format 2.5 as "2,5" in the query.
                // removeMissing=1: ask for full-state sync instead of the legacy clear-and-replace.
                string url = $"{serverUrl}/api/events?since={sinceTimestamp}&timeout={pollTimeoutSeconds.ToString(CultureInfo.InvariantCulture)}&removeMissing=1";
                var req = UnityWebRequest.Get(url);
                current = req;
                req.timeout = Mathf.CeilToInt(pollTimeoutSeconds) + 5;
                if (!string.IsNullOrEmpty(apiKey))
                    req.SetRequestHeader("Authorization", $"Bearer {apiKey}");
                yield return req.SendWebRequest();

                // StopLoop disposed it meanwhile (only possible if the coroutine outlived it).
                if (current != req) yield break;
                current = null;
                bool restarted = restartPoll;
                restartPoll = false;

                if (!restarted && req.result == UnityWebRequest.Result.Success)
                {
                    if (!connected)
                    {
                        connected = true;
                        OnConnected?.Invoke();
                    }
                    consecutiveFailures = 0;
                    string json = req.downloadHandler.text;
                    if (!string.IsNullOrEmpty(json) && json.Contains("coordinates"))
                    {
                        try
                        {
                            var evt = JsonUtility.FromJson<PushEvent>(json);
                            // timestamp > 0 marks a real event; a timeout response carries none.
                            // Empty coordinates with clearExisting is a valid event (clear-all).
                            if (evt != null && evt.timestamp > 0)
                            {
                                gotEvent = true;
                                sinceTimestamp = evt.timestamp;
                                // The bridge sends its whole store with removeMissing: markers it
                                // already has update in place (no re-created markers, no replayed
                                // animations) and only the ones it dropped go. An older bridge
                                // still sends clearExisting, which is honoured as before.
                                OnCoordinatesReceived?.Invoke(new CoordinateResponse
                                {
                                    coordinates = evt.coordinates ?? Array.Empty<CoordinateData>(),
                                    clearExisting = evt.clearExisting,
                                    removeMissing = evt.removeMissing
                                });
                                if (logEvents)
                                    Debug.Log($"[Push] Received {evt.coordinates?.Length ?? 0} markers (clear={evt.clearExisting}, removeMissing={evt.removeMissing})");
                            }
                        }
                        catch (Exception e)
                        {
                            OnError?.Invoke("Bad event JSON: " + e.Message);
                        }
                    }
                    // A server that answers at once without an event (not a long-poll server,
                    // a captive portal) must not be polled in a tight loop.
                    if (!gotEvent && Time.realtimeSinceStartup - started < 1f) wait = 1f;
                }
                else if (!running)
                {
                    // Aborted by Disconnect.
                }
                else if (restarted)
                {
                    // Aborted by Connect() with a new URL/key: poll again right away.
                }
                else
                {
                    consecutiveFailures++;
                    connected = false;
                    long code = req.responseCode;
                    if (code == 401 || code == 403)
                    {
                        OnError?.Invoke("Events endpoint unauthorized — set apiKey on WebSocketPushClient");
                        wait = Mathf.Max(10f, RetryDelay());
                    }
                    else if (code == 404 || code == 501)
                    {
                        // Server without events — back off hard
                        OnError?.Invoke("Events endpoint not available");
                        wait = Mathf.Max(10f, RetryDelay());
                    }
                    else
                    {
                        OnError?.Invoke(code > 0 ? $"HTTP {code}: {req.error}" : req.error);
                        wait = RetryDelay();
                    }
                    if (logEvents)
                        Debug.LogWarning($"[Push] Poll failed ({consecutiveFailures}x), retry in {wait:F1}s: {req.error}");
                }
                req.Dispose();

                // Realtime: a paused timeScale must not freeze the retry forever.
                if (wait > 0f) yield return new WaitForSecondsRealtime(wait);
                else yield return null;
            }
            loop = null;
        }

        /// <summary>Wire shape of a GET /api/events response from the Python bridge.</summary>
        [Serializable]
        private class PushEvent
        {
            public CoordinateData[] coordinates;
            public bool clearExisting;
            public bool removeMissing;
            public long timestamp;
            public bool timeout;
        }
    }
}
