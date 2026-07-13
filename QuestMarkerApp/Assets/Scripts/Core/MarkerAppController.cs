using UnityEngine;
using QuestMarkerApp.API;
using QuestMarkerApp.Markers;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// Main controller that orchestrates all components of the marker system
    /// </summary>
    public class MarkerAppController : MonoBehaviour
    {
        [Header("Core Components")]
        [SerializeField] private CoordinateAPIService apiService;
        [SerializeField] private LocalAPIServer localServer;
        [SerializeField] private MarkerManager markerManager;
        [SerializeField] private SpatialAnchorManager spatialAnchorManager;
        [SerializeField] private CoordinateCalibration calibrationSystem;

        [Header("Configuration")]
        [SerializeField] private bool useLocalServer = true;
        [SerializeField] private bool useRemotePolling = false;
        [SerializeField] private string remoteServerUrl = "http://192.168.1.100:5000";

#if !MOBILE_PREVIEW
        [Header("Input")]
        [SerializeField] private OVRInput.Button calibrationButton = OVRInput.Button.One;
        [SerializeField] private OVRInput.Button clearMarkersButton = OVRInput.Button.Two;
        [SerializeField] private OVRInput.Button startCalibrationButton = OVRInput.Button.Three;
#endif

        private bool isInitialized = false;
        private bool useRealCoordinates = false;

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
        }

        private void OnDestroy()
        {
            Cleanup();
        }

        private void ValidateComponents()
        {
            // Auto-find or create components if not assigned
            if (apiService == null)
            {
                apiService = FindObjectOfType<CoordinateAPIService>();
                if (apiService == null && useRemotePolling)
                {
                    apiService = gameObject.AddComponent<CoordinateAPIService>();
                }
            }

            if (localServer == null)
            {
                localServer = FindObjectOfType<LocalAPIServer>();
                if (localServer == null && useLocalServer)
                {
                    localServer = gameObject.AddComponent<LocalAPIServer>();
                }
            }

            if (markerManager == null)
            {
                markerManager = FindObjectOfType<MarkerManager>();
                if (markerManager == null)
                {
                    markerManager = gameObject.AddComponent<MarkerManager>();
                }
            }

            if (spatialAnchorManager == null)
            {
                spatialAnchorManager = FindObjectOfType<SpatialAnchorManager>();
                if (spatialAnchorManager == null)
                {
                    spatialAnchorManager = gameObject.AddComponent<SpatialAnchorManager>();
                }
            }

            if (calibrationSystem == null)
            {
                calibrationSystem = FindObjectOfType<CoordinateCalibration>();
                if (calibrationSystem == null)
                {
                    calibrationSystem = gameObject.AddComponent<CoordinateCalibration>();
                }
            }
        }

        private void Initialize()
        {
            // Subscribe to spatial anchor events
            if (spatialAnchorManager != null)
            {
                spatialAnchorManager.OnReferenceAnchorCreated += OnReferenceAnchorReady;
                spatialAnchorManager.OnReferenceAnchorLoaded += OnReferenceAnchorReady;
            }

            // Subscribe to API events
            if (useLocalServer && localServer != null)
            {
                localServer.OnCoordinatesReceived += OnCoordinatesReceived;
                localServer.OnServerStarted += OnLocalServerStarted;
                localServer.OnServerError += OnServerError;
                localServer.OnCalibrationReceived += OnCalibrationReceived;
                localServer.OnStartCalibrationMode += OnStartCalibrationMode;
            }

            if (useRemotePolling && apiService != null)
            {
                apiService.OnCoordinatesReceived += OnCoordinatesReceived;
                apiService.OnConnectionError += OnServerError;
                apiService.Configure(remoteServerUrl);
            }

            // Subscribe to marker events
            if (markerManager != null)
            {
                markerManager.OnMarkerCreated += OnMarkerCreated;
                markerManager.OnMarkersCleared += OnMarkersCleared;
            }

            isInitialized = true;
            Debug.Log("[MarkerApp] Initialization complete");
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
            }

            if (apiService != null)
            {
                apiService.OnCoordinatesReceived -= OnCoordinatesReceived;
                apiService.OnConnectionError -= OnServerError;
            }

            if (markerManager != null)
            {
                markerManager.OnMarkerCreated -= OnMarkerCreated;
                markerManager.OnMarkersCleared -= OnMarkersCleared;
            }
        }

        private void HandleInput()
        {
#if MOBILE_PREVIEW
            // Mobile preview: Quest controller buttons are replaced by the on-screen
            // buttons in MobilePreviewController. Keyboard shortcuts help when testing
            // in the Editor (Play mode) with a mouse/keyboard.
            if (Input.GetKeyDown(KeyCode.C)) ClearAllMarkers();
            if (Input.GetKeyDown(KeyCode.R)) CalibrateReferencePoint();
            if (Input.GetKeyDown(KeyCode.X)) StartCalibrationMode();
#else
            // Calibration button - reset reference anchor
            if (OVRInput.GetDown(calibrationButton))
            {
                CalibrateReferencePoint();
            }

            // Clear all markers
            if (OVRInput.GetDown(clearMarkersButton))
            {
                ClearAllMarkers();
            }

            // Start calibration mode (X button)
            if (OVRInput.GetDown(startCalibrationButton))
            {
                StartCalibrationMode();
            }
#endif
        }

        /// <summary>
        /// Start calibration mode for real-world coordinate mapping
        /// </summary>
        public void StartCalibrationMode()
        {
            if (calibrationSystem != null)
            {
                calibrationSystem.StartCalibration();
                useRealCoordinates = true;
                Debug.Log("[MarkerApp] Calibration mode started - point at known location and press trigger");
            }
        }

        /// <summary>
        /// Calibrate the reference point at current controller position
        /// </summary>
        public void CalibrateReferencePoint()
        {
            if (spatialAnchorManager != null)
            {
                spatialAnchorManager.ResetReferenceAnchor();
                Debug.Log("[MarkerApp] Reference point calibrated");
            }
        }

        /// <summary>
        /// Clear all markers
        /// </summary>
        public void ClearAllMarkers()
        {
            if (markerManager != null)
            {
                markerManager.ClearAllMarkers();
            }
        }

        /// <summary>
        /// Manually add a coordinate
        /// </summary>
        public void AddCoordinate(float x, float y, float z, string label = null, string color = null)
        {
            CoordinateData data = new CoordinateData
            {
                id = System.Guid.NewGuid().ToString(),
                x = x,
                y = y,
                z = z,
                label = label,
                color = color,
                scale = 0.1f,
                timestamp = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            CoordinateResponse response = new CoordinateResponse
            {
                coordinates = new CoordinateData[] { data },
                clearExisting = false
            };

            OnCoordinatesReceived(response);
        }

        /// <summary>
        /// Get server URLs for connecting from external apps
        /// </summary>
        public string[] GetServerUrls()
        {
            if (localServer != null)
            {
                return localServer.GetServerUrls();
            }
            return new string[0];
        }

        // Event Handlers
        private void OnReferenceAnchorReady(Transform anchor)
        {
            if (markerManager != null)
            {
                markerManager.SetReferencePoint(anchor);
            }
            Debug.Log($"[MarkerApp] Reference anchor ready at {anchor.position}");
        }

        private void OnLocalServerStarted()
        {
            string[] urls = GetServerUrls();
            Debug.Log($"[MarkerApp] Local server started. Connect to: {string.Join(", ", urls)}");
        }

        private void OnCoordinatesReceived(CoordinateResponse response)
        {
            if (markerManager != null)
            {
                // If calibrated, convert real-world coordinates to Quest coordinates
                if (useRealCoordinates && calibrationSystem != null && calibrationSystem.IsCalibrated)
                {
                    foreach (var coord in response.coordinates)
                    {
                        Vector3 realPos = coord.ToVector3();
                        Vector3 questPos = calibrationSystem.RealToQuest(realPos);
                        coord.x = questPos.x;
                        coord.y = questPos.y;
                        coord.z = questPos.z;
                    }
                }
                
                markerManager.ProcessCoordinates(response);
            }
        }

        private void OnCalibrationReceived(CalibrationRequest request)
        {
            if (calibrationSystem == null) return;
            
            if (request.quickCalibrate)
            {
                // Quick single-point calibration
                calibrationSystem.QuickCalibrate(request.ToVector3(), request.rotationY);
            }
            else
            {
                // Add calibration point with real coordinates
                calibrationSystem.SetRealCoordinatesForLastPoint(request.ToVector3());
            }
            
            useRealCoordinates = true;
            Debug.Log($"[MarkerApp] Calibration point received: ({request.realX}, {request.realY}, {request.realZ})");
        }

        private void OnStartCalibrationMode()
        {
            StartCalibrationMode();
        }

        private void OnServerError(string error)
        {
            Debug.LogWarning($"[MarkerApp] Server error: {error}");
        }

        private void OnMarkerCreated(Marker marker)
        {
            Debug.Log($"[MarkerApp] Marker created: {marker.MarkerId}");
        }

        private void OnMarkersCleared()
        {
            Debug.Log("[MarkerApp] All markers cleared");
        }
    }
}
