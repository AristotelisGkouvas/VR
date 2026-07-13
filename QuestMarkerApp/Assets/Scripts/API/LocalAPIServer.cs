using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEngine;

namespace QuestMarkerApp.API
{
    /// <summary>
    /// Local HTTP server running on the Quest 3 headset
    /// Allows external applications to send coordinates via POST requests
    /// </summary>
    public class LocalAPIServer : MonoBehaviour
    {
        [Header("Server Configuration")]
        [SerializeField] private int port = 8080;
        [SerializeField] private bool startOnAwake = true;
        [SerializeField] private bool logRequests = true;

        [Header("CORS Settings")]
        [SerializeField] private bool allowCORS = true;
        [SerializeField] private string allowedOrigins = "*";

        // Events
        public event Action<CoordinateResponse> OnCoordinatesReceived;
        public event Action<CalibrationRequest> OnCalibrationReceived;
        public event Action OnStartCalibrationMode;
        public event Action<string> OnServerError;
        public event Action OnServerStarted;
        public event Action OnServerStopped;

        private HttpListener httpListener;
        private Thread listenerThread;
        private bool isRunning = false;
        private Queue<CoordinateResponse> pendingResponses = new Queue<CoordinateResponse>();
        private Queue<CalibrationRequest> pendingCalibrations = new Queue<CalibrationRequest>();
        private Queue<Action> pendingActions = new Queue<Action>();
        private object lockObject = new object();

        public int Port => port;
        public bool IsRunning => isRunning;

        private void Awake()
        {
            if (startOnAwake)
            {
                StartServer();
            }
        }

        private void Update()
        {
            // Process pending responses on main thread
            lock (lockObject)
            {
                while (pendingResponses.Count > 0)
                {
                    CoordinateResponse response = pendingResponses.Dequeue();
                    OnCoordinatesReceived?.Invoke(response);
                }
                
                while (pendingCalibrations.Count > 0)
                {
                    CalibrationRequest calibration = pendingCalibrations.Dequeue();
                    OnCalibrationReceived?.Invoke(calibration);
                }
                
                while (pendingActions.Count > 0)
                {
                    Action action = pendingActions.Dequeue();
                    action?.Invoke();
                }
            }
        }

        private void OnDestroy()
        {
            StopServer();
        }

        private void OnApplicationQuit()
        {
            StopServer();
        }

        /// <summary>
        /// Start the local HTTP server
        /// </summary>
        public void StartServer()
        {
            if (isRunning) return;

            try
            {
                httpListener = new HttpListener();
                httpListener.Prefixes.Add($"http://*:{port}/");
                httpListener.Start();
                isRunning = true;

                listenerThread = new Thread(ListenLoop);
                listenerThread.IsBackground = true;
                listenerThread.Start();

                OnServerStarted?.Invoke();
                Debug.Log($"[LocalAPIServer] Server started on port {port}");
            }
            catch (Exception e)
            {
                string error = $"Failed to start server: {e.Message}";
                OnServerError?.Invoke(error);
                Debug.LogError($"[LocalAPIServer] {error}");
            }
        }

        /// <summary>
        /// Stop the local HTTP server
        /// </summary>
        public void StopServer()
        {
            if (!isRunning) return;

            isRunning = false;

            try
            {
                httpListener?.Stop();
                httpListener?.Close();
                listenerThread?.Join(1000);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LocalAPIServer] Error stopping server: {e.Message}");
            }

            OnServerStopped?.Invoke();
            Debug.Log("[LocalAPIServer] Server stopped");
        }

        private void ListenLoop()
        {
            while (isRunning)
            {
                try
                {
                    HttpListenerContext context = httpListener.GetContext();
                    ThreadPool.QueueUserWorkItem(ProcessRequest, context);
                }
                catch (Exception e)
                {
                    if (isRunning)
                    {
                        Debug.LogError($"[LocalAPIServer] Listen error: {e.Message}");
                    }
                }
            }
        }

        private void ProcessRequest(object state)
        {
            HttpListenerContext context = (HttpListenerContext)state;
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;

            try
            {
                // Add CORS headers
                if (allowCORS)
                {
                    response.Headers.Add("Access-Control-Allow-Origin", allowedOrigins);
                    response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                    response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization");
                }

                // Handle preflight
                if (request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = 200;
                    response.Close();
                    return;
                }

                string path = request.Url.AbsolutePath.ToLower();

                if (logRequests)
                {
                    Debug.Log($"[LocalAPIServer] {request.HttpMethod} {path}");
                }

                // Route requests
                if (path == "/api/coordinates" && request.HttpMethod == "POST")
                {
                    HandleCoordinatesPost(request, response);
                }
                else if (path == "/api/coordinates" && request.HttpMethod == "GET")
                {
                    HandleCoordinatesGet(response);
                }
                else if (path == "/api/health" || path == "/health")
                {
                    HandleHealthCheck(response);
                }
                else if (path == "/api/clear" && request.HttpMethod == "POST")
                {
                    HandleClearMarkers(response);
                }
                else if (path == "/api/calibrate" && request.HttpMethod == "POST")
                {
                    HandleCalibrate(request, response);
                }
                else if (path == "/api/calibrate/start" && request.HttpMethod == "POST")
                {
                    HandleStartCalibration(response);
                }
                else if (path == "/api/calibrate/status" && request.HttpMethod == "GET")
                {
                    HandleCalibrationStatus(response);
                }
                else
                {
                    SendResponse(response, 404, "{\"error\": \"Not found\"}");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[LocalAPIServer] Request processing error: {e.Message}");
                SendResponse(response, 500, $"{{\"error\": \"{e.Message}\"}}");
            }
        }

        private void HandleCoordinatesPost(HttpListenerRequest request, HttpListenerResponse response)
        {
            try
            {
                using (StreamReader reader = new StreamReader(request.InputStream, request.ContentEncoding))
                {
                    string body = reader.ReadToEnd();

                    if (logRequests)
                    {
                        Debug.Log($"[LocalAPIServer] Received body: {body}");
                    }

                    CoordinateResponse coordResponse = JsonUtility.FromJson<CoordinateResponse>(body);

                    // Queue for main thread processing
                    lock (lockObject)
                    {
                        pendingResponses.Enqueue(coordResponse);
                    }

                    SendResponse(response, 200, "{\"success\": true, \"message\": \"Coordinates received\"}");
                }
            }
            catch (Exception e)
            {
                SendResponse(response, 400, $"{{\"error\": \"Invalid JSON: {e.Message}\"}}");
            }
        }

        private void HandleCoordinatesGet(HttpListenerResponse response)
        {
            // Return current status
            string json = "{\"status\": \"ready\", \"message\": \"Send POST request with coordinates\"}";
            SendResponse(response, 200, json);
        }

        private void HandleHealthCheck(HttpListenerResponse response)
        {
            string json = "{\"status\": \"healthy\", \"device\": \"Meta Quest 3\", \"version\": \"1.0.0\"}";
            SendResponse(response, 200, json);
        }

        private void HandleClearMarkers(HttpListenerResponse response)
        {
            // Send clear command
            CoordinateResponse clearResponse = new CoordinateResponse
            {
                coordinates = new CoordinateData[0],
                clearExisting = true
            };

            lock (lockObject)
            {
                pendingResponses.Enqueue(clearResponse);
            }

            SendResponse(response, 200, "{\"success\": true, \"message\": \"Markers cleared\"}");
        }

        private void HandleCalibrate(HttpListenerRequest request, HttpListenerResponse response)
        {
            try
            {
                using (StreamReader reader = new StreamReader(request.InputStream, request.ContentEncoding))
                {
                    string body = reader.ReadToEnd();
                    
                    if (logRequests)
                    {
                        Debug.Log($"[LocalAPIServer] Calibration data: {body}");
                    }
                    
                    CalibrationRequest calibrationRequest = JsonUtility.FromJson<CalibrationRequest>(body);
                    
                    lock (lockObject)
                    {
                        pendingCalibrations.Enqueue(calibrationRequest);
                    }
                    
                    SendResponse(response, 200, "{\"success\": true, \"message\": \"Calibration data received\"}");
                }
            }
            catch (Exception e)
            {
                SendResponse(response, 400, $"{{\"error\": \"Invalid calibration JSON: {e.Message}\"}}");
            }
        }
        
        private void HandleStartCalibration(HttpListenerResponse response)
        {
            lock (lockObject)
            {
                pendingActions.Enqueue(() => OnStartCalibrationMode?.Invoke());
            }
            
            SendResponse(response, 200, "{\"success\": true, \"message\": \"Calibration mode started. Point controller at known location and press trigger.\"}");
        }
        
        private void HandleCalibrationStatus(HttpListenerResponse response)
        {
            // This will be updated with actual calibration status
            string json = "{\"status\": \"ready\", \"message\": \"Send POST to /api/calibrate with real coordinates\", \"calibrated\": false}";
            SendResponse(response, 200, json);
        }

        private void SendResponse(HttpListenerResponse response, int statusCode, string content)
        {
            try
            {
                response.StatusCode = statusCode;
                response.ContentType = "application/json";

                byte[] buffer = Encoding.UTF8.GetBytes(content);
                response.ContentLength64 = buffer.Length;

                using (Stream output = response.OutputStream)
                {
                    output.Write(buffer, 0, buffer.Length);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[LocalAPIServer] Response error: {e.Message}");
            }
        }

        /// <summary>
        /// Get the URLs to connect to this server
        /// </summary>
        public string[] GetServerUrls()
        {
            List<string> urls = new List<string>();

            foreach (var ip in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var addr in ip.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        urls.Add($"http://{addr.Address}:{port}");
                    }
                }
            }

            return urls.ToArray();
        }
    }
}
