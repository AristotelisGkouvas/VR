using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace QuestMarkerApp.API
{
    /// <summary>
    /// Service for handling API communication with the coordinate server
    /// Supports both polling and WebSocket connections
    /// </summary>
    public class CoordinateAPIService : MonoBehaviour
    {
        [Header("API Configuration")]
        [SerializeField] private string serverUrl = "http://192.168.1.100:5000";
        [SerializeField] private string apiEndpoint = "/api/coordinates";
        [SerializeField] private float pollingInterval = 1.0f;
        [SerializeField] private bool autoStartPolling = true;

        [Header("Authentication")]
        [SerializeField] private string apiKey = "";
        [SerializeField] private bool useAuthentication = false;

        [Header("Debug")]
        [SerializeField] private bool logRequests = true;

        // Events
        public event Action<CoordinateResponse> OnCoordinatesReceived;
        public event Action<string> OnConnectionError;
        public event Action OnConnectionEstablished;

        private bool isPolling = false;
        private Coroutine pollingCoroutine;

        public string ServerUrl
        {
            get => serverUrl;
            set => serverUrl = value;
        }

        private void Start()
        {
            if (autoStartPolling)
            {
                StartPolling();
            }
        }

        private void OnDestroy()
        {
            StopPolling();
        }

        /// <summary>
        /// Start polling the server for coordinate updates
        /// </summary>
        public void StartPolling()
        {
            if (isPolling) return;

            isPolling = true;
            pollingCoroutine = StartCoroutine(PollCoordinates());

            if (logRequests)
                Debug.Log($"[CoordinateAPI] Started polling at {serverUrl}{apiEndpoint}");
        }

        /// <summary>
        /// Stop polling the server
        /// </summary>
        public void StopPolling()
        {
            isPolling = false;
            if (pollingCoroutine != null)
            {
                StopCoroutine(pollingCoroutine);
                pollingCoroutine = null;
            }
        }

        /// <summary>
        /// Make a single request to fetch coordinates
        /// </summary>
        public void FetchCoordinatesOnce(Action<CoordinateResponse> callback = null)
        {
            StartCoroutine(FetchCoordinatesRequest(callback));
        }

        /// <summary>
        /// Send coordinates to the server
        /// </summary>
        public void SendCoordinates(CoordinateData[] coordinates, Action<bool> callback = null)
        {
            StartCoroutine(SendCoordinatesRequest(coordinates, callback));
        }

        private IEnumerator PollCoordinates()
        {
            // Initial connection test
            yield return StartCoroutine(TestConnection());

            while (isPolling)
            {
                yield return StartCoroutine(FetchCoordinatesRequest(null));
                yield return new WaitForSeconds(pollingInterval);
            }
        }

        private IEnumerator TestConnection()
        {
            string url = $"{serverUrl}/api/health";

            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.timeout = 5;
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    OnConnectionEstablished?.Invoke();
                    if (logRequests)
                        Debug.Log("[CoordinateAPI] Connection established successfully");
                }
                else
                {
                    if (logRequests)
                        Debug.LogWarning($"[CoordinateAPI] Health check failed, continuing anyway: {request.error}");
                }
            }
        }

        private IEnumerator FetchCoordinatesRequest(Action<CoordinateResponse> callback)
        {
            string url = $"{serverUrl}{apiEndpoint}";

            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                AddAuthHeaders(request);
                request.timeout = 10;

                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        string json = request.downloadHandler.text;
                        CoordinateResponse response = JsonUtility.FromJson<CoordinateResponse>(json);

                        if (logRequests)
                            Debug.Log($"[CoordinateAPI] Received {response.coordinates?.Length ?? 0} coordinates");

                        OnCoordinatesReceived?.Invoke(response);
                        callback?.Invoke(response);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[CoordinateAPI] Failed to parse response: {e.Message}");
                    }
                }
                else
                {
                    string error = $"Request failed: {request.error}";
                    OnConnectionError?.Invoke(error);

                    if (logRequests)
                        Debug.LogWarning($"[CoordinateAPI] {error}");
                }
            }
        }

        private IEnumerator SendCoordinatesRequest(CoordinateData[] coordinates, Action<bool> callback)
        {
            string url = $"{serverUrl}{apiEndpoint}";
            CoordinateRequest requestData = new CoordinateRequest { coordinates = coordinates };
            string jsonBody = JsonUtility.ToJson(requestData);

            using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                AddAuthHeaders(request);
                request.timeout = 10;

                yield return request.SendWebRequest();

                bool success = request.result == UnityWebRequest.Result.Success;

                if (logRequests)
                {
                    if (success)
                        Debug.Log($"[CoordinateAPI] Successfully sent {coordinates.Length} coordinates");
                    else
                        Debug.LogWarning($"[CoordinateAPI] Failed to send coordinates: {request.error}");
                }

                callback?.Invoke(success);
            }
        }

        private void AddAuthHeaders(UnityWebRequest request)
        {
            if (useAuthentication && !string.IsNullOrEmpty(apiKey))
            {
                request.SetRequestHeader("Authorization", $"Bearer {apiKey}");
            }
        }

        /// <summary>
        /// Configure the API service at runtime
        /// </summary>
        public void Configure(string url, string endpoint = null, float? interval = null)
        {
            serverUrl = url;
            if (endpoint != null) apiEndpoint = endpoint;
            if (interval.HasValue) pollingInterval = interval.Value;

            // Restart polling with new configuration
            if (isPolling)
            {
                StopPolling();
                StartPolling();
            }
        }
    }
}
