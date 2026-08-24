using UnityEngine;
using QuestMarkerApp.API;
using QuestMarkerApp.Markers;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// Main controller — API, anchors, calibration, sessions, GPS, highlight ray.
    /// </summary>
    public class MarkerAppController : MonoBehaviour
    {
        [Header("Core Components")]
        [SerializeField] private CoordinateAPIService apiService;
        [SerializeField] private LocalAPIServer localServer;
        [SerializeField] private MarkerManager markerManager;
        [SerializeField] private SpatialAnchorManager spatialAnchorManager;
        [SerializeField] private CoordinateCalibration calibrationSystem;
        [SerializeField] private SessionManager sessionManager;

        [Header("Configuration")]
        [SerializeField] private bool useLocalServer = true;
        [SerializeField] private bool useRemotePolling = false;
        [SerializeField] private bool useWebSocketPush = false;
        [SerializeField] private string remoteServerUrl = "http://192.168.1.100:5000";
        [SerializeField] private bool highlightWithControllerRay = true;

#if !MOBILE_PREVIEW
        [Header("Input")]
        [SerializeField] private OVRInput.Button calibrationButton = OVRInput.Button.One;
        [SerializeField] private OVRInput.Button clearMarkersButton = OVRInput.Button.Two;
        [SerializeField] private OVRInput.Button startCalibrationButton = OVRInput.Button.Three;
        [SerializeField] private OVRInput.Button undoButton = OVRInput.Button.Four;
#endif

        private bool isInitialized;
        private bool useRealCoordinates;
        private WebSocketPushClient wsClient;
#if !MOBILE_PREVIEW
        private OVRCameraRig cachedCameraRig;
#endif

        public bool UseRealCoordinates => useRealCoordinates;
        public bool IsInitialized => isInitialized;
        public SessionManager Session => sessionManager;
        // SessionManager owns the GPS origin — session import updates it there,
        // so keeping a duplicate field here would go stale.
        public GPSReference GpsOrigin => sessionManager != null ? sessionManager.GpsOrigin : null;

        private void Awake()
        {
            ValidateComponents();
        }

        private void Start()
        {
            Initialize();
        }

        private void Update()
        {
            HandleInput();
            UpdateHighlightRay();
        }

        private void OnDestroy()
        {
            Cleanup();
        }

        private void ValidateComponents()
        {
            if (apiService == null)
            {
                apiService = FindObjectOfType<CoordinateAPIService>();
                if (apiService == null && (useRemotePolling || useWebSocketPush))
                    apiService = gameObject.AddComponent<CoordinateAPIService>();
            }

            if (localServer == null)
            {
                localServer = FindObjectOfType<LocalAPIServer>();
                if (localServer == null && useLocalServer)
                    localServer = gameObject.AddComponent<LocalAPIServer>();
            }

            if (markerManager == null)
            {
                markerManager = FindObjectOfType<MarkerManager>() ?? gameObject.AddComponent<MarkerManager>();
            }

            if (spatialAnchorManager == null)
            {
                spatialAnchorManager = FindObjectOfType<SpatialAnchorManager>() ?? gameObject.AddComponent<SpatialAnchorManager>();
            }

            if (calibrationSystem == null)
            {
                calibrationSystem = FindObjectOfType<CoordinateCalibration>() ?? gameObject.AddComponent<CoordinateCalibration>();
            }

            if (sessionManager == null)
            {
                sessionManager = FindObjectOfType<SessionManager>() ?? gameObject.AddComponent<SessionManager>();
            }

            if (localServer != null)
                localServer.BindComponents(markerManager, calibrationSystem, sessionManager, spatialAnchorManager);
        }

        private void Initialize()
        {
            if (spatialAnchorManager != null)
            {
                spatialAnchorManager.OnReferenceAnchorCreated += OnReferenceAnchorReady;
                spatialAnchorManager.OnReferenceAnchorLoaded += OnReferenceAnchorReady;
            }

            if (useLocalServer && localServer != null)
            {
                localServer.OnCoordinatesReceived += OnCoordinatesReceived;
                localServer.OnServerStarted += OnLocalServerStarted;
                localServer.OnServerError += OnServerError;
                localServer.OnCalibrationReceived += OnCalibrationReceived;
                localServer.OnStartCalibrationMode += OnStartCalibrationMode;
                localServer.OnDeleteMarkerRequested += OnDeleteMarkerRequested;
                localServer.OnResetCalibrationRequested += OnResetCalibrationRequested;
                localServer.OnUndoRequested += OnUndoRequested;
                localServer.OnSessionImportJson += OnSessionImportJson;
                localServer.OnSessionExportRequested += OnSessionExportRequested;
                localServer.OnGpsOriginSet += OnGpsOriginSet;
            }

            if (useRemotePolling && apiService != null)
            {
                apiService.OnCoordinatesReceived += OnCoordinatesReceived;
                apiService.OnConnectionError += OnServerError;
                apiService.Configure(remoteServerUrl);
            }

            if (useWebSocketPush)
            {
                wsClient = GetComponent<WebSocketPushClient>() ?? gameObject.AddComponent<WebSocketPushClient>();
                wsClient.OnCoordinatesReceived += OnCoordinatesReceived;
                wsClient.Connect(remoteServerUrl);
            }

            if (markerManager != null)
            {
                markerManager.OnMarkerCreated += OnMarkerCreated;
                markerManager.OnMarkersCleared += OnMarkersCleared;
            }

            if (calibrationSystem != null)
            {
                calibrationSystem.OnCalibrationComplete += OnCalibrationComplete;
                if (calibrationSystem.IsCalibrated)
                    useRealCoordinates = true;
            }

            isInitialized = true;
            Debug.Log("[MarkerApp] Initialization complete (v1.2)");
        }

        private void Cleanup()
        {
            if (spatialAnchorManager != null)
            {
                spatialAnchorManager.OnReferenceAnchorCreated -= OnReferenceAnchorReady;
                spatialAnchorManager.OnReferenceAnchorLoaded -= OnReferenceAnchorReady;
            }

            if (localServer != null)
            {
                localServer.OnCoordinatesReceived -= OnCoordinatesReceived;
                localServer.OnServerStarted -= OnLocalServerStarted;
                localServer.OnServerError -= OnServerError;
                localServer.OnCalibrationReceived -= OnCalibrationReceived;
                localServer.OnStartCalibrationMode -= OnStartCalibrationMode;
                localServer.OnDeleteMarkerRequested -= OnDeleteMarkerRequested;
                localServer.OnResetCalibrationRequested -= OnResetCalibrationRequested;
                localServer.OnUndoRequested -= OnUndoRequested;
                localServer.OnSessionImportJson -= OnSessionImportJson;
                localServer.OnSessionExportRequested -= OnSessionExportRequested;
                localServer.OnGpsOriginSet -= OnGpsOriginSet;
            }

            if (apiService != null)
            {
                apiService.OnCoordinatesReceived -= OnCoordinatesReceived;
                apiService.OnConnectionError -= OnServerError;
            }

            if (wsClient != null)
                wsClient.OnCoordinatesReceived -= OnCoordinatesReceived;

            if (markerManager != null)
            {
                markerManager.OnMarkerCreated -= OnMarkerCreated;
                markerManager.OnMarkersCleared -= OnMarkersCleared;
            }

            if (calibrationSystem != null)
                calibrationSystem.OnCalibrationComplete -= OnCalibrationComplete;
        }

        private void HandleInput()
        {
#if MOBILE_PREVIEW
            if (Input.GetKeyDown(KeyCode.C)) ClearAllMarkers();
            if (Input.GetKeyDown(KeyCode.R)) CalibrateReferencePoint();
            if (Input.GetKeyDown(KeyCode.X)) StartCalibrationMode();
            if (Input.GetKeyDown(KeyCode.Z)) Undo();
            if (Input.GetKeyDown(KeyCode.S)) SaveSession();
#else
            if (OVRInput.GetDown(calibrationButton)) CalibrateReferencePoint();
            if (OVRInput.GetDown(clearMarkersButton)) ClearAllMarkers();
            if (OVRInput.GetDown(startCalibrationButton)) StartCalibrationMode();
            if (OVRInput.GetDown(undoButton)) Undo();
#endif
        }

        private void UpdateHighlightRay()
        {
            if (!highlightWithControllerRay || markerManager == null) return;
            if (calibrationSystem != null && calibrationSystem.IsCalibrating) return;

            Transform controller = null;
#if !MOBILE_PREVIEW
            if (cachedCameraRig == null) cachedCameraRig = FindObjectOfType<OVRCameraRig>();
            if (cachedCameraRig != null) controller = cachedCameraRig.rightHandAnchor;
#endif
            if (controller == null && Camera.main != null)
                controller = Camera.main.transform;
            if (controller == null) return;

            markerManager.HighlightAlongRay(new Ray(controller.position, controller.forward));
        }

        public void StartCalibrationMode()
        {
            if (calibrationSystem != null)
            {
                calibrationSystem.StartCalibration();
                useRealCoordinates = true;
                Debug.Log("[MarkerApp] Calibration mode started");
            }
        }

        public void CalibrateReferencePoint()
        {
            if (spatialAnchorManager != null)
            {
                spatialAnchorManager.ResetReferenceAnchor();
                Debug.Log("[MarkerApp] Reference point calibrated");
            }
        }

        public void ClearAllMarkers()
        {
            sessionManager?.PushHistory();
            markerManager?.ClearAllMarkers();
        }

        public void Undo()
        {
            if (sessionManager != null && sessionManager.Undo())
                Debug.Log("[MarkerApp] Undo applied");
        }

        public void SaveSession()
        {
            if (sessionManager == null) return;
            sessionManager.SaveToFile();
            if (localServer != null)
                localServer.SetLastExportedSession(sessionManager.ExportJson());
        }

        public void AddCoordinate(float x, float y, float z, string label = null, string color = null)
        {
            var data = new CoordinateData
            {
                id = System.Guid.NewGuid().ToString("N"),
                x = x, y = y, z = z,
                label = label, color = color, scale = 0.1f,
                timestamp = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            OnCoordinatesReceived(new CoordinateResponse
            {
                coordinates = new[] { data },
                clearExisting = false
            });
        }

        public string[] GetServerUrls()
        {
            return localServer != null ? localServer.GetServerUrls() : System.Array.Empty<string>();
        }

        private void OnReferenceAnchorReady(Transform anchor)
        {
            markerManager?.SetReferencePoint(anchor);
            Debug.Log($"[MarkerApp] Reference anchor ready at {anchor.position}");
        }

        private void OnLocalServerStarted()
        {
            Debug.Log($"[MarkerApp] Local server: {string.Join(", ", GetServerUrls())}");
        }

        private void OnCoordinatesReceived(CoordinateResponse response)
        {
            if (markerManager == null || response == null) return;

            sessionManager?.PushHistory();

            if (response.coordinates != null)
            {
                bool applyCal = useRealCoordinates && calibrationSystem != null && calibrationSystem.IsCalibrated;
                for (int i = 0; i < response.coordinates.Length; i++)
                {
                    var c = response.coordinates[i];
                    if (c == null) continue;
                    response.coordinates[i] = CoordinateTransform.ToLocalMeters(
                        c, sessionManager?.GpsOrigin, calibrationSystem, applyCal);
                }
            }

            markerManager.ProcessCoordinates(response);
        }

        private void OnCalibrationReceived(CalibrationRequest request)
        {
            if (calibrationSystem == null || request == null) return;

            if (request.quickCalibrate)
                calibrationSystem.QuickCalibrate(request.ToVector3(), request.rotationY);
            else
                calibrationSystem.SetRealCoordinatesForLastPoint(request.ToVector3());

            useRealCoordinates = true;
            Debug.Log($"[MarkerApp] Calibration point: ({request.realX}, {request.realY}, {request.realZ})");
        }

        private void OnStartCalibrationMode() => StartCalibrationMode();

        private void OnDeleteMarkerRequested(string id)
        {
            if (markerManager == null || string.IsNullOrEmpty(id)) return;
            sessionManager?.PushHistory();
            markerManager.RemoveMarker(id);
        }

        private void OnResetCalibrationRequested()
        {
            calibrationSystem?.ResetCalibration();
            useRealCoordinates = false;
        }

        private void OnUndoRequested() => Undo();

        private void OnSessionImportJson(string json)
        {
            sessionManager?.LoadFromJson(json);
        }

        private void OnSessionExportRequested()
        {
            if (sessionManager != null && localServer != null)
                localServer.SetLastExportedSession(sessionManager.ExportJson());
        }

        private void OnGpsOriginSet(GPSReference origin)
        {
            if (sessionManager != null)
            {
                sessionManager.GpsOrigin = origin;
                sessionManager.Crs = "gps";
            }
            Debug.Log($"[MarkerApp] GPS origin set: {origin.latitude}, {origin.longitude}");
        }

        private void OnCalibrationComplete(CalibrationData data)
        {
            useRealCoordinates = true;
            Debug.Log($"[MarkerApp] Calibration complete residual={data?.residualRmsMeters:F3}m");
        }

        private void OnServerError(string error) => Debug.LogWarning($"[MarkerApp] Server error: {error}");
        private void OnMarkerCreated(Marker marker) => Debug.Log($"[MarkerApp] Marker: {marker.MarkerId}");
        private void OnMarkersCleared() => Debug.Log("[MarkerApp] Markers cleared");
    }
}
