using UnityEngine;
using UnityEngine.UI;
using TMPro;
using QuestMarkerApp.Markers;
using QuestMarkerApp.API;
using QuestMarkerApp.Core;

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// UI Manager for displaying status, server URL, and marker information
    /// </summary>
    public class UIManager : MonoBehaviour
    {
        [Header("UI Panels")]
        [SerializeField] private Canvas mainCanvas;
        [SerializeField] private GameObject statusPanel;
        [SerializeField] private GameObject infoPanel;

        [Header("Status Display")]
        [SerializeField] private TextMeshProUGUI serverStatusText;
        [SerializeField] private TextMeshProUGUI serverUrlText;
        [SerializeField] private TextMeshProUGUI markerCountText;
        [SerializeField] private TextMeshProUGUI connectionStatusText;
        [SerializeField] private TextMeshProUGUI calibrationStatusText;

        [Header("Info Display")]
        [SerializeField] private TextMeshProUGUI instructionsText;

        [Header("References")]
        [SerializeField] private LocalAPIServer localServer;
        [SerializeField] private MarkerManager markerManager;
        [SerializeField] private CoordinateCalibration calibrationSystem;

        [Header("Settings")]
        [SerializeField] private float uiDistance = 2f;
        [SerializeField] private bool followHead = true;
        [SerializeField] private float followSpeed = 2f;
        [SerializeField] private float statusRefreshInterval = 1f;

        private Transform headTransform;
        private bool showUI = true;
        private bool suppressed;
        private float nextRefresh;

        private void Start()
        {
            if (Camera.main != null)
            {
                headTransform = Camera.main.transform;
            }

            FindReferences();
            // Suppress() can run before Start (MarkerAppController's init order): the canvas
            // created just above then stayed up for good, floating at the world origin, since a
            // suppressed Update never touches it again.
            if (suppressed)
            {
                if (mainCanvas != null) mainCanvas.gameObject.SetActive(false);
                return;
            }
            SubscribeToEvents();
            UpdateUI();
            SetupInstructions();
        }

        private void Update()
        {
            // Suppressed for good by MarkerAppController: no canvas follow, no input polling.
            if (suppressed) return;

            if (followHead && headTransform != null && mainCanvas != null)
            {
                UpdateCanvasPosition();
            }

#if !MOBILE_PREVIEW
            if (OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick))
            {
                ToggleUI();
            }
#endif

            if (Time.unscaledTime >= nextRefresh)
            {
                nextRefresh = Time.unscaledTime + statusRefreshInterval;
                UpdateUI();
            }
        }

        private void OnDestroy()
        {
            UnsubscribeFromEvents();
        }

        private void FindReferences()
        {
            if (localServer == null)
            {
                localServer = FindAnyObjectByType<LocalAPIServer>();
            }

            if (markerManager == null)
            {
                markerManager = FindAnyObjectByType<MarkerManager>();
            }

            if (calibrationSystem == null)
            {
                calibrationSystem = FindAnyObjectByType<CoordinateCalibration>();
            }

            if (mainCanvas == null)
            {
                CreateDefaultCanvas();
            }
        }

        private void CreateDefaultCanvas()
        {
            GameObject canvasObj = new GameObject("MarkerAppCanvas");
            mainCanvas = canvasObj.AddComponent<Canvas>();
            mainCanvas.renderMode = RenderMode.WorldSpace;

            RectTransform rt = canvasObj.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(800, 600);
            rt.localScale = Vector3.one * 0.001f;

            canvasObj.AddComponent<CanvasScaler>();
            canvasObj.AddComponent<GraphicRaycaster>();

            statusPanel = CreatePanel("StatusPanel", new Vector2(0, 200), new Vector2(700, 200));
            serverStatusText = CreateText(statusPanel.transform, "ServerStatus", new Vector2(0, 60), "Server: Starting...");
            serverUrlText = CreateText(statusPanel.transform, "ServerUrl", new Vector2(0, 20), "URL: ...");
            markerCountText = CreateText(statusPanel.transform, "MarkerCount", new Vector2(0, -20), "Markers: 0");
            calibrationStatusText = CreateText(statusPanel.transform, "CalibrationStatus", new Vector2(0, -60), "Calibration: -");

            infoPanel = CreatePanel("InfoPanel", new Vector2(0, -120), new Vector2(700, 280));
            instructionsText = CreateText(infoPanel.transform, "Instructions", Vector2.zero, "");
            if (instructionsText != null)
            {
                var instrRt = instructionsText.GetComponent<RectTransform>();
                if (instrRt != null) instrRt.sizeDelta = new Vector2(650, 260);
            }
        }

        private GameObject CreatePanel(string name, Vector2 position, Vector2 size)
        {
            GameObject panel = new GameObject(name);
            panel.transform.SetParent(mainCanvas.transform, false);

            RectTransform rt = panel.AddComponent<RectTransform>();
            rt.anchoredPosition = position;
            rt.sizeDelta = size;

            Image bg = panel.AddComponent<Image>();
            bg.color = new Color(0, 0, 0, 0.7f);

            return panel;
        }

        private TextMeshProUGUI CreateText(Transform parent, string name, Vector2 position, string content)
        {
            GameObject textObj = new GameObject(name);
            textObj.transform.SetParent(parent, false);

            RectTransform rt = textObj.AddComponent<RectTransform>();
            rt.anchoredPosition = position;
            rt.sizeDelta = new Vector2(650, 40);

            TextMeshProUGUI text = textObj.AddComponent<TextMeshProUGUI>();
            text.text = content;
            text.fontSize = 22;
            text.color = Color.white;
            text.alignment = TextAlignmentOptions.Center;

            return text;
        }

        private void SubscribeToEvents()
        {
            if (localServer != null)
            {
                localServer.OnServerStarted += OnServerStarted;
                localServer.OnServerStopped += OnServerStopped;
                localServer.OnServerError += OnServerError;
            }

            if (markerManager != null)
            {
                markerManager.OnMarkerCreated += OnMarkerChanged;
                markerManager.OnMarkerRemoved += OnMarkerChanged;
                markerManager.OnMarkersCleared += OnMarkersCleared;
            }

            if (calibrationSystem != null)
            {
                calibrationSystem.OnCalibrationComplete += OnCalibrationChanged;
                calibrationSystem.OnCalibrationReset += OnCalibrationReset;
            }
        }

        private void UnsubscribeFromEvents()
        {
            if (localServer != null)
            {
                localServer.OnServerStarted -= OnServerStarted;
                localServer.OnServerStopped -= OnServerStopped;
                localServer.OnServerError -= OnServerError;
            }

            if (markerManager != null)
            {
                markerManager.OnMarkerCreated -= OnMarkerChanged;
                markerManager.OnMarkerRemoved -= OnMarkerChanged;
                markerManager.OnMarkersCleared -= OnMarkersCleared;
            }

            if (calibrationSystem != null)
            {
                calibrationSystem.OnCalibrationComplete -= OnCalibrationChanged;
                calibrationSystem.OnCalibrationReset -= OnCalibrationReset;
            }
        }

        private void UpdateCanvasPosition()
        {
            if (suppressed && mainCanvas != null && mainCanvas.gameObject.activeSelf)
                mainCanvas.gameObject.SetActive(false);
            Vector3 targetPosition = headTransform.position + headTransform.forward * uiDistance;
            targetPosition.y = headTransform.position.y;

            mainCanvas.transform.position = Vector3.Lerp(
                mainCanvas.transform.position,
                targetPosition,
                Time.deltaTime * followSpeed
            );

            mainCanvas.transform.LookAt(headTransform);
            mainCanvas.transform.Rotate(0, 180, 0);
        }

        private void UpdateUI()
        {
            if (serverStatusText != null)
            {
                bool isRunning = localServer != null && localServer.IsRunning;
                serverStatusText.text = $"Server: {(isRunning ? "<color=green>Running</color>" : "<color=red>Stopped</color>")}";
            }

            if (serverUrlText != null && localServer != null)
            {
                string[] urls = localServer.GetServerUrls();
                if (urls.Length > 0)
                {
                    serverUrlText.text = $"URL: {urls[0]}/api/coordinates";
                }
            }

            if (markerCountText != null && markerManager != null)
            {
                markerCountText.text = $"Active Markers: {markerManager.ActiveMarkerCount}/{markerManager.MaxMarkers}";
            }

            if (calibrationStatusText != null)
            {
                if (calibrationSystem == null)
                {
                    calibrationStatusText.text = "Calibration: n/a";
                }
                else if (calibrationSystem.IsCalibrating)
                {
                    calibrationStatusText.text =
                        $"Calibration: <color=yellow>In progress</color> ({calibrationSystem.CapturedPointCount} pts)\n" +
                        $"<size=80%>{calibrationSystem.CurrentStepMessage}</size>";
                }
                else if (calibrationSystem.IsCalibrated)
                {
                    calibrationStatusText.text =
                        $"Calibration: <color=green>OK</color> RMS {calibrationSystem.LastResidualRms:F3}m";
                }
                else
                {
                    calibrationStatusText.text = "Calibration: <color=grey>Not set</color>";
                }
            }
        }

        private void SetupInstructions()
        {
            if (instructionsText != null)
            {
                instructionsText.text =
                    "<b>Οδηγίες Χρήσης:</b>\n\n" +
                    "• POST /api/coordinates — σημεία\n" +
                    "• GET /api/coordinates — λίστα σημείων\n" +
                    "• DELETE /api/coordinates/{id} — διαγραφή\n" +
                    "• X: Μενού\n" +
                    "• Σκανδάλη: Επιλογή αισθητήρα";
            }
        }

        /// <summary>Hide this HUD for good; the main menu now carries its status.</summary>
        public void Suppress()
        {
            suppressed = true;
            showUI = false;
            if (mainCanvas != null) mainCanvas.gameObject.SetActive(false);
        }

        private void ToggleUI()
        {
            if (suppressed) return;
            showUI = !showUI;
            if (mainCanvas != null)
            {
                mainCanvas.gameObject.SetActive(showUI);
            }
        }

        private void OnServerStarted()
        {
            UpdateUI();
            Debug.Log("[UI] Server started");
        }

        private void OnServerStopped()
        {
            UpdateUI();
        }

        private void OnServerError(string error)
        {
            if (connectionStatusText != null)
            {
                connectionStatusText.text = $"<color=red>Error: {error}</color>";
            }
        }

        private void OnMarkerChanged(Marker marker)
        {
            UpdateUI();
        }

        private void OnMarkersCleared()
        {
            UpdateUI();
        }

        private void OnCalibrationChanged(CalibrationData data)
        {
            UpdateUI();
        }

        private void OnCalibrationReset()
        {
            UpdateUI();
        }
    }
}
