using System;
using UnityEngine;
using QuestMarkerApp.API;
using QuestMarkerApp.Markers;
using QuestMarkerApp.Models;
using QuestMarkerApp.UI;

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
        [SerializeField] private ProjectAlignment projectAlignment;

        [Header("Configuration")]
        [SerializeField] private bool useLocalServer = true;
        [SerializeField] private bool useRemotePolling = false;
        [SerializeField] private bool useWebSocketPush = false;
        [SerializeField] private string remoteServerUrl = "http://192.168.1.100:5000";
        [Tooltip("The PC bridge's --api-key (what its clients must send). Empty: no key is sent.")]
        [SerializeField] private string remoteApiKey = "";
        [SerializeField] private bool highlightWithControllerRay = true;


        private bool isInitialized;
        /// <summary>Menu went down over a running route: released early it opens the menu, held it stops the route.</summary>
        private bool routeMenuPending;
        private bool useRealCoordinates;
        private WebSocketPushClient wsClient;
        private MainMenu mainMenu;
        private TerrawebClient terraweb;
        private FlowPanel flowPanel;
        private SensorCard sensorCard;
        private SensorNavigator navigator;
        private InspectionRoute route;
        private Action<string> onModelReceived, onModelDeleted;
        // Live pushes arrive several times a second; one undo step covers each burst.
        private const float BatchHistoryInterval = 2f;
        private float lastBatchHistoryTime = float.NegativeInfinity;
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
            L.Init(); // cache the language on the main thread before any worker reads it
            ValidateComponents();
        }

        private void Start()
        {
            Initialize();
        }

        private void Update()
        {
            // A project replaces the legacy calibration flow; never leave its laser running.
            if (calibrationSystem != null && calibrationSystem.IsCalibrating && projectAlignment != null
                && projectAlignment.HasProject)
                calibrationSystem.StopCalibration();
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
                apiService = FindAnyObjectByType<CoordinateAPIService>();
                if (apiService == null && useRemotePolling)
                    apiService = gameObject.AddComponent<CoordinateAPIService>();
            }
            // Push mode has its own client; a polling service left running would deliver every
            // batch a second time. Disabled before its Start, it never auto-starts polling.
            if (apiService != null && !useRemotePolling)
            {
                apiService.StopPolling();
                apiService.enabled = false;
            }

            if (localServer == null)
            {
                localServer = FindAnyObjectByType<LocalAPIServer>();
                if (localServer == null && useLocalServer)
                    localServer = gameObject.AddComponent<LocalAPIServer>();
            }

            if (markerManager == null)
            {
                markerManager = FindAnyObjectByType<MarkerManager>() ?? gameObject.AddComponent<MarkerManager>();
            }

            if (spatialAnchorManager == null)
            {
                spatialAnchorManager = FindAnyObjectByType<SpatialAnchorManager>() ?? gameObject.AddComponent<SpatialAnchorManager>();
            }

            if (calibrationSystem == null)
            {
                calibrationSystem = FindAnyObjectByType<CoordinateCalibration>() ?? gameObject.AddComponent<CoordinateCalibration>();
            }

            if (sessionManager == null)
            {
                sessionManager = FindAnyObjectByType<SessionManager>() ?? gameObject.AddComponent<SessionManager>();
            }

            if (projectAlignment == null)
            {
                projectAlignment = FindAnyObjectByType<ProjectAlignment>() ?? gameObject.AddComponent<ProjectAlignment>();
            }

            if (localServer != null)
                localServer.BindComponents(markerManager, calibrationSystem, sessionManager, spatialAnchorManager, projectAlignment);
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
                localServer.OnProjectReceived += OnProjectReceived;
                localServer.OnProjectResetRequested += OnProjectResetRequested;
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
                // Never the headset's own key: that would hand it to whatever URL is configured here.
                wsClient.Connect(remoteServerUrl, remoteApiKey);
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

            Child<VrPointer>("VrPointer");
            Child<HoldConfirm>("HoldConfirm");
            Child<BoundaryControl>("BoundaryControl");
            Child<DisplayPerformance>("DisplayPerformance");
            Child<AnchorFieldTest>("AnchorFieldTest");
            terraweb = gameObject.GetComponent<TerrawebClient>() ?? gameObject.AddComponent<TerrawebClient>();
            flowPanel = Child<FlowPanel>("FlowPanel");
            var flow = Child<TerrawebFlow>("TerrawebFlow");
            flow.Init(terraweb, flowPanel, projectAlignment, markerManager);
            navigator = Child<SensorNavigator>("SensorNavigator");
            navigator.Init(markerManager);
            var displacementView = Child<DisplacementView>("DisplacementView");
            displacementView.Init(projectAlignment, markerManager);
            Child<HoverInfo>("HoverInfo").Init(projectAlignment, markerManager);
            sensorCard = Child<SensorCard>("SensorCard");
            sensorCard.Init(terraweb, () => projectAlignment != null ? projectAlignment.TerrawebProjectId : 0);
            sensorCard.OnNavigateRequested += (id, point) => navigator.Navigate(id, point);
            // Field tools the menu offers: 30-day ribbons by the icons, polled alert changes,
            // an ordered inspection walk. The menu finds them by type.
            Child<HistoryRibbons>("HistoryRibbons").Init(projectAlignment, markerManager);
            Child<LimitBars>("LimitBars").Init(projectAlignment, markerManager);
            Child<LiveAlerts>("LiveAlerts").Init(terraweb, projectAlignment);
            route = Child<InspectionRoute>("InspectionRoute");
            route.Init(projectAlignment, markerManager, flowPanel);

            // Design model (OBJ / DXF, IFC converted to OBJ on the PC) laid over the structure
            // through the same alignment as the sensors. A file sent from the PC loads at once;
            // the last one shown comes back on launch.
            ModelOverlay.Importers["dxf"] = (path, options, progress, cancel) => DxfImporter.Load(path, options, progress, cancel);
            var model = Child<ModelOverlay>("ModelOverlay");
            model.Init(projectAlignment, markerManager);
            // The project's sensors as dots on the tabletop / life-size preview of that model.
            Child<ModelSensors>("ModelSensors").Init(model, projectAlignment, markerManager, sensorCard);
            // The watch on the off hand: mode, alignment trust, data age, connection.
            Child<StatusChip>("StatusChip").Init(projectAlignment, terraweb, flow, model);
            Child<ComfortVignette>("ComfortVignette");
            Child<AlarmBeacons>("AlarmBeacons").Init(markerManager);
            Child<MeasureTool>("MeasureTool").Init(projectAlignment, markerManager);
            if (localServer != null)
            {
                // Kept so Cleanup can unsubscribe: the server may outlive this controller.
                onModelReceived = path =>
                {
                    if (model == null) return;
                    string full = System.IO.Path.GetFullPath(path);
                    ModelStore.ModelInfo info = ModelStore.List().Find(m =>
                        string.Equals(System.IO.Path.GetFullPath(m.Path), full, StringComparison.OrdinalIgnoreCase));
                    if (info == null) return;
                    model.Load(info);
                    Feedback.Play(Feedback.Sound.Ping);
                };
                onModelDeleted = name =>
                {
                    if (model == null) return;
                    if (model.LoadedName != null && string.Equals(model.LoadedName, name, StringComparison.OrdinalIgnoreCase))
                        model.Unload();
                };
                localServer.OnModelReceived += onModelReceived;
                localServer.OnModelDeleted += onModelDeleted;
            }
            ModelStore.ModelInfo last = ModelStore.Find(ModelStore.LastLoadedName);
            if (last != null) model.Load(last);

            mainMenu = Child<MainMenu>("MainMenu");
            mainMenu.Init(this, projectAlignment, markerManager, localServer, terraweb, flow, navigator);
            // Voice: "βρες Κ5", "δείξε συναγερμούς", "διαδρομή επιθεώρησης"... (typed until the
            // Wit.ai engine is installed; see VoiceRecognizers.cs).
            var voice = Child<VoiceCommands>("VoiceCommands");
            voice.Init(this, projectAlignment, markerManager, navigator, displacementView, mainMenu, flowPanel);
            voice.OnRouteRequested = arg =>
            {
                // "Stop" words never get here: VoiceCommands handles them, route included.
                string a = arg ?? "";
                if (InspectionRoute.IsActive) route.Skip();
                else if (a.Contains("συναγερμ") || a.Contains("προειδοπ") || a.Contains("alarm") || a.Contains("alert")) route.StartAlerts();
                else route.StartAll();
            };
            var hintsGo = new GameObject("ControllerHints");
            hintsGo.transform.SetParent(transform, false);
            hintsGo.AddComponent<ControllerHints>().Init(projectAlignment);
            // First-use guide: a coach card over the off hand, on a first run or from Help.
            Child<Onboarding>("Onboarding").Init(projectAlignment, markerManager, terraweb, flowPanel);
            // The main menu carries the server/marker status the old floating HUD showed.
            FindAnyObjectByType<UIManager>()?.Suppress();

            isInitialized = true;
            Debug.Log("[MarkerApp] Initialization complete (v1.2)");
            StartCoroutine(OpenMenuWhenEmpty());
        }

        // An empty room on launch gave a first-time user nothing to go on: show the menu
        // (after tracking settles, so it lands in front of the user) unless a project is up.
        private System.Collections.IEnumerator OpenMenuWhenEmpty()
        {
            yield return new WaitForSeconds(1.5f);
            // Not while the guide is teaching the menu button: opening it here would do the step for them.
            if ((projectAlignment == null || !projectAlignment.HasProject) && !UiState.AnyModal && !Onboarding.HoldsMenu)
                mainMenu?.Open(userInitiated: false); // keeps the first-run controller labels showing
        }

        private T Child<T>(string name) where T : Component
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            return go.AddComponent<T>();
        }

        /// <summary>Pick a sensor of the aligned project, nearest first, and navigate to it.</summary>
        public void OpenSensorPicker()
        {
            if (projectAlignment == null || !projectAlignment.HasProject || flowPanel == null) return;
            Camera cam = Camera.main;
            Vector3 here = cam != null ? cam.transform.position : Vector3.zero;

            var entries = new System.Collections.Generic.List<(float dist, FlowItem item)>();
            foreach (ProjectPoint p in projectAlignment.Points)
            {
                string markerId = ProjectAlignment.MarkerPrefix + p.id;
                if (!markerManager.ActiveMarkers.TryGetValue(markerId, out Marker m) || m == null) continue;
                Vector3 d = m.transform.position - here;
                float dist = new Vector2(d.x, d.z).magnitude;
                entries.Add((dist, new FlowItem
                {
                    Label = string.IsNullOrEmpty(p.label) ? p.id : p.label,
                    Sub = string.IsNullOrEmpty(p.typeLabel) ? $"H {p.h:F2}" : $"{p.typeLabel} · H {p.h:F2}",
                    Value = dist < 10f ? $"{dist:F1} m" : $"{dist:F0} m",
                    Icon = string.IsNullOrEmpty(p.icon) ? "sensor-point" : p.icon,
                    Color = ColorUtility.TryParseHtmlString(p.color ?? "", out Color c) ? c : Brand.Primary,
                    Tag = (markerId, p)
                }));
            }
            entries.Sort((a, b) => a.dist.CompareTo(b.dist));
            var items = entries.ConvertAll(e => e.item);

            flowPanel.ShowList(L.T("Πλοήγηση", "Navigate"), L.T("Διάλεξε αισθητήρα", "Pick a sensor"), items, item =>
            {
                var (markerId, point) = ((string, ProjectPoint))item.Tag;
                flowPanel.Close();
                navigator.Navigate(markerId, point);
            }, flowPanel.Close);
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
                localServer.OnProjectReceived -= OnProjectReceived;
                localServer.OnProjectResetRequested -= OnProjectResetRequested;
                if (onModelReceived != null) localServer.OnModelReceived -= onModelReceived;
                if (onModelDeleted != null) localServer.OnModelDeleted -= onModelDeleted;
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
            if (Input.GetKeyDown(KeyCode.M)) mainMenu?.Toggle();
            if (UiState.InputBlocked) return;
            if (Input.GetKeyDown(KeyCode.C)) ClearAllMarkers();
            if (Input.GetKeyDown(KeyCode.R)) CalibrateReferencePoint();
            if (Input.GetKeyDown(KeyCode.X)) StartCalibrationMode();
            if (Input.GetKeyDown(KeyCode.Z)) Undo();
            if (Input.GetKeyDown(KeyCode.S)) SaveSession();
#else
            // The Menu button sits on the left controller only. With it pocketed, a click of
            // the remaining controller's stick opens the menu instead.
            bool pocketed = !OVRInput.IsControllerConnected(OVRInput.Controller.LTouch);
            bool menuPressed = OVRInput.GetDown(OVRInput.Button.Start)
                || (pocketed && OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.RTouch));
            bool menuHeld = OVRInput.Get(OVRInput.Button.Start)
                || (pocketed && OVRInput.Get(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.RTouch));
            // A running route ends on a held Menu (a ring fills); a short press opens the menu
            // over it, on release, so a brushed button no longer throws the route away.
            bool routeOnTop = route != null && UiState.Context(projectAlignment) == "route";
            if (routeOnTop || routeMenuPending)
            {
                if (menuPressed) routeMenuPending = true;
                if (routeMenuPending)
                {
                    if (HoldConfirm.Run("route", menuHeld, L.T("Διακοπή διαδρομής", "Stop route")))
                    {
                        routeMenuPending = false;
                        Feedback.Play(Feedback.Sound.Undo, 0.6f);
                        route.Stop();
                    }
                    else if (!menuHeld)
                    {
                        routeMenuPending = false;
                        Feedback.Play(Feedback.Sound.Tick);
                        mainMenu?.Toggle();
                    }
                    return;
                }
                menuPressed = false;
            }
            if (menuPressed)
            {
                Feedback.Play(Feedback.Sound.Tick);
                // Menu always gets you out: close whatever floats in front, else end a running
                // inspection route (its hint reads "Μενού: διακοπή"), else toggle the menu.
                if (VrKeyboard.IsOpen) VrKeyboard.CancelOpen();
                else if (FlowPanel.IsOpen) flowPanel.Close();
                else if (SensorCard.IsOpen) sensorCard.Close();
                else mainMenu?.Toggle();
                return;
            }
            // With only a card open, aiming at another sensor and pulling the trigger switches
            // the card to it (the pointer has first claim if the ray is on the card itself).
            if (SensorCard.IsOpen && !MainMenu.IsOpen && !FlowPanel.IsOpen && !VrKeyboard.IsOpen
                && !VrPointer.ConsumedThisFrame && Hands.TriggerDown())
            {
                Marker other = markerManager != null ? markerManager.Highlighted : null;
                if (other != null && other.MarkerId != sensorCard.MarkerId
                    && projectAlignment.TryGetPoint(other.MarkerId, out ProjectPoint otherPoint))
                {
                    Feedback.Play(Feedback.Sound.Click);
                    sensorCard.Open(otherPoint, other.MarkerId, other.transform);
                }
            }

            if (UiState.InputBlocked) return;

            if (Hands.OffHandPrimaryDown())
            {
                // X opens the main menu. The alignment panel opens by itself while the project
                // still needs aligning and otherwise only from the menu; X just closes it
                // (ProjectAlignment handles X while it is open). The legacy laser calibration
                // stays reachable only via POST /api/calibrate/start.
                Feedback.Play(Feedback.Sound.Tick);
                if (projectAlignment != null && projectAlignment.IsModeActive) projectAlignment.SetMode(false);
                else mainMenu?.Open();
                return;
            }

            // While aligning, B belongs to the project panel and A would move the
            // reference anchor out from under the placed control points.
            if (projectAlignment != null && projectAlignment.IsModeActive) return;

            // Aim at a sensor and pull the trigger for its card.
            if (projectAlignment != null && projectAlignment.HasProject && !MeasureTool.IsActive
                && !VrPointer.ConsumedThisFrame && Hands.TriggerDown())
            {
                Marker aimed = markerManager != null ? markerManager.Highlighted : null;
                if (aimed != null && projectAlignment.TryGetPoint(aimed.MarkerId, out ProjectPoint point))
                {
                    Feedback.Play(Feedback.Sound.Click);
                    Feedback.Buzz(Hands.Dominant, 0.35f, 0.04f);
                    sensorCard.Open(point, aimed.MarkerId, aimed.transform);
                }
            }

            // With a project loaded the raw shortcuts are too easy to hit by accident — A
            // would move the origin under an alignment, B wipe every sensor. The menu has them.
            if (projectAlignment != null && projectAlignment.HasProject) return;

            // Role-based like the controller hints: with the left hand dominant the labels
            // swap sides, and these must swap with them (B used to clear everything while the
            // badge on it read "Αναίρεση").
            // Held, with a ring: in thin gloves a brushed A or B moved the origin or wiped everything.
            if (HoldConfirm.Run("origin", Hands.DominantPrimaryHeld(), L.T("Αρχή εδώ", "Origin here"))) CalibrateReferencePoint();
            if (HoldConfirm.Run("clear", Hands.DominantSecondaryHeld(), L.T("Καθαρισμός", "Clear all"))) ClearAllMarkers();
            if (Hands.OffHandSecondaryDown()) Undo();
#endif
        }

        private void UpdateHighlightRay()
        {
            if (!highlightWithControllerRay || markerManager == null) return;
            if (calibrationSystem != null && calibrationSystem.IsCalibrating) return;
            // Clear rather than freeze the highlight, or a later trigger opens a stale card.
            if (projectAlignment != null && projectAlignment.IsModeActive) { markerManager.ClearHighlight(); return; }
            bool cardOnly = SensorCard.IsOpen && !MainMenu.IsOpen && !FlowPanel.IsOpen && !VrKeyboard.IsOpen;
            if (UiState.AnyModal && !cardOnly) { markerManager.ClearHighlight(); return; }

            Transform controller = null;
#if !MOBILE_PREVIEW
            if (cachedCameraRig == null) cachedCameraRig = FindAnyObjectByType<OVRCameraRig>();
            if (cachedCameraRig != null && Hands.DominantConnected) controller = Hands.DominantAnchor(cachedCameraRig);
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

            // A clear or a full-state sync always gets its own undo step, burst or not.
            if (response.clearExisting || response.removeMissing
                || Time.unscaledTime - lastBatchHistoryTime >= BatchHistoryInterval)
            {
                sessionManager?.PushHistory();
                lastBatchHistoryTime = Time.unscaledTime;
            }

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

        private void OnProjectReceived(ProjectRequest project)
        {
            if (projectAlignment == null) return;
            projectAlignment.SetProject(project);
            projectAlignment.OpenIfNeeded();
        }

        private void OnProjectResetRequested() => projectAlignment?.ResetPlacements();

        private void OnCalibrationComplete(CalibrationData data)
        {
            useRealCoordinates = true;
            Debug.Log($"[MarkerApp] Calibration complete residual={data?.residualRmsMeters:F3}m");
        }

        private void OnServerError(string error) => Debug.LogWarning($"[MarkerApp] Server error: {error}");
        private void OnMarkerCreated(Marker marker) { } // per-marker logging stalled bulk loads (stack traces on)
        private void OnMarkersCleared() => Debug.Log("[MarkerApp] Markers cleared");
    }
}
