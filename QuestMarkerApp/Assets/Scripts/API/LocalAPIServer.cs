using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEngine;
using QuestMarkerApp.Core;
using QuestMarkerApp.Markers;

namespace QuestMarkerApp.API
{
    /// <summary>
    /// Local HTTP server on the Quest with auth, rate limit, logs, sessions, calibration, anchors.
    /// </summary>
    public class LocalAPIServer : MonoBehaviour
    {
        private const int MaxBodyBytes = 512 * 1024;
        private const string AppVersion = "1.2.0";

        [Header("Server Configuration")]
        [SerializeField] private int port = 8080;
        [SerializeField] private bool startOnAwake = true;
        [SerializeField] private bool logRequests = true;
        [SerializeField] private bool enableDiscoveryBeacon = true;

        [Header("Security")]
        [SerializeField] private ApiSecurityConfig securityConfig = new ApiSecurityConfig();

        [Header("CORS")]
        [SerializeField] private bool allowCORS = true;
        [SerializeField] private string allowedOrigins = "*";

        [Header("Optional Refs")]
        [SerializeField] private MarkerManager markerManager;
        [SerializeField] private CoordinateCalibration calibrationSystem;
        [SerializeField] private SessionManager sessionManager;
        [SerializeField] private SpatialAnchorManager spatialAnchorManager;
        [SerializeField] private NetworkDiscovery networkDiscovery;

        public event Action<CoordinateResponse> OnCoordinatesReceived;
        public event Action<CalibrationRequest> OnCalibrationReceived;
        public event Action OnStartCalibrationMode;
        public event Action<string> OnDeleteMarkerRequested;
        public event Action OnResetCalibrationRequested;
        public event Action OnUndoRequested;
        public event Action<string> OnSessionImportJson;
        public event Action OnSessionExportRequested;
        public event Action<GPSReference> OnGpsOriginSet;
        public event Action<string> OnServerError;
        public event Action OnServerStarted;
        public event Action OnServerStopped;

        private HttpListener httpListener;
        private Thread listenerThread;
        private volatile bool isRunning;
        private readonly Queue<CoordinateResponse> pendingResponses = new Queue<CoordinateResponse>();
        private readonly Queue<CalibrationRequest> pendingCalibrations = new Queue<CalibrationRequest>();
        private readonly Queue<Action> pendingActions = new Queue<Action>();
        private readonly object lockObject = new object();
        private long requestCount;
        private DateTime startedAtUtc;
        private ApiSecurity security;
        private readonly RequestLog requestLog = new RequestLog(100);
        private string lastExportedSessionJson = "";

        public int Port => port;
        public bool IsRunning => isRunning;
        public RequestLog Logs => requestLog;

        private void Awake()
        {
            security = new ApiSecurity(securityConfig);
            if (markerManager == null) markerManager = FindObjectOfType<MarkerManager>();
            if (calibrationSystem == null) calibrationSystem = FindObjectOfType<CoordinateCalibration>();
            if (sessionManager == null) sessionManager = FindObjectOfType<SessionManager>();
            if (spatialAnchorManager == null) spatialAnchorManager = FindObjectOfType<SpatialAnchorManager>();
            if (networkDiscovery == null) networkDiscovery = GetComponent<NetworkDiscovery>() ?? gameObject.AddComponent<NetworkDiscovery>();

            if (startOnAwake) StartServer();
        }

        private void Update()
        {
            lock (lockObject)
            {
                while (pendingResponses.Count > 0)
                    OnCoordinatesReceived?.Invoke(pendingResponses.Dequeue());
                while (pendingCalibrations.Count > 0)
                    OnCalibrationReceived?.Invoke(pendingCalibrations.Dequeue());
                while (pendingActions.Count > 0)
                    pendingActions.Dequeue()?.Invoke();
            }
        }

        private void OnDestroy() => StopServer();
        private void OnApplicationQuit() => StopServer();

        public void StartServer()
        {
            if (isRunning) return;
            try
            {
                httpListener = new HttpListener();
                httpListener.Prefixes.Add($"http://*:{port}/");
                httpListener.Start();
                isRunning = true;
                startedAtUtc = DateTime.UtcNow;
                requestCount = 0;

                listenerThread = new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name = "QuestMarkerLocalAPI"
                };
                listenerThread.Start();

                if (enableDiscoveryBeacon && networkDiscovery != null)
                {
                    networkDiscovery.Configure(port, SystemInfo.deviceName);
                    networkDiscovery.StartBeacon();
                }

                OnServerStarted?.Invoke();
                UnityEngine.Debug.Log($"[LocalAPIServer] v{AppVersion} on port {port}");
            }
            catch (Exception e)
            {
                string error = $"Failed to start server: {e.Message}";
                OnServerError?.Invoke(error);
                UnityEngine.Debug.LogError($"[LocalAPIServer] {error}");
            }
        }

        public void StopServer()
        {
            if (!isRunning) return;
            isRunning = false;
            try
            {
                networkDiscovery?.StopBeacon();
                httpListener?.Stop();
                httpListener?.Close();
                listenerThread?.Join(1000);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[LocalAPIServer] Error stopping: {e.Message}");
            }
            finally
            {
                httpListener = null;
                listenerThread = null;
            }
            OnServerStopped?.Invoke();
        }

        public void SetLastExportedSession(string json) => lastExportedSessionJson = json ?? "";

        public void BindComponents(MarkerManager markers, CoordinateCalibration calibration,
            SessionManager sessions = null, SpatialAnchorManager anchors = null)
        {
            if (markers != null) markerManager = markers;
            if (calibration != null) calibrationSystem = calibration;
            if (sessions != null) sessionManager = sessions;
            if (anchors != null) spatialAnchorManager = anchors;
        }

        private void ListenLoop()
        {
            while (isRunning)
            {
                try
                {
                    var context = httpListener.GetContext();
                    Interlocked.Increment(ref requestCount);
                    ThreadPool.QueueUserWorkItem(ProcessRequest, context);
                }
                catch (HttpListenerException) { if (!isRunning) break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception e)
                {
                    if (isRunning)
                        UnityEngine.Debug.LogError($"[LocalAPIServer] Listen error: {e.Message}");
                }
            }
        }

        private void ProcessRequest(object state)
        {
            var context = (HttpListenerContext)state;
            var request = context.Request;
            var response = context.Response;
            var sw = Stopwatch.StartNew();
            int status = 500;
            string path = "/";

            try
            {
                if (allowCORS)
                {
                    response.Headers.Add("Access-Control-Allow-Origin", allowedOrigins);
                    response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS");
                    response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization, X-Api-Key");
                }

                if (request.HttpMethod == "OPTIONS")
                {
                    status = 200;
                    response.StatusCode = 200;
                    response.Close();
                    return;
                }

                path = request.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant();
                if (string.IsNullOrEmpty(path)) path = "/";

                string client = request.RemoteEndPoint?.Address?.ToString() ?? "unknown";

                if (!security.CheckRateLimit(client))
                {
                    status = 429;
                    SendJson(response, 429, "{\"error\":\"Rate limit exceeded\"}");
                    return;
                }

                if (security.IsAuthRequired(path))
                {
                    string key = request.Headers["X-Api-Key"];
                    string auth = request.Headers["Authorization"];
                    if (!security.ValidateApiKey(key, auth))
                    {
                        status = 401;
                        SendJson(response, 401, "{\"error\":\"Unauthorized — provide X-Api-Key or Bearer token\"}");
                        return;
                    }
                }

                if (logRequests)
                    UnityEngine.Debug.Log($"[LocalAPIServer] {request.HttpMethod} {path}");

                status = Route(request, response, path);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError($"[LocalAPIServer] Error: {e.Message}");
                status = 500;
                try { SendJson(response, 500, "{\"error\":\"" + EscapeJson(e.Message) + "\"}"); } catch { /* */ }
            }
            finally
            {
                sw.Stop();
                requestLog.Add(request.HttpMethod, path, status, (int)sw.ElapsedMilliseconds,
                    request.RemoteEndPoint?.Address?.ToString());
            }
        }

        private int Route(HttpListenerRequest request, HttpListenerResponse response, string path)
        {
            string method = request.HttpMethod;

            if (path == "/api/coordinates" && method == "POST") return HandleCoordinatesPost(request, response);
            if (path == "/api/coordinates" && method == "GET") return HandleCoordinatesGet(response);
            if (path.StartsWith("/api/coordinates/") && method == "DELETE")
            {
                // Extract the id from the original path — `path` was lowercased for routing,
                // but marker ids are case-sensitive.
                string rawPath = request.Url.AbsolutePath.TrimEnd('/');
                string id = Uri.UnescapeDataString(rawPath.Substring(rawPath.LastIndexOf('/') + 1));
                return HandleDeleteMarker(id, response);
            }
            if (path == "/api/health" || path == "/health") return HandleHealthCheck(response);
            if (path == "/api/status" && method == "GET") return HandleStatus(response);
            if (path == "/api/logs" && method == "GET") { SendJson(response, 200, requestLog.ToJson()); return 200; }
            if (path == "/api/clear" && method == "POST") return HandleClearMarkers(response);
            if (path == "/api/undo" && method == "POST") return HandleUndo(response);
            if (path == "/api/calibrate" && method == "POST") return HandleCalibrate(request, response);
            if (path == "/api/calibrate/start" && method == "POST") return HandleStartCalibration(response);
            if (path == "/api/calibrate/status" && method == "GET") return HandleCalibrationStatus(response);
            if (path == "/api/calibrate/reset" && method == "POST") return HandleResetCalibration(response);
            if (path == "/api/session" && method == "GET") return HandleSessionExport(response);
            if (path == "/api/session" && method == "POST") return HandleSessionImport(request, response);
            if (path == "/api/session/new" && method == "POST") return HandleSessionNew(request, response);
            if (path == "/api/gps-origin" && method == "POST") return HandleGpsOrigin(request, response);
            if (path == "/api/anchors" && method == "GET") return HandleAnchors(response);
            if (path == "/api/layer" && method == "POST") return HandleLayer(request, response);

            SendJson(response, 404, "{\"error\":\"Not found\",\"path\":\"" + EscapeJson(path) + "\"}");
            return 404;
        }

        private int HandleCoordinatesPost(HttpListenerRequest request, HttpListenerResponse response)
        {
            try
            {
                string body = ReadBody(request);
                if (body == null) { SendJson(response, 413, "{\"error\":\"Request body too large\"}"); return 413; }
                if (string.IsNullOrWhiteSpace(body)) { SendJson(response, 400, "{\"error\":\"Empty body\"}"); return 400; }

                var coordResponse = JsonUtility.FromJson<CoordinateResponse>(body);
                if (coordResponse == null) { SendJson(response, 400, "{\"error\":\"Invalid JSON\"}"); return 400; }

                int count = coordResponse.coordinates?.Length ?? 0;
                lock (lockObject) pendingResponses.Enqueue(coordResponse);
                SendJson(response, 200, "{\"success\":true,\"message\":\"Coordinates received\",\"count\":" + count + "}");
                return 200;
            }
            catch (Exception e)
            {
                SendJson(response, 400, "{\"error\":\"Invalid JSON: " + EscapeJson(e.Message) + "\"}");
                return 400;
            }
        }

        /// <summary>
        /// Run a producer on the Unity main thread (via the Update pump) and block this
        /// worker thread until it completes. Marker/calibration/session state must not be
        /// read from HttpListener threads. Falls back after timeoutMs (e.g. app paused).
        /// </summary>
        private string RunOnMainThread(Func<string> produce, string fallback, int timeoutMs = 1000)
        {
            var done = new ManualResetEventSlim(false);
            string result = fallback;
            lock (lockObject)
            {
                pendingActions.Enqueue(() =>
                {
                    try { result = produce(); }
                    catch (Exception e) { UnityEngine.Debug.LogError($"[LocalAPIServer] Main-thread read failed: {e.Message}"); }
                    finally { done.Set(); }
                });
            }
            return done.Wait(timeoutMs) ? result : fallback;
        }

        private int HandleCoordinatesGet(HttpListenerResponse response)
        {
            const string empty = "{\"status\":\"ready\",\"markerCount\":0,\"coordinates\":[]}";
            string json = markerManager != null
                ? RunOnMainThread(() => markerManager.ToCoordinatesJson(), empty)
                : empty;
            SendJson(response, 200, json);
            return 200;
        }

        private int HandleDeleteMarker(string id, HttpListenerResponse response)
        {
            if (string.IsNullOrEmpty(id)) { SendJson(response, 400, "{\"error\":\"Missing marker id\"}"); return 400; }
            string captured = id;
            lock (lockObject) pendingActions.Enqueue(() => OnDeleteMarkerRequested?.Invoke(captured));
            SendJson(response, 200, "{\"success\":true,\"id\":\"" + EscapeJson(id) + "\"}");
            return 200;
        }

        private int HandleHealthCheck(HttpListenerResponse response)
        {
            double uptime = isRunning ? (DateTime.UtcNow - startedAtUtc).TotalSeconds : 0;
            int markers = markerManager != null ? markerManager.ActiveMarkerCount : 0;
            bool calibrated = calibrationSystem != null && calibrationSystem.IsCalibrated;
            string json =
                "{\"status\":\"healthy\",\"device\":\"Meta Quest 3\",\"version\":\"" + AppVersion + "\"," +
                "\"uptimeSeconds\":" + uptime.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "," +
                "\"requestCount\":" + Interlocked.Read(ref requestCount) + "," +
                "\"markerCount\":" + markers + "," +
                "\"calibrated\":" + (calibrated ? "true" : "false") + "}";
            SendJson(response, 200, json);
            return 200;
        }

        private int HandleStatus(HttpListenerResponse response)
        {
            string fallback =
                "{\"serverRunning\":" + (isRunning ? "true" : "false") + "," +
                "\"port\":" + port + ",\"version\":\"" + AppVersion + "\"}";
            string json = RunOnMainThread(() =>
            {
                int markers = markerManager != null ? markerManager.ActiveMarkerCount : 0;
                int max = markerManager != null ? markerManager.MaxMarkers : 0;
                bool calibrated = calibrationSystem != null && calibrationSystem.IsCalibrated;
                bool calibrating = calibrationSystem != null && calibrationSystem.IsCalibrating;
                string session = sessionManager != null ? sessionManager.SessionName : "";
                return
                    "{\"serverRunning\":" + (isRunning ? "true" : "false") + "," +
                    "\"port\":" + port + ",\"version\":\"" + AppVersion + "\"," +
                    "\"markerCount\":" + markers + ",\"maxMarkers\":" + max + "," +
                    "\"calibrated\":" + (calibrated ? "true" : "false") + "," +
                    "\"calibrating\":" + (calibrating ? "true" : "false") + "," +
                    "\"session\":\"" + EscapeJson(session) + "\"}";
            }, fallback);
            SendJson(response, 200, json);
            return 200;
        }

        private int HandleClearMarkers(HttpListenerResponse response)
        {
            lock (lockObject)
            {
                pendingResponses.Enqueue(new CoordinateResponse
                {
                    coordinates = Array.Empty<CoordinateData>(),
                    clearExisting = true
                });
            }
            SendJson(response, 200, "{\"success\":true,\"message\":\"Markers cleared\"}");
            return 200;
        }

        private int HandleUndo(HttpListenerResponse response)
        {
            lock (lockObject) pendingActions.Enqueue(() => OnUndoRequested?.Invoke());
            SendJson(response, 200, "{\"success\":true,\"message\":\"Undo requested\"}");
            return 200;
        }

        private int HandleCalibrate(HttpListenerRequest request, HttpListenerResponse response)
        {
            try
            {
                string body = ReadBody(request);
                if (body == null) { SendJson(response, 413, "{\"error\":\"Request body too large\"}"); return 413; }
                var cal = JsonUtility.FromJson<CalibrationRequest>(body);
                if (cal == null) { SendJson(response, 400, "{\"error\":\"Invalid calibration JSON\"}"); return 400; }
                lock (lockObject) pendingCalibrations.Enqueue(cal);
                SendJson(response, 200, "{\"success\":true,\"message\":\"Calibration data received\"}");
                return 200;
            }
            catch (Exception e)
            {
                SendJson(response, 400, "{\"error\":\"" + EscapeJson(e.Message) + "\"}");
                return 400;
            }
        }

        private int HandleStartCalibration(HttpListenerResponse response)
        {
            lock (lockObject) pendingActions.Enqueue(() => OnStartCalibrationMode?.Invoke());
            SendJson(response, 200, "{\"success\":true,\"message\":\"Calibration mode started\"}");
            return 200;
        }

        private int HandleCalibrationStatus(HttpListenerResponse response)
        {
            if (calibrationSystem == null)
            {
                SendJson(response, 200, "{\"status\":\"unknown\",\"calibrated\":false}");
                return 200;
            }
            string json = RunOnMainThread(() =>
            {
                var s = calibrationSystem.GetStatus();
                float residual = calibrationSystem.LastResidualRms;
                return
                    "{\"status\":\"" + (s.isCalibrated ? "calibrated" : (s.isCalibrating ? "calibrating" : "ready")) + "\"," +
                    "\"calibrated\":" + (s.isCalibrated ? "true" : "false") + "," +
                    "\"calibrating\":" + (s.isCalibrating ? "true" : "false") + "," +
                    "\"capturedPoints\":" + s.capturedPoints + "," +
                    "\"pointsWithRealCoords\":" + s.pointsWithRealCoords + "," +
                    "\"pointsUsed\":" + s.pointsUsed + "," +
                    "\"minimumRequired\":" + s.minimumRequired + "," +
                    "\"step\":\"" + EscapeJson(calibrationSystem.CurrentStepMessage) + "\"," +
                    "\"residualRmsMeters\":" + residual.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "}";
            }, "{\"status\":\"unknown\",\"calibrated\":false}");
            SendJson(response, 200, json);
            return 200;
        }

        private int HandleResetCalibration(HttpListenerResponse response)
        {
            lock (lockObject) pendingActions.Enqueue(() => OnResetCalibrationRequested?.Invoke());
            SendJson(response, 200, "{\"success\":true}");
            return 200;
        }

        private int HandleSessionExport(HttpListenerResponse response)
        {
            string json = RunOnMainThread(() =>
            {
                OnSessionExportRequested?.Invoke(); // lets the controller refresh lastExportedSessionJson
                return sessionManager != null ? sessionManager.ExportJson() : lastExportedSessionJson;
            }, lastExportedSessionJson);
            if (string.IsNullOrEmpty(json)) json = "{\"markers\":[]}";
            SendJson(response, 200, json);
            return 200;
        }

        private int HandleSessionImport(HttpListenerRequest request, HttpListenerResponse response)
        {
            string body = ReadBody(request);
            if (body == null) { SendJson(response, 413, "{\"error\":\"body too large\"}"); return 413; }
            string captured = body;
            lock (lockObject) pendingActions.Enqueue(() => OnSessionImportJson?.Invoke(captured));
            SendJson(response, 200, "{\"success\":true,\"message\":\"Session import queued\"}");
            return 200;
        }

        private int HandleSessionNew(HttpListenerRequest request, HttpListenerResponse response)
        {
            string name = "session";
            try
            {
                string body = ReadBody(request);
                if (!string.IsNullOrEmpty(body))
                {
                    var parsed = JsonUtility.FromJson<SessionNewBody>(body);
                    if (parsed != null && !string.IsNullOrEmpty(parsed.name))
                        name = parsed.name;
                }
            }
            catch { /* keep default */ }

            string captured = name;
            lock (lockObject)
            {
                pendingActions.Enqueue(() =>
                {
                    if (sessionManager != null) sessionManager.NewSession(captured);
                });
            }
            SendJson(response, 200, "{\"success\":true,\"name\":\"" + EscapeJson(name) + "\"}");
            return 200;
        }

        private int HandleGpsOrigin(HttpListenerRequest request, HttpListenerResponse response)
        {
            try
            {
                string body = ReadBody(request);
                var gps = JsonUtility.FromJson<GPSReference>(body);
                if (gps == null) { SendJson(response, 400, "{\"error\":\"Invalid GPS origin\"}"); return 400; }
                lock (lockObject) pendingActions.Enqueue(() => OnGpsOriginSet?.Invoke(gps));
                SendJson(response, 200, "{\"success\":true}");
                return 200;
            }
            catch (Exception e)
            {
                SendJson(response, 400, "{\"error\":\"" + EscapeJson(e.Message) + "\"}");
                return 400;
            }
        }

        private int HandleAnchors(HttpListenerResponse response)
        {
            const string none = "{\"hasReference\":false}";
            string json = spatialAnchorManager != null
                ? RunOnMainThread(() => spatialAnchorManager.GetStatusJson(), none)
                : none;
            SendJson(response, 200, json);
            return 200;
        }

        private int HandleLayer(HttpListenerRequest request, HttpListenerResponse response)
        {
            try
            {
                string body = ReadBody(request) ?? "";
                // {"layer":"hazards"} empty = all
                string layer = "";
                if (!string.IsNullOrEmpty(body))
                {
                    var parsed = JsonUtility.FromJson<LayerBody>(body);
                    if (parsed?.layer != null) layer = parsed.layer;
                }
                string captured = layer;
                lock (lockObject)
                {
                    pendingActions.Enqueue(() =>
                    {
                        if (markerManager != null)
                            markerManager.VisibleLayerFilter = captured;
                    });
                }
                SendJson(response, 200, "{\"success\":true,\"layer\":\"" + EscapeJson(layer) + "\"}");
                return 200;
            }
            catch (Exception e)
            {
                SendJson(response, 400, "{\"error\":\"" + EscapeJson(e.Message) + "\"}");
                return 400;
            }
        }

        private string ReadBody(HttpListenerRequest request)
        {
            if (request.ContentLength64 > MaxBodyBytes) return null;
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
            {
                char[] buffer = new char[4096];
                var sb = new StringBuilder();
                int read, total = 0;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > MaxBodyBytes) return null;
                    sb.Append(buffer, 0, read);
                }
                return sb.ToString();
            }
        }

        private void SendJson(HttpListenerResponse response, int statusCode, string content)
        {
            try
            {
                response.StatusCode = statusCode;
                response.ContentType = "application/json; charset=utf-8";
                response.Headers.Add("Cache-Control", "no-store");
                byte[] buffer = Encoding.UTF8.GetBytes(content ?? "{}");
                response.ContentLength64 = buffer.Length;
                using (Stream output = response.OutputStream)
                    output.Write(buffer, 0, buffer.Length);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError($"[LocalAPIServer] Response error: {e.Message}");
            }
            finally
            {
                try { response.Close(); } catch { /* */ }
            }
        }

        private static string EscapeJson(string s) => JsonText.Escape(s);

        [Serializable]
        private class SessionNewBody { public string name; }

        [Serializable]
        private class LayerBody { public string layer; }

        public string[] GetServerUrls()
        {
            var urls = new List<string>();
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            string ip = addr.Address.ToString();
                            if (ip.StartsWith("127.")) continue;
                            urls.Add($"http://{ip}:{port}");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[LocalAPIServer] Network enum failed: {e.Message}");
            }
            if (urls.Count == 0) urls.Add($"http://127.0.0.1:{port}");
            return urls.ToArray();
        }
    }
}
