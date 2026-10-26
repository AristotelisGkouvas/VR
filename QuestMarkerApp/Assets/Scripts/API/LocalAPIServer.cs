using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
        private const int BodyReadTimeoutMs = 5000;
        private const string AppVersion = "1.2.0";
        // Design models (OBJ/DXF) stream to disk, never through ReadBody: a site model can be
        // hundreds of megabytes and the PC tool sends it over Wi-Fi.
        private const long MaxModelBytes = 300L * 1024 * 1024;
        // 300 MB in a fixed 120 s needed a steady 20 Mbit/s; site Wi-Fi often has less. A
        // stalled client is caught by the idle limit instead, the overall one is a backstop.
        private const int ModelUploadTimeoutMs = 15 * 60 * 1000;
        private const int ModelUploadIdleMs = 30000;
        private const int ModelChunkBytes = 64 * 1024;
        private static readonly string[] ModelFormats = { "obj", "dxf" };
        // A textured model arrives as one zip (OBJ + MTL + images) and is unpacked into
        // models/<name>/. The caps keep a hostile or broken archive from filling the headset:
        // a zip bomb stops at 1 GB of output, a million tiny entries at 500.
        private const string PackageFormat = "zip";
        private const long MaxPackageExtractBytes = 1024L * 1024 * 1024;
        private const int MaxPackageEntries = 500;
        private static readonly string[] PackageExtensions = { ".obj", ".mtl", ".png", ".jpg", ".jpeg" };
        // Each upload can hold 300 MB of temp file plus 1 GB unpacked and a worker for 15 min:
        // a third one at once is a 503, so a client loop cannot fill the disk or the pool.
        private const int MaxConcurrentModelUploads = 2;
        // An answer sent without reading the body (401, 429, preflight, health) reads at most
        // this much for at most this long first; see DiscardUnreadBody.
        private const int RejectDrainMs = 1000;

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
        [SerializeField] private ProjectAlignment projectAlignment;

        public event Action<CoordinateResponse> OnCoordinatesReceived;
        public event Action<CalibrationRequest> OnCalibrationReceived;
        public event Action OnStartCalibrationMode;
        public event Action<string> OnDeleteMarkerRequested;
        public event Action OnResetCalibrationRequested;
        public event Action OnUndoRequested;
        public event Action<string> OnSessionImportJson;
        public event Action OnSessionExportRequested;
        public event Action<GPSReference> OnGpsOriginSet;
        public event Action<ProjectRequest> OnProjectReceived;
        public event Action OnProjectResetRequested;
        /// <summary>Full path of a model file just written to the models directory (sidecar beside it).</summary>
        public event Action<string> OnModelReceived;
        /// <summary>Display name of a model whose file and sidecar were just deleted.</summary>
        public event Action<string> OnModelDeleted;
        public event Action<string> OnServerError;
        public event Action OnServerStarted;
        public event Action OnServerStopped;

        private HttpListener httpListener;
        private Thread listenerThread;
        private volatile bool isRunning;
        // Set by the listen thread when it gives up on its own (network gone while in the
        // foreground); Update then tears the listener down and restarts it with back-off.
        private volatile bool listenerFailed;
        private readonly object listenerStateLock = new object();
        private bool restartPending;          // main thread only
        private float nextRestartAt;          // unscaled time
        private float restartDelay;           // seconds; 0 = no recent failure
        private float startedAtRealtime;
        private const float MinRestartDelay = 2f, MaxRestartDelay = 60f;
        private readonly Queue<CoordinateResponse> pendingResponses = new Queue<CoordinateResponse>();
        private const int MaxPendingResponses = 32;
        private readonly Queue<CalibrationRequest> pendingCalibrations = new Queue<CalibrationRequest>();
        private readonly Queue<Action> pendingActions = new Queue<Action>();
        // Asleep (headset off) nothing drains the queue: past this, commands get 503, not a backlog.
        private const int MaxPendingActions = 256;
        private readonly object lockObject = new object();
        // Serialises replace/delete of model files so two uploads of one name cannot interleave.
        private readonly object modelLock = new object();
        private int activeModelUploads;
        private const string TooLargeJson = "{\"error\":\"Request body too large (max 512 KB)\"}";
        private static readonly HashSet<string> KnownPaths = new HashSet<string>(StringComparer.Ordinal)
        {
            "/api/coordinates", "/api/status", "/api/logs", "/api/clear", "/api/undo", "/api/calibrate",
            "/api/calibrate/start", "/api/calibrate/status", "/api/calibrate/reset", "/api/session",
            "/api/session/new", "/api/gps-origin", "/api/anchors", "/api/layer", "/api/project",
            "/api/project/reset", "/api/model", "/api/models"
        };
        private long requestCount;
        private DateTime startedAtUtc;
        private ApiSecurity security;
        private readonly RequestLog requestLog = new RequestLog(100);
        private string lastExportedSessionJson = "";
        // Application.persistentDataPath is main-thread only: cached in Awake for the HTTP workers.
        private string modelsDirectory = "";

        public int Port => port;
        /// <summary>Per-device key every LAN client (PC bridge, scripts) must send as X-Api-Key.</summary>
        public string ApiKey => securityConfig.apiKey;
        public bool IsRunning => isRunning;
        public RequestLog Logs => requestLog;
        /// <summary>Where uploaded models and their sidecar JSON live (persistentDataPath/models).</summary>
        public string ModelsDirectory => modelsDirectory;

        private void Awake()
        {
            // The LAN API can replace the sensors a field worker relies on, so it is never open:
            // scenes serialized requireApiKey=false, hence enforced here. Browsers get no CORS.
            securityConfig.requireApiKey = true;
            logRequests &= UnityEngine.Debug.isDebugBuild; // request paths in logcat only while developing
            if (string.IsNullOrEmpty(securityConfig.apiKey)) securityConfig.apiKey = DeviceApiKey();
            allowCORS = false;
            security = new ApiSecurity(securityConfig);
            modelsDirectory = Path.Combine(Application.persistentDataPath, "models");
            if (markerManager == null) markerManager = FindAnyObjectByType<MarkerManager>();
            if (calibrationSystem == null) calibrationSystem = FindAnyObjectByType<CoordinateCalibration>();
            if (sessionManager == null) sessionManager = FindAnyObjectByType<SessionManager>();
            if (spatialAnchorManager == null) spatialAnchorManager = FindAnyObjectByType<SpatialAnchorManager>();
            if (projectAlignment == null) projectAlignment = FindAnyObjectByType<ProjectAlignment>();
            if (networkDiscovery == null) networkDiscovery = GetComponent<NetworkDiscovery>() ?? gameObject.AddComponent<NetworkDiscovery>();

            if (startOnAwake) StartServer();
        }

        private void Update()
        {
            SuperviseListener();

            // Take the queued work out under the lock, run it outside: handlers can be slow
            // (marker rebuilds) and HTTP workers must not block on them to enqueue.
            List<CoordinateResponse> responses = null;
            List<CalibrationRequest> calibrations = null;
            List<Action> actions = null;
            lock (lockObject)
            {
                if (pendingResponses.Count > 0) { responses = new List<CoordinateResponse>(pendingResponses); pendingResponses.Clear(); }
                if (pendingCalibrations.Count > 0) { calibrations = new List<CalibrationRequest>(pendingCalibrations); pendingCalibrations.Clear(); }
                if (pendingActions.Count > 0) { actions = new List<Action>(pendingActions); pendingActions.Clear(); }
            }
            if (responses != null)
                foreach (CoordinateResponse r in responses) OnCoordinatesReceived?.Invoke(r);
            if (calibrations != null)
                foreach (CalibrationRequest c in calibrations) OnCalibrationReceived?.Invoke(c);
            if (actions != null)
                foreach (Action a in actions) a?.Invoke();
        }

        /// <summary>
        /// Main-thread half of the listener watchdog. A listen thread that gave up has already
        /// cleared isRunning (so the menu stops advertising a dead URL); here its listener and
        /// the beacon are released and StartServer is retried after 2 s, doubling to 60 s while
        /// it keeps failing. A listener that then stays up for a minute resets the back-off.
        /// </summary>
        private void SuperviseListener()
        {
            float now = Time.unscaledTime;
            if (listenerFailed)
            {
                listenerFailed = false;
                try { networkDiscovery?.StopBeacon(); } catch { /* */ }
                try { httpListener?.Close(); } catch { /* */ }
                httpListener = null;
                listenerThread = null;
                OnServerStopped?.Invoke();
                restartDelay = restartDelay <= 0f ? MinRestartDelay : Mathf.Min(MaxRestartDelay, restartDelay * 2f);
                nextRestartAt = now + restartDelay;
                restartPending = true;
                UnityEngine.Debug.LogWarning($"[LocalAPIServer] Listener stopped on its own — restarting in {restartDelay:F0} s");
            }
            if (restartPending && !isRunning && now >= nextRestartAt)
            {
                restartPending = false;
                StartServer();
                if (!isRunning)
                {
                    // Port or network still unavailable: try again later, backing off.
                    restartDelay = Mathf.Min(MaxRestartDelay, Mathf.Max(MinRestartDelay, restartDelay * 2f));
                    nextRestartAt = now + restartDelay;
                    restartPending = true;
                }
            }
            if (restartDelay > 0f && isRunning && !restartPending && now - startedAtRealtime > 60f)
                restartDelay = 0f;
        }

        private void OnDestroy() => StopServer();
        private void OnApplicationQuit() => StopServer();

        /// <summary>
        /// The listener keeps running while paused (the PC may keep pushing; the queues are
        /// capped), but the beacon stops so PCs do not discover a sleeping headset. On resume
        /// a listener Android tore down during sleep is restarted.
        /// </summary>
        private void OnApplicationPause(bool paused)
        {
            if (!paused && (listenerFailed || restartPending))
            {
                // Died during sleep or while waiting to retry: waking up is the moment to retry.
                SuperviseListener();
                nextRestartAt = Time.unscaledTime;
                SuperviseListener();
                return;
            }
            if (!isRunning) return;
            if (paused)
            {
                networkDiscovery?.StopBeacon();
                return;
            }
            bool alive = httpListener != null && httpListener.IsListening
                         && listenerThread != null && listenerThread.IsAlive;
            if (!alive)
            {
                UnityEngine.Debug.LogWarning("[LocalAPIServer] Listener died while paused — restarting");
                StopServer();
                StartServer();
            }
            else if (enableDiscoveryBeacon && networkDiscovery != null)
            {
                networkDiscovery.StartBeacon();
            }
        }

        public void StartServer()
        {
            if (isRunning) return;
            try
            {
                httpListener = new HttpListener();
                httpListener.Prefixes.Add($"http://*:{port}/");
                httpListener.Start();
                listenerFailed = false;
                isRunning = true;
                startedAtUtc = DateTime.UtcNow;
                startedAtRealtime = Time.unscaledTime;
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
                // Port taken etc.: release the half-made listener so a later StartServer can retry.
                isRunning = false;
                try { httpListener?.Close(); } catch { /* */ }
                httpListener = null;
                string error = $"Failed to start server: {e.Message}";
                OnServerError?.Invoke(error);
                UnityEngine.Debug.LogError($"[LocalAPIServer] {error}");
            }
        }

        public void StopServer()
        {
            restartPending = false; // a deliberate stop is not retried by the watchdog
            lock (listenerStateLock)
            {
                if (!isRunning) return;
                isRunning = false;
            }
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

        /// <summary>A random 8-character key made once per install (no 0/O/1/I to misread).</summary>
        private static string DeviceApiKey()
        {
            const string pref = "api.key";
            string key = PlayerPrefs.GetString(pref, "");
            if (key.Length >= 8) return key;
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var bytes = new byte[8];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var sb = new StringBuilder(8);
            foreach (byte b in bytes) sb.Append(alphabet[b % alphabet.Length]);
            key = sb.ToString();
            PlayerPrefs.SetString(pref, key);
            PlayerPrefs.Save();
            return key;
        }

        public void SetLastExportedSession(string json) => lastExportedSessionJson = json ?? "";

        public void BindComponents(MarkerManager markers, CoordinateCalibration calibration,
            SessionManager sessions = null, SpatialAnchorManager anchors = null, ProjectAlignment project = null)
        {
            if (project != null) projectAlignment = project;
            if (markers != null) markerManager = markers;
            if (calibration != null) calibrationSystem = calibration;
            if (sessions != null) sessionManager = sessions;
            if (anchors != null) spatialAnchorManager = anchors;
        }

        private void ListenLoop()
        {
            // Local copy: StopServer nulls the field while this thread may still be in GetContext.
            HttpListener listener = httpListener;
            int failures = 0;
            while (isRunning && listener != null)
            {
                try
                {
                    var context = listener.GetContext();
                    failures = 0;
                    Interlocked.Increment(ref requestCount);
                    ThreadPool.QueueUserWorkItem(ProcessRequest, context);
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception e)
                {
                    if (!isRunning) break;
                    // A broken listener (network gone during sleep) throws at once on every call:
                    // back off instead of spinning a core and flooding logcat, and give up after
                    // a while — OnApplicationPause(false) restarts a dead listener.
                    failures++;
                    if (failures <= 3 || failures % 50 == 0)
                        UnityEngine.Debug.LogError($"[LocalAPIServer] Listen error ({failures}): {e.Message}");
                    if (failures >= 200 || !listener.IsListening) break;
                    Thread.Sleep(Math.Min(1000, 10 * failures));
                }
            }

            // Gave up on its own (not stopped by StopServer, not replaced by a newer listener):
            // report the server down at once and leave the restart to the main thread.
            lock (listenerStateLock)
            {
                if (isRunning && ReferenceEquals(listener, httpListener))
                {
                    isRunning = false;
                    listenerFailed = true;
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

                path = request.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant();
                if (string.IsNullOrEmpty(path)) path = "/";

                string client = request.RemoteEndPoint?.Address?.ToString() ?? "unknown";
                string retryAfter = Math.Max(1, (int)Math.Ceiling(securityConfig.windowSeconds))
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);

                // An IP that keeps guessing keys is refused outright for the rest of the window.
                if (security.IsLockedOut(client))
                {
                    status = 429;
                    DiscardUnreadBody(request, response);
                    response.AddHeader("Retry-After", retryAfter);
                    SendJson(response, 429, "{\"error\":\"Too many wrong API keys — wait and retry\"}");
                    return;
                }

                // Preflight carries no credentials: it only ever gets the plain (low) budget.
                bool preflight = request.HttpMethod == "OPTIONS";
                bool authenticated = false;
                if (!preflight && security.IsAuthRequired(path))
                {
                    string key = request.Headers["X-Api-Key"];
                    string auth = request.Headers["Authorization"];
                    if (!security.ValidateApiKey(key, auth))
                    {
                        security.RecordAuthFailure(client);
                        status = 401;
                        DiscardUnreadBody(request, response);
                        response.AddHeader("WWW-Authenticate", "Bearer");
                        SendJson(response, 401, "{\"error\":\"Unauthorized — provide X-Api-Key or Bearer token\"}");
                        return;
                    }
                    authenticated = true;
                    if (!security.CheckAuthenticatedRateLimit(client))
                    {
                        status = 429;
                        DiscardUnreadBody(request, response);
                        response.AddHeader("Retry-After", retryAfter);
                        SendJson(response, 429, "{\"error\":\"Rate limit exceeded\"}");
                        return;
                    }
                }
                else if (!security.CheckRateLimit(client))
                {
                    status = 429;
                    DiscardUnreadBody(request, response);
                    response.AddHeader("Retry-After", retryAfter);
                    SendJson(response, 429, "{\"error\":\"Rate limit exceeded\"}");
                    return;
                }

                // Past here without a key: only preflight and /api/health, neither reads a body.
                if (!authenticated) DiscardUnreadBody(request, response);

                if (preflight)
                {
                    status = 204;
                    response.StatusCode = 204;
                    response.Close();
                    return;
                }

                if (logRequests)
                    UnityEngine.Debug.Log($"[LocalAPIServer] {request.HttpMethod} {path}");

                status = Route(request, response, path);
            }
            catch (BodyTimeoutException)
            {
                // The body never arrived: answer 408 and drop the connection. Abort, not
                // Close — Close would try to drain the very input that never comes.
                status = 408;
                try
                {
                    response.StatusCode = 408;
                    response.ContentType = "application/json; charset=utf-8";
                    byte[] buffer = Encoding.UTF8.GetBytes("{\"error\":\"Request body timeout\"}");
                    response.ContentLength64 = buffer.Length;
                    response.OutputStream.Write(buffer, 0, buffer.Length);
                }
                catch { /* client may already be gone */ }
                try { response.Abort(); } catch { /* */ }
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
            if (path == "/api/project" && method == "POST") return HandleProjectPost(request, response);
            if (path == "/api/project" && method == "GET") return HandleProjectStatus(response);
            if (path == "/api/project/reset" && method == "POST")
                return QueueCommand(response, () => OnProjectResetRequested?.Invoke(),
                    "{\"success\":true,\"message\":\"Placements cleared\"}");
            if (path == "/api/model" && method == "POST") return HandleModelPost(request, response);
            if (path == "/api/models" && method == "GET") return HandleModelsList(response);
            if (path.StartsWith("/api/model/") && method == "DELETE")
            {
                // Model names are case-sensitive file names (Greek included): take them from the raw path.
                string rawPath = request.Url.AbsolutePath.TrimEnd('/');
                string name = Uri.UnescapeDataString(rawPath.Substring(rawPath.LastIndexOf('/') + 1));
                return HandleModelDelete(name, response);
            }

            if (KnownPaths.Contains(path) || path.StartsWith("/api/coordinates/") || path.StartsWith("/api/model/"))
            {
                SendJson(response, 405, "{\"error\":\"Method not allowed\",\"method\":\"" + EscapeJson(method) +
                                        "\",\"path\":\"" + EscapeJson(path) + "\"}");
                return 405;
            }
            SendJson(response, 404, "{\"error\":\"Not found\",\"path\":\"" + EscapeJson(path) + "\"}");
            return 404;
        }

        // ---- Main-thread hand-off and body parsing helpers --------------------------------

        /// <summary>
        /// Call before answering without reading the request body. HttpListener's Close() drains
        /// an unread body to keep the connection alive, waiting up to 1 s per 2 KB read for as
        /// long as bytes keep trickling in — so a keyless client announcing a 300 MB body could
        /// pin a pool worker indefinitely with each connection. A small body (≤ 512 KB) is read
        /// here within <see cref="RejectDrainMs"/> so a normal client still gets its 401/429
        /// cleanly; the connection is closed after the answer either way, never drained.
        /// </summary>
        private static void DiscardUnreadBody(HttpListenerRequest request, HttpListenerResponse response)
        {
            try
            {
                if (!request.HasEntityBody) return;
                response.KeepAlive = false; // Close() then sends "Connection: close" and skips the drain
                long declared = request.ContentLength64;
                if (declared <= 0 || declared > MaxBodyBytes) return; // chunked or large: just close
                Stream input = request.InputStream;
                byte[] buffer = new byte[8192];
                var clock = Stopwatch.StartNew();
                long total = 0;
                while (total < declared)
                {
                    int remaining = RejectDrainMs - (int)clock.ElapsedMilliseconds;
                    if (remaining <= 0) return;
                    Task<int> read = input.ReadAsync(buffer, 0, buffer.Length);
                    if (!read.Wait(remaining))
                    {
                        read.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                        return;
                    }
                    int n = read.Result;
                    if (n <= 0) return;
                    total += n;
                }
            }
            catch { /* client gone or stream broken: the answer is best effort */ }
        }

        /// <summary>Queue work for Update(); false when the queue is full (main thread asleep/stuck).</summary>
        private bool TryEnqueueAction(Action action)
        {
            lock (lockObject)
            {
                if (pendingActions.Count >= MaxPendingActions) return false;
                pendingActions.Enqueue(action);
                return true;
            }
        }

        /// <summary>Queue a command for the main thread and answer 200 with okJson, or 503 when the queue is full.</summary>
        private int QueueCommand(HttpListenerResponse response, Action action, string okJson)
        {
            if (!TryEnqueueAction(action)) return SendBusy(response);
            SendJson(response, 200, okJson);
            return 200;
        }

        private int SendBusy(HttpListenerResponse response)
        {
            try { response.AddHeader("Retry-After", "2"); } catch { /* */ }
            SendJson(response, 503, "{\"error\":\"Headset busy or asleep — too many queued commands, retry shortly\"}");
            return 503;
        }

        /// <summary>
        /// Queue a coordinate batch (capped). Dropping the oldest batch must not lose a
        /// "clear first" in it, so that flag moves on to the next batch.
        /// </summary>
        private void EnqueueCoordinates(CoordinateResponse batch)
        {
            lock (lockObject)
            {
                // Paused (headset off) the main thread drains nothing; a PC pushing all
                // along must not replay hundreds of batches in one frame on resume.
                while (pendingResponses.Count >= MaxPendingResponses)
                {
                    CoordinateResponse dropped = pendingResponses.Dequeue();
                    if (dropped != null && dropped.clearExisting)
                    {
                        if (pendingResponses.Count > 0) pendingResponses.Peek().clearExisting = true;
                        else batch.clearExisting = true;
                    }
                }
                pendingResponses.Enqueue(batch);
            }
        }

        /// <summary>
        /// Read the body and parse it as a JSON object of type T. On failure the error
        /// (413 too large, 400 empty / not an object / malformed) has been sent and
        /// <paramref name="status"/> holds its code. An empty body with allowEmpty yields
        /// value == null and true. BodyTimeoutException propagates (→ 408).
        /// </summary>
        private bool TryReadJson<T>(HttpListenerRequest request, HttpListenerResponse response, bool allowEmpty,
            out T value, out int status) where T : class
        {
            value = null;
            status = 0;
            string body = ReadBody(request);
            if (body == null) { status = 413; SendJson(response, 413, TooLargeJson); return false; }
            body = body.Trim();
            if (body.Length == 0)
            {
                if (allowEmpty) return true;
                status = 400;
                SendJson(response, 400, "{\"error\":\"Empty body — expected a JSON object\"}");
                return false;
            }
            if (body[0] != '{')
            {
                status = 400;
                SendJson(response, 400, "{\"error\":\"Body must be a JSON object ({...})\"}");
                return false;
            }
            try
            {
                // JsonUtility is safe off the main thread (it only touches the new object).
                value = JsonUtility.FromJson<T>(body);
            }
            catch (Exception e)
            {
                status = 400;
                SendJson(response, 400, "{\"error\":\"Invalid JSON: " + EscapeJson(e.Message) + "\"}");
                return false;
            }
            if (value == null)
            {
                status = 400;
                SendJson(response, 400, "{\"error\":\"Invalid JSON\"}");
                return false;
            }
            return true;
        }

        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        private int HandleCoordinatesPost(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (!TryReadJson(request, response, false, out CoordinateResponse coordResponse, out int status))
                return status;

            int count = coordResponse.coordinates?.Length ?? 0;
            EnqueueCoordinates(coordResponse);
            SendJson(response, 200, "{\"success\":true,\"message\":\"Coordinates received\",\"count\":" + count + "}");
            return 200;
        }

        /// <summary>
        /// Run a producer on the Unity main thread (via the Update pump) and block this
        /// worker thread until it completes. Marker/calibration/session state must not be
        /// read from HttpListener threads. Falls back after timeoutMs (e.g. app paused).
        /// </summary>
        private string RunOnMainThread(Func<string> produce, string fallback, int timeoutMs = 1000)
        {
            return TryRunOnMainThread(produce, out string result, timeoutMs) && result != null ? result : fallback;
        }

        /// <summary>
        /// As <see cref="RunOnMainThread"/>, but reports whether the main thread answered.
        /// A request that timed out is withdrawn: when the app wakes up it does not run the
        /// stale producer (session exports, status reads) for a client that is long gone.
        /// </summary>
        private bool TryRunOnMainThread(Func<string> produce, out string result, int timeoutMs = 1000)
        {
            var done = new ManualResetEventSlim(false);
            string produced = null;
            bool ok = false;
            int state = 0; // 0 queued, 1 claimed by the main thread, 2 withdrawn by this worker
            bool queued = TryEnqueueAction(() =>
            {
                if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return;
                try { produced = produce(); ok = true; }
                catch (Exception e) { UnityEngine.Debug.LogError($"[LocalAPIServer] Main-thread read failed: {e.Message}"); }
                finally { done.Set(); }
            });
            result = null;
            if (!queued) return false;
            if (!done.Wait(timeoutMs))
            {
                if (Interlocked.CompareExchange(ref state, 2, 0) == 0) return false;
                done.Wait(); // the main thread picked it up just now: let it finish
            }
            result = produced;
            return ok;
        }

        private int HandleCoordinatesGet(HttpListenerResponse response)
        {
            const string empty = "{\"status\":\"ready\",\"markerCount\":0,\"coordinates\":[]}";
            // Unity's == null on a component is main-thread state too: test it in the producer.
            string json = RunOnMainThread(() => markerManager != null ? markerManager.ToCoordinatesJson() : empty, empty);
            SendJson(response, 200, json);
            return 200;
        }

        private int HandleDeleteMarker(string id, HttpListenerResponse response)
        {
            if (string.IsNullOrEmpty(id)) { SendJson(response, 400, "{\"error\":\"Missing marker id\"}"); return 400; }
            string captured = id;
            return QueueCommand(response, () => OnDeleteMarkerRequested?.Invoke(captured),
                "{\"success\":true,\"id\":\"" + EscapeJson(id) + "\"}");
        }

        private int HandleHealthCheck(HttpListenerResponse response)
        {
            // Unauthenticated and served on the listener thread: no Unity-side state here
            // (marker count / calibration live in the authenticated /api/status).
            double uptime = isRunning ? (DateTime.UtcNow - startedAtUtc).TotalSeconds : 0;
            string json =
                "{\"status\":\"healthy\",\"device\":\"Meta Quest 3\",\"version\":\"" + AppVersion + "\"," +
                "\"uptimeSeconds\":" + uptime.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "," +
                "\"requestCount\":" + Interlocked.Read(ref requestCount) + "}";
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
            EnqueueCoordinates(new CoordinateResponse
            {
                coordinates = Array.Empty<CoordinateData>(),
                clearExisting = true
            });
            SendJson(response, 200, "{\"success\":true,\"message\":\"Markers cleared\"}");
            return 200;
        }

        private int HandleUndo(HttpListenerResponse response)
        {
            return QueueCommand(response, () => OnUndoRequested?.Invoke(),
                "{\"success\":true,\"message\":\"Undo requested\"}");
        }

        private int HandleProjectPost(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (!TryReadJson(request, response, false, out ProjectRequest project, out int status))
                return status;
            int count = project.points?.Length ?? 0;
            if (count == 0) { SendJson(response, 400, "{\"error\":\"Project needs a non-empty points array\"}"); return 400; }
            for (int i = 0; i < count; i++)
            {
                ProjectPoint p = project.points[i];
                if (p == null || !IsFinite(p.e) || !IsFinite(p.n) || !IsFinite(p.h))
                {
                    SendJson(response, 400, "{\"error\":\"Point " + i + " needs finite e, n, h\"}");
                    return 400;
                }
            }
            return QueueCommand(response, () => OnProjectReceived?.Invoke(project),
                "{\"success\":true,\"message\":\"Project received\",\"count\":" + count + "}");
        }

        private int HandleProjectStatus(HttpListenerResponse response)
        {
            const string none = "{\"loaded\":false,\"aligned\":false}";
            // Was 200 {"error":"timeout"} when the main thread did not answer; 503 says retry.
            if (!TryRunOnMainThread(() => projectAlignment != null ? projectAlignment.StatusJson() : none, out string json)
                || string.IsNullOrEmpty(json))
                return SendBusy(response);
            SendJson(response, 200, json);
            return 200;
        }

        private int HandleCalibrate(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (!TryReadJson(request, response, false, out CalibrationRequest cal, out int status))
                return status;
            if (!IsFinite(cal.realX) || !IsFinite(cal.realY) || !IsFinite(cal.realZ) || !IsFinite(cal.rotationY))
            {
                SendJson(response, 400, "{\"error\":\"realX, realY, realZ and rotationY must be finite numbers\"}");
                return 400;
            }
            bool queued;
            lock (lockObject)
            {
                queued = pendingCalibrations.Count < MaxPendingResponses;
                if (queued) pendingCalibrations.Enqueue(cal);
            }
            if (!queued) return SendBusy(response);
            SendJson(response, 200, "{\"success\":true,\"message\":\"Calibration data received\"}");
            return 200;
        }

        private int HandleStartCalibration(HttpListenerResponse response)
        {
            return QueueCommand(response, () => OnStartCalibrationMode?.Invoke(),
                "{\"success\":true,\"message\":\"Calibration mode started\"}");
        }

        private int HandleCalibrationStatus(HttpListenerResponse response)
        {
            const string unknown = "{\"status\":\"unknown\",\"calibrated\":false}";
            string json = RunOnMainThread(() =>
            {
                if (calibrationSystem == null) return unknown;
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
            }, unknown);
            SendJson(response, 200, json);
            return 200;
        }

        private int HandleResetCalibration(HttpListenerResponse response)
        {
            return QueueCommand(response, () => OnResetCalibrationRequested?.Invoke(), "{\"success\":true}");
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
            // Validate the JSON here so a bad file is a 400 to the sender, not a silent no-op
            // on the headset; the raw text still goes to the importer (it reads extra shapes).
            string body = ReadBody(request);
            if (body == null) { SendJson(response, 413, TooLargeJson); return 413; }
            body = body.Trim();
            if (body.Length == 0 || body[0] != '{')
            {
                SendJson(response, 400, "{\"error\":\"Body must be a session JSON object ({...})\"}");
                return 400;
            }
            MarkerSession session;
            try { session = JsonUtility.FromJson<MarkerSession>(body); }
            catch (Exception e)
            {
                SendJson(response, 400, "{\"error\":\"Invalid session JSON: " + EscapeJson(e.Message) + "\"}");
                return 400;
            }
            string bad = SessionFieldError("name", session?.name, 120) ?? SessionFieldError("id", session?.id, 64);
            if (bad != null)
            {
                SendJson(response, 400, "{\"error\":\"" + EscapeJson(bad) + "\"}");
                return 400;
            }
            string captured = body;
            return QueueCommand(response, () => OnSessionImportJson?.Invoke(captured),
                "{\"success\":true,\"message\":\"Session import queued\"}");
        }

        /// <summary>
        /// Session name and id end up in the saved file's name. SessionManager sanitises them
        /// anyway; this tells the sender up front instead of renaming silently. Only what could
        /// escape the folder or break a file name is refused — Greek, spaces and dashes pass.
        /// </summary>
        private static string SessionFieldError(string field, string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return null; // optional: the headset keeps its own
            if (value.Length > maxLength) return $"Session {field} is longer than {maxLength} characters";
            if (value.Contains("..")) return $"Session {field} must not contain '..'";
            foreach (char c in value)
            {
                if (char.IsControl(c) || c == '/' || c == '\\' || c == ':' || c == '*' || c == '?'
                    || c == '"' || c == '<' || c == '>' || c == '|')
                    return $"Session {field} must not contain / \\ : * ? \" < > | or control characters";
            }
            return null;
        }

        private int HandleSessionNew(HttpListenerRequest request, HttpListenerResponse response)
        {
            // An empty body keeps the default name; malformed JSON is a 400 (it used to be ignored).
            if (!TryReadJson(request, response, true, out SessionNewBody parsed, out int status))
                return status;
            string name = "session";
            if (parsed != null && !string.IsNullOrWhiteSpace(parsed.name))
            {
                name = parsed.name.Trim();
                if (name.Length > 120) name = name.Substring(0, 120);
                string bad = SessionFieldError("name", name, 120);
                if (bad != null)
                {
                    SendJson(response, 400, "{\"error\":\"" + EscapeJson(bad) + "\"}");
                    return 400;
                }
            }

            string captured = name;
            return QueueCommand(response, () =>
            {
                if (sessionManager != null) sessionManager.NewSession(captured);
            }, "{\"success\":true,\"name\":\"" + EscapeJson(name) + "\"}");
        }

        private int HandleGpsOrigin(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (!TryReadJson(request, response, false, out GPSReference gps, out int status))
                return status;
            if (!IsFinite(gps.latitude) || !IsFinite(gps.longitude) || !IsFinite(gps.altitude)
                || Math.Abs(gps.latitude) > 90 || Math.Abs(gps.longitude) > 180)
            {
                SendJson(response, 400, "{\"error\":\"latitude must be within ±90, longitude within ±180, altitude finite\"}");
                return 400;
            }
            return QueueCommand(response, () => OnGpsOriginSet?.Invoke(gps), "{\"success\":true}");
        }

        private int HandleAnchors(HttpListenerResponse response)
        {
            const string none = "{\"hasReference\":false}";
            string json = RunOnMainThread(() => spatialAnchorManager != null ? spatialAnchorManager.GetStatusJson() : none, none);
            SendJson(response, 200, json);
            return 200;
        }

        private int HandleLayer(HttpListenerRequest request, HttpListenerResponse response)
        {
            // {"layer":"hazards"}; empty body or empty layer = all. An oversized body used to
            // fall through to "show all" silently; it is a 413 now.
            if (!TryReadJson(request, response, true, out LayerBody parsed, out int status))
                return status;
            string layer = parsed?.layer ?? "";
            string captured = layer;
            return QueueCommand(response, () =>
            {
                if (markerManager != null)
                    markerManager.VisibleLayerFilter = captured;
            }, "{\"success\":true,\"layer\":\"" + EscapeJson(layer) + "\"}");
        }

        // ---- Design models -------------------------------------------------------------

        /// <summary>
        /// POST /api/model — the raw body is the model file. Metadata travels in headers
        /// (X-Model-Name, X-Model-Format, X-Model-Units, X-Model-Up, X-Model-Georeferenced,
        /// X-Model-Origin "E,N,H", X-Model-Project, X-Model-Source). Name/Source may be
        /// UTF-8 percent-encoded since HTTP headers cannot carry Greek letters as-is.
        /// The body streams to a temp file (300 MB cap; 30 s without data or 15 min overall
        /// is a 408) and is then renamed
        /// to &lt;name&gt;.&lt;format&gt; with a &lt;file&gt;.json sidecar for ModelStore.
        /// X-Model-Format "zip" is a textured package (OBJ + MTL + images), unpacked into
        /// models/&lt;name&gt;/ with a model.json sidecar; see <see cref="StorePackage"/>.
        /// </summary>
        private int HandleModelPost(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (Interlocked.Increment(ref activeModelUploads) > MaxConcurrentModelUploads)
            {
                Interlocked.Decrement(ref activeModelUploads);
                DiscardUnreadBody(request, response);
                try { response.AddHeader("Retry-After", "30"); } catch { /* */ }
                SendJson(response, 503, "{\"error\":\"Other model uploads are in progress — retry when they finish\"}");
                return 503;
            }
            try { return StoreModelUpload(request, response); }
            finally { Interlocked.Decrement(ref activeModelUploads); }
        }

        private int StoreModelUpload(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (string.IsNullOrEmpty(modelsDirectory))
            {
                SendJson(response, 503, "{\"error\":\"Models directory not ready\"}");
                return 503;
            }

            string name = SanitizeModelName(DecodeHeader(request.Headers["X-Model-Name"]));
            if (string.IsNullOrEmpty(name)) { SendJson(response, 400, "{\"error\":\"X-Model-Name header is required\"}"); return 400; }

            string format = (request.Headers["X-Model-Format"] ?? "").Trim().ToLowerInvariant();
            bool package = format == PackageFormat;
            if (!package && Array.IndexOf(ModelFormats, format) < 0)
            {
                SendJson(response, 400, "{\"error\":\"X-Model-Format must be one of: obj, dxf, zip\"}");
                return 400;
            }

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            float unitScale = 1f;
            string unitsHeader = request.Headers["X-Model-Units"];
            if (!string.IsNullOrWhiteSpace(unitsHeader) &&
                // NumberStyles.Float accepts "NaN"/"Infinity", and NaN <= 0 is false: check explicitly.
                (!float.TryParse(unitsHeader.Trim(), System.Globalization.NumberStyles.Float, inv, out unitScale)
                 || !IsFinite(unitScale) || unitScale <= 0f))
            {
                SendJson(response, 400, "{\"error\":\"X-Model-Units must be a positive number (source unit in metres)\"}");
                return 400;
            }

            string up = (request.Headers["X-Model-Up"] ?? "z").Trim().ToLowerInvariant();
            if (up != "z" && up != "y") { SendJson(response, 400, "{\"error\":\"X-Model-Up must be z or y\"}"); return 400; }

            bool georeferenced = true;
            string geoHeader = request.Headers["X-Model-Georeferenced"];
            if (!string.IsNullOrWhiteSpace(geoHeader))
            {
                string g = geoHeader.Trim().ToLowerInvariant();
                if (g == "true" || g == "1" || g == "yes") georeferenced = true;
                else if (g == "false" || g == "0" || g == "no") georeferenced = false;
                else { SendJson(response, 400, "{\"error\":\"X-Model-Georeferenced must be true or false\"}"); return 400; }
            }

            double[] origin = null;
            string originHeader = request.Headers["X-Model-Origin"];
            if (!string.IsNullOrWhiteSpace(originHeader))
            {
                string[] parts = originHeader.Split(',');
                origin = new double[3];
                bool ok = parts.Length == 3;
                for (int i = 0; ok && i < 3; i++)
                    ok = double.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float, inv, out origin[i])
                         && IsFinite(origin[i]); // a NaN would be written as invalid JSON into the sidecar
                if (!ok) { SendJson(response, 400, "{\"error\":\"X-Model-Origin must be E,N,H (finite numbers)\"}"); return 400; }
            }

            int? projectId = null;
            string projectHeader = request.Headers["X-Model-Project"];
            if (!string.IsNullOrWhiteSpace(projectHeader))
            {
                if (!int.TryParse(projectHeader.Trim(), System.Globalization.NumberStyles.Integer, inv, out int pid))
                {
                    SendJson(response, 400, "{\"error\":\"X-Model-Project must be an integer\"}");
                    return 400;
                }
                projectId = pid;
            }

            string source = SanitizeModelName(DecodeHeader(request.Headers["X-Model-Source"]));
            if (string.IsNullOrEmpty(source)) source = name + "." + format;

            if (request.ContentLength64 > MaxModelBytes)
            {
                SendJson(response, 413, "{\"error\":\"Model exceeds the 300 MB limit\"}");
                return 413;
            }

            Directory.CreateDirectory(modelsDirectory);
            string tempPath = Path.Combine(modelsDirectory, Guid.NewGuid().ToString("N") + ".upload");
            long bytes;
            try
            {
                bytes = StreamBodyToFile(request.InputStream, tempPath);
            }
            catch (Exception)
            {
                TryDelete(tempPath);
                throw; // BodyTimeoutException → 408 in ProcessRequest; anything else → 500
            }
            if (bytes < 0)
            {
                TryDelete(tempPath);
                SendJson(response, 413, "{\"error\":\"Model exceeds the 300 MB limit\"}");
                return 413;
            }
            if (bytes == 0)
            {
                TryDelete(tempPath);
                SendJson(response, 400, "{\"error\":\"Empty body\"}");
                return 400;
            }

            if (package)
                return StorePackage(response, tempPath, name, unitScale, up, georeferenced, origin, projectId, source, bytes);

            string finalPath = Path.Combine(modelsDirectory, name + "." + format);
            string sidecarPath = finalPath + ".json";
            try
            {
                lock (modelLock)
                {
                    TryDelete(finalPath); // same name replaces the previous upload
                    // ...including a textured package of that name, so the list never shows two.
                    TryDeleteFolder(Path.Combine(modelsDirectory, name));
                    File.Move(tempPath, finalPath);
                    File.WriteAllText(sidecarPath, BuildSidecarJson(name, format, unitScale, up, georeferenced,
                        origin, projectId, source, bytes, false), new UTF8Encoding(false));
                }
            }
            catch (Exception e)
            {
                TryDelete(tempPath);
                TryDelete(finalPath);
                TryDelete(sidecarPath);
                SendJson(response, 500, "{\"error\":\"Could not store model: " + EscapeJson(e.Message) + "\"}");
                return 500;
            }

            string captured = finalPath;
            lock (lockObject) pendingActions.Enqueue(() => OnModelReceived?.Invoke(captured));
            UnityEngine.Debug.Log($"[LocalAPIServer] Model '{name}' ({format}, {bytes} bytes) stored at {finalPath}");

            var sb = new StringBuilder(256);
            sb.Append("{\"success\":true,");
            JsonText.AppendField(sb, "name", name);
            sb.Append(",\"format\":\"").Append(format).Append("\",");
            sb.Append("\"bytes\":").Append(bytes).Append(',');
            JsonText.AppendField(sb, "path", finalPath);
            sb.Append('}');
            SendJson(response, 200, sb.ToString());
            return 200;
        }

        /// <summary>
        /// Copy the request body to <paramref name="tempPath"/> in 64 KB chunks on this worker
        /// thread. Returns the byte count, or -1 past the size cap; throws
        /// <see cref="BodyTimeoutException"/> when no data arrives for 30 s or the upload
        /// outlives the 15 min budget.
        /// </summary>
        private static long StreamBodyToFile(Stream input, string tempPath)
        {
            var deadline = Stopwatch.StartNew();
            byte[] buffer = new byte[ModelChunkBytes];
            long total = 0;
            using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, ModelChunkBytes))
            {
                while (true)
                {
                    int remaining = ModelUploadTimeoutMs - (int)deadline.ElapsedMilliseconds;
                    if (remaining <= 0) throw new BodyTimeoutException();
                    // HttpListener's input stream has no read timeout: wait on the async read
                    // instead so a stalled client cannot pin this worker forever.
                    Task<int> read = input.ReadAsync(buffer, 0, buffer.Length);
                    if (!read.Wait(Math.Min(remaining, ModelUploadIdleMs)))
                    {
                        read.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                        throw new BodyTimeoutException();
                    }
                    int n = read.Result;
                    if (n <= 0) break;
                    total += n;
                    if (total > MaxModelBytes) return -1;
                    file.Write(buffer, 0, n);
                }
            }
            return total;
        }

        // ---- Textured packages (zip) ---------------------------------------------------

        /// <summary>Thrown by <see cref="ExtractPackage"/> for an archive the client must fix (HTTP 400).</summary>
        private sealed class PackageRejectedException : Exception
        {
            public PackageRejectedException(string message) : base(message) { }
        }

        /// <summary>
        /// Unpack the uploaded zip at <paramref name="zipPath"/> into models/&lt;name&gt;/, write
        /// its model.json and announce the .obj. Runs on this request's worker thread, so
        /// a 300 MB archive never stalls the main thread. The archive is unpacked into a
        /// hidden ".extract-…" folder first and only then replaces the previous package, so a
        /// bad upload leaves the old model intact and ModelStore never lists half a package.
        /// </summary>
        private int StorePackage(HttpListenerResponse response, string zipPath, string name, float unitScale,
            string up, bool georeferenced, double[] origin, int? projectId, string source, long zipBytes)
        {
            string stagingDir = Path.Combine(modelsDirectory, Models.ModelStore.ExtractingPrefix + Guid.NewGuid().ToString("N"));
            string finalDir = Path.Combine(modelsDirectory, name);
            string objName;
            long extracted;
            try
            {
                Directory.CreateDirectory(stagingDir);
                extracted = ExtractPackage(zipPath, stagingDir, out objName);
            }
            catch (Exception e)
            {
                TryDelete(zipPath);
                TryDeleteFolder(stagingDir);
                if (e is PackageRejectedException)
                {
                    SendJson(response, 400, "{\"error\":\"" + EscapeJson(e.Message) + "\"}");
                    return 400;
                }
                if (e is InvalidDataException)
                {
                    SendJson(response, 400, "{\"error\":\"Not a valid zip archive: " + EscapeJson(e.Message) + "\"}");
                    return 400;
                }
                SendJson(response, 500, "{\"error\":\"Could not unpack model: " + EscapeJson(e.Message) + "\"}");
                return 500;
            }
            TryDelete(zipPath);

            string objPath = Path.Combine(finalDir, objName);
            try
            {
                File.WriteAllText(Path.Combine(stagingDir, Models.ModelStore.PackageSidecar),
                    BuildSidecarJson(name, "obj", unitScale, up, georeferenced, origin, projectId, source, extracted, true),
                    new UTF8Encoding(false));
                lock (modelLock)
                {
                    // Same name replaces the previous upload, package or single file.
                    if (Directory.Exists(finalDir)) Directory.Delete(finalDir, true);
                    foreach (string f in ModelFormats)
                    {
                        string single = Path.Combine(modelsDirectory, name + "." + f);
                        TryDelete(single);
                        TryDelete(single + ".json");
                    }
                    Directory.Move(stagingDir, finalDir);
                }
            }
            catch (Exception e)
            {
                TryDeleteFolder(stagingDir);
                SendJson(response, 500, "{\"error\":\"Could not store model: " + EscapeJson(e.Message) + "\"}");
                return 500;
            }

            string captured = objPath;
            lock (lockObject) pendingActions.Enqueue(() => OnModelReceived?.Invoke(captured));
            UnityEngine.Debug.Log($"[LocalAPIServer] Model package '{name}' ({zipBytes} bytes zipped, {extracted} unpacked) stored at {finalDir}");

            var sb = new StringBuilder(256);
            sb.Append("{\"success\":true,");
            JsonText.AppendField(sb, "name", name);
            sb.Append(",\"format\":\"obj\",\"package\":true,");
            sb.Append("\"bytes\":").Append(extracted).Append(',');
            JsonText.AppendField(sb, "path", objPath);
            sb.Append('}');
            SendJson(response, 200, sb.ToString());
            return 200;
        }

        /// <summary>
        /// Extract the model files of <paramref name="zipPath"/> into <paramref name="destDir"/>
        /// and return the bytes written. Rules: at most 500 entries and 1 GB unpacked (counted
        /// while copying, not trusted from the headers); only .obj .mtl .png .jpg .jpeg are
        /// kept, anything else is skipped; an entry with "..", a rooted path or a drive that
        /// would land outside the folder rejects the whole archive (zip-slip). A single
        /// top-level folder that holds everything (what "compress folder" produces) is
        /// stripped. Exactly one .obj must result, at the top of the package.
        /// </summary>
        private static long ExtractPackage(string zipPath, string destDir, out string objName)
        {
            objName = null;
            string destRoot = Path.GetFullPath(destDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                              + Path.DirectorySeparatorChar;
            using (ZipArchive zip = OpenZip(zipPath))
            {
                if (zip.Entries.Count > MaxPackageEntries)
                    throw new PackageRejectedException($"Zip has {zip.Entries.Count} entries; at most {MaxPackageEntries} are allowed");

                // Pass 1: validate names, pick the kept entries, find a common top folder.
                var kept = new List<KeyValuePair<ZipArchiveEntry, string>>();
                string commonRoot = null;
                bool rootAgreed = true;
                long declared = 0;
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    string rel = entry.FullName.Replace('\\', '/');
                    if (rel.Length == 0 || rel.EndsWith("/")) continue; // directory entry
                    if (rel.StartsWith("/") || rel.IndexOf(':') >= 0)
                        throw new PackageRejectedException("Zip entry with an absolute path: " + entry.FullName);
                    string[] parts = rel.Split('/');
                    foreach (string p in parts)
                        if (p == "..") throw new PackageRejectedException("Zip entry escapes the folder: " + entry.FullName);
                    // macOS resource forks ride along in Finder zips; they are never model files.
                    if (parts[0] == "__MACOSX" || parts[parts.Length - 1].StartsWith("._")) continue;
                    string ext = Path.GetExtension(rel).ToLowerInvariant();
                    if (Array.IndexOf(PackageExtensions, ext) < 0) continue;

                    string top = parts.Length > 1 ? parts[0] : null;
                    if (kept.Count == 0) commonRoot = top;
                    else if (!string.Equals(commonRoot, top, StringComparison.Ordinal)) rootAgreed = false;
                    declared += Math.Max(0, entry.Length);
                    if (declared > MaxPackageExtractBytes)
                        throw new PackageRejectedException("Zip unpacks to more than 1 GB");
                    kept.Add(new KeyValuePair<ZipArchiveEntry, string>(entry, rel));
                }
                if (kept.Count == 0) throw new PackageRejectedException("Zip contains no .obj, .mtl or image files");
                int strip = rootAgreed && commonRoot != null ? commonRoot.Length + 1 : 0;

                // Pass 2: count .obj files before writing anything.
                int objCount = 0, rootObjCount = 0;
                foreach (var kv in kept)
                {
                    if (!kv.Value.EndsWith(".obj", StringComparison.OrdinalIgnoreCase)) continue;
                    objCount++;
                    if (kv.Value.Substring(strip).IndexOf('/') < 0) { rootObjCount++; objName = Path.GetFileName(kv.Value); }
                }
                if (objCount != 1)
                    throw new PackageRejectedException(objCount == 0
                        ? "Zip contains no .obj file"
                        : $"Zip contains {objCount} .obj files; a model package must contain exactly one");
                if (rootObjCount != 1)
                    throw new PackageRejectedException("The .obj must be at the top of the zip (or of its single top folder)");

                // Pass 3: write, counting the real output against the cap.
                long total = 0;
                byte[] buffer = new byte[ModelChunkBytes];
                foreach (var kv in kept)
                {
                    string rel = kv.Value.Substring(strip);
                    string target = Path.GetFullPath(Path.Combine(destRoot, rel));
                    if (!target.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase))
                        throw new PackageRejectedException("Zip entry escapes the folder: " + kv.Key.FullName);
                    string targetDir = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
                    using (Stream input = kv.Key.Open())
                    using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, ModelChunkBytes))
                    {
                        int n;
                        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            total += n;
                            if (total > MaxPackageExtractBytes)
                                throw new PackageRejectedException("Zip unpacks to more than 1 GB");
                            output.Write(buffer, 0, n);
                        }
                    }
                }
                return total;
            }
        }

        private static ZipArchive OpenZip(string path)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ModelChunkBytes);
            try { return new ZipArchive(stream, ZipArchiveMode.Read, false); }
            catch { stream.Dispose(); throw; }
        }

        private static long FolderBytes(string folder)
        {
            long total = 0;
            try
            {
                foreach (string f in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { /* vanished meanwhile */ }
                }
            }
            catch { /* listed with what was counted */ }
            return total;
        }

        private static void TryDeleteFolder(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) Directory.Delete(path, true); }
            catch (Exception e) { UnityEngine.Debug.LogWarning($"[LocalAPIServer] Could not delete {path}: {e.Message}"); }
        }

        private static string BuildSidecarJson(string name, string format, float unitScale, string up,
            bool georeferenced, double[] origin, int? projectId, string source, long bytes, bool package)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new StringBuilder(384);
            sb.Append('{');
            JsonText.AppendField(sb, "name", name); sb.Append(',');
            sb.Append("\"format\":\"").Append(format).Append("\",");
            sb.Append("\"unitScale\":").Append(unitScale.ToString("R", inv)).Append(',');
            sb.Append("\"up\":\"").Append(up).Append("\",");
            sb.Append("\"georeferenced\":").Append(georeferenced ? "true" : "false").Append(',');
            if (origin != null)
            {
                sb.Append("\"originE\":").Append(origin[0].ToString("R", inv)).Append(',');
                sb.Append("\"originN\":").Append(origin[1].ToString("R", inv)).Append(',');
                sb.Append("\"originH\":").Append(origin[2].ToString("R", inv)).Append(',');
            }
            if (projectId.HasValue) sb.Append("\"projectId\":").Append(projectId.Value).Append(',');
            JsonText.AppendField(sb, "sourceFile", source); sb.Append(',');
            sb.Append("\"bytes\":").Append(bytes).Append(',');
            if (package) sb.Append("\"package\":true,");
            JsonText.AppendField(sb, "uploadedAt", DateTime.UtcNow.ToString("o", inv));
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>GET /api/models — every model file with its sidecar fields flattened in.</summary>
        private int HandleModelsList(HttpListenerResponse response)
        {
            var sb = new StringBuilder(512);
            sb.Append('[');
            bool first = true;
            if (!string.IsNullOrEmpty(modelsDirectory) && Directory.Exists(modelsDirectory))
            {
                string[] files = Directory.GetFiles(modelsDirectory);
                Array.Sort(files, StringComparer.Ordinal);
                foreach (string file in files)
                {
                    string ext = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
                    if (Array.IndexOf(ModelFormats, ext) < 0) continue; // skip sidecars, temp uploads
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('{');
                    JsonText.AppendField(sb, "file", Path.GetFileName(file)); sb.Append(',');
                    JsonText.AppendField(sb, "path", file); sb.Append(',');
                    long size = 0;
                    try { size = new FileInfo(file).Length; } catch { /* listed without a size */ }
                    sb.Append("\"bytes\":").Append(size).Append(',');
                    string inner = ReadSidecarInner(file + ".json");
                    if (inner != null)
                    {
                        sb.Append("\"hasSidecar\":true,").Append(inner);
                    }
                    else
                    {
                        sb.Append("\"hasSidecar\":false,");
                        JsonText.AppendField(sb, "name", Path.GetFileNameWithoutExtension(file)); sb.Append(',');
                        sb.Append("\"format\":\"").Append(ext).Append('"');
                    }
                    sb.Append('}');
                }

                // Textured packages: one folder each, "file" is "<folder>/<model>.obj".
                string[] folders = Directory.GetDirectories(modelsDirectory);
                Array.Sort(folders, StringComparer.Ordinal);
                foreach (string folder in folders)
                {
                    string obj = Models.ModelStore.PackageObj(folder);
                    if (obj == null) continue; // staging folders, strays
                    string folderName = Path.GetFileName(folder);
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('{');
                    JsonText.AppendField(sb, "file", folderName + "/" + Path.GetFileName(obj)); sb.Append(',');
                    JsonText.AppendField(sb, "path", obj); sb.Append(',');
                    JsonText.AppendField(sb, "folder", folderName); sb.Append(',');
                    sb.Append("\"bytes\":").Append(FolderBytes(folder)).Append(',');
                    string inner = ReadSidecarInner(Path.Combine(folder, Models.ModelStore.PackageSidecar));
                    if (inner != null)
                    {
                        // "bytes" above is the folder now; the sidecar's own is the unpacked size at upload.
                        sb.Append("\"hasSidecar\":true,");
                        if (inner.IndexOf("\"package\"", StringComparison.Ordinal) < 0) sb.Append("\"package\":true,");
                        sb.Append(inner);
                    }
                    else
                    {
                        sb.Append("\"hasSidecar\":false,\"package\":true,");
                        JsonText.AppendField(sb, "name", folderName); sb.Append(',');
                        sb.Append("\"format\":\"obj\"");
                    }
                    sb.Append('}');
                }
            }
            sb.Append(']');
            SendJson(response, 200, sb.ToString());
            return 200;
        }

        /// <summary>The sidecar's fields without the outer braces, or null when absent/unusable.</summary>
        private static string ReadSidecarInner(string sidecarPath)
        {
            try
            {
                if (!File.Exists(sidecarPath)) return null;
                string json = File.ReadAllText(sidecarPath, Encoding.UTF8).Trim();
                if (json.Length < 2 || json[0] != '{' || json[json.Length - 1] != '}') return null;
                string inner = json.Substring(1, json.Length - 2).Trim();
                return inner.Length > 0 ? inner : null;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[LocalAPIServer] Sidecar unreadable {sidecarPath}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// DELETE /api/model/{name} — removes &lt;name&gt;.obj|dxf (or the exact file name) and its
        /// sidecar, or else the package folder models/&lt;name&gt;/ with everything in it.
        /// </summary>
        private int HandleModelDelete(string requestedName, HttpListenerResponse response)
        {
            string name = SanitizeModelName(requestedName);
            if (string.IsNullOrEmpty(name)) { SendJson(response, 400, "{\"error\":\"Missing model name\"}"); return 400; }
            if (string.IsNullOrEmpty(modelsDirectory) || !Directory.Exists(modelsDirectory))
            {
                SendJson(response, 404, "{\"error\":\"Model not found\",\"name\":\"" + EscapeJson(name) + "\"}");
                return 404;
            }

            var candidates = new List<string>();
            string ownExt = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
            if (Array.IndexOf(ModelFormats, ownExt) >= 0) candidates.Add(Path.Combine(modelsDirectory, name));
            foreach (string f in ModelFormats) candidates.Add(Path.Combine(modelsDirectory, name + "." + f));

            string deleted = null;
            string displayName = null;
            string deleteError = null;
            lock (modelLock)
            {
                foreach (string candidate in candidates)
                {
                    if (!File.Exists(candidate)) continue;
                    try
                    {
                        File.Delete(candidate);
                        TryDelete(candidate + ".json");
                        deleted = candidate;
                        displayName = Path.GetFileNameWithoutExtension(candidate);
                    }
                    catch (Exception e) { deleteError = e.Message; }
                    break;
                }

                // A textured package is the folder named after the model (the upload's safe name).
                string folder = Path.Combine(modelsDirectory, name);
                if (deleted == null && deleteError == null && Directory.Exists(folder)
                    && Models.ModelStore.PackageObj(folder) != null)
                {
                    try
                    {
                        Directory.Delete(folder, true);
                        deleted = folder;
                        displayName = name;
                    }
                    catch (Exception e) { deleteError = e.Message; }
                }
            }
            if (deleteError != null)
            {
                SendJson(response, 500, "{\"error\":\"Could not delete model: " + EscapeJson(deleteError) + "\"}");
                return 500;
            }
            if (deleted == null)
            {
                SendJson(response, 404, "{\"error\":\"Model not found\",\"name\":\"" + EscapeJson(name) + "\"}");
                return 404;
            }

            lock (lockObject) pendingActions.Enqueue(() => OnModelDeleted?.Invoke(displayName));
            var sb = new StringBuilder(128);
            sb.Append("{\"success\":true,");
            JsonText.AppendField(sb, "name", displayName); sb.Append(',');
            JsonText.AppendField(sb, "file", Path.GetFileName(deleted));
            sb.Append('}');
            SendJson(response, 200, sb.ToString());
            return 200;
        }

        /// <summary>Header values arrive Latin-1; the PC tool percent-encodes UTF-8 names (Greek).</summary>
        private static string DecodeHeader(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            try { return Uri.UnescapeDataString(value); }
            catch { return value; }
        }

        /// <summary>
        /// A file-system-safe display name: path separators, drive colons, control characters
        /// and other invalid file name characters are dropped; letters of any script stay.
        /// </summary>
        internal static string SanitizeModelName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (c < 32 || c == 127) continue;
                if (c == '/' || c == '\\' || c == ':') continue;
                if (Array.IndexOf(invalid, c) >= 0) continue;
                sb.Append(c);
            }
            // Leading dots too: ".extract-…" is the staging folder prefix ModelStore never lists.
            string clean = sb.ToString().Trim().Trim('.').Trim();
            if (clean == "." || clean == ".." || clean.Length == 0) return "";
            if (clean.Length > 120) clean = clean.Substring(0, 120).TrimEnd();
            return clean;
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch (Exception e) { UnityEngine.Debug.LogWarning($"[LocalAPIServer] Could not delete {path}: {e.Message}"); }
        }

        /// <summary>
        /// Null when the body exceeds the size cap; throws <see cref="BodyTimeoutException"/>
        /// when a client announces a body and never sends it, so no worker waits forever.
        /// </summary>
        private string ReadBody(HttpListenerRequest request)
        {
            if (request.ContentLength64 > MaxBodyBytes) return null;
            var read = Task.Run(() => ReadBodyBlocking(request));
            try
            {
                if (read.Wait(BodyReadTimeoutMs)) return read.Result;
            }
            catch (AggregateException e)
            {
                throw e.InnerException ?? e; // same surface as the former direct read
            }
            // The read fails once the connection is aborted; keep that from being unobserved.
            read.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            throw new BodyTimeoutException();
        }

        private string ReadBodyBlocking(HttpListenerRequest request)
        {
            // Count raw bytes (the cap is in bytes, not chars: Greek text is 2 bytes a char)
            // and decode once at the end.
            Stream input = request.InputStream;
            byte[] buffer = new byte[8192];
            using (var ms = new MemoryStream())
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (ms.Length + read > MaxBodyBytes) return null;
                    ms.Write(buffer, 0, read);
                }
                Encoding encoding = BodyEncoding(request.ContentType);
                byte[] bytes = ms.GetBuffer();
                int length = (int)ms.Length;
                // Skip a UTF-8 byte-order mark: JsonUtility would choke on it.
                int offset = length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                return encoding.GetString(bytes, offset, length - offset);
            }
        }

        /// <summary>
        /// The charset named in Content-Type, else UTF-8. HttpListenerRequest.ContentEncoding
        /// falls back to the platform default when there is no charset (Python requests and
        /// curl send none), which garbles Greek labels where that default is not UTF-8.
        /// </summary>
        private static Encoding BodyEncoding(string contentType)
        {
            if (!string.IsNullOrEmpty(contentType))
            {
                foreach (string part in contentType.Split(';'))
                {
                    string p = part.Trim();
                    if (!p.StartsWith("charset=", StringComparison.OrdinalIgnoreCase)) continue;
                    string name = p.Substring(8).Trim().Trim('"');
                    try { return Encoding.GetEncoding(name); }
                    catch (ArgumentException) { break; }
                }
            }
            return new UTF8Encoding(false);
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

        private class BodyTimeoutException : Exception
        {
            public BodyTimeoutException() : base("Request body not received in time") { }
        }

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
