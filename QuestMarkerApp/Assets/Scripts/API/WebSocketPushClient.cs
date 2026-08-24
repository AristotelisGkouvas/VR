using System;
using System.Collections;
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
        [SerializeField] private bool logEvents = true;

        public event Action<CoordinateResponse> OnCoordinatesReceived;
        public event Action OnConnected;
        public event Action<string> OnError;

        private bool running;
        private Coroutine loop;
        private long sinceTimestamp;

        public void Connect(string url = null, string key = null)
        {
            if (!string.IsNullOrEmpty(url)) serverUrl = url.TrimEnd('/');
            if (!string.IsNullOrEmpty(key)) apiKey = key;
            // 0 = "everything": the first poll returns the current snapshot immediately,
            // and from then on we advance using server-issued timestamps (no clock skew).
            sinceTimestamp = 0;
            if (running) return;
            running = true;
            loop = StartCoroutine(LongPollLoop());
        }

        public void Disconnect()
        {
            running = false;
            if (loop != null)
            {
                StopCoroutine(loop);
                loop = null;
            }
        }

        private void OnDestroy() => Disconnect();

        private IEnumerator LongPollLoop()
        {
            OnConnected?.Invoke();
            if (logEvents) Debug.Log($"[Push] Long-poll started → {serverUrl}/api/events");

            while (running)
            {
                string url = $"{serverUrl}/api/events?since={sinceTimestamp}&timeout={pollTimeoutSeconds}";
                using (var req = UnityWebRequest.Get(url))
                {
                    req.timeout = (int)pollTimeoutSeconds + 5;
                    if (!string.IsNullOrEmpty(apiKey))
                        req.SetRequestHeader("Authorization", $"Bearer {apiKey}");
                    yield return req.SendWebRequest();

                    if (req.result == UnityWebRequest.Result.Success)
                    {
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
                                    sinceTimestamp = evt.timestamp;
                                    OnCoordinatesReceived?.Invoke(new CoordinateResponse
                                    {
                                        coordinates = evt.coordinates ?? Array.Empty<CoordinateData>(),
                                        clearExisting = evt.clearExisting
                                    });
                                    if (logEvents)
                                        Debug.Log($"[Push] Received {evt.coordinates?.Length ?? 0} markers (clear={evt.clearExisting})");
                                }
                            }
                            catch (Exception e)
                            {
                                OnError?.Invoke(e.Message);
                            }
                        }
                    }
                    else if (req.responseCode == 401 || req.responseCode == 403)
                    {
                        OnError?.Invoke("Events endpoint unauthorized — set apiKey on WebSocketPushClient");
                        yield return new WaitForSeconds(10f);
                    }
                    else if (req.responseCode == 404 || req.responseCode == 501)
                    {
                        // Server without events — back off hard
                        OnError?.Invoke("Events endpoint not available");
                        yield return new WaitForSeconds(10f);
                    }
                    else
                    {
                        OnError?.Invoke(req.error);
                        yield return new WaitForSeconds(retryDelaySeconds);
                    }
                }

                yield return null;
            }
        }

        /// <summary>Wire shape of a GET /api/events response from the Python bridge.</summary>
        [Serializable]
        private class PushEvent
        {
            public CoordinateData[] coordinates;
            public bool clearExisting;
            public long timestamp;
            public bool timeout;
        }
    }
}
