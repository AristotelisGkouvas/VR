using UnityEngine;
using UnityEngine.UI;
using TMPro;
using QuestMarkerApp.Markers;
using QuestMarkerApp.API;

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

        [Header("Info Display")]
        [SerializeField] private TextMeshProUGUI instructionsText;

        [Header("References")]
        [SerializeField] private LocalAPIServer localServer;
        [SerializeField] private MarkerManager markerManager;

        [Header("Settings")]
        [SerializeField] private float uiDistance = 2f;
        [SerializeField] private bool followHead = true;
        [SerializeField] private float followSpeed = 2f;

        private Transform headTransform;
        private bool showUI = true;

        private void Start()
        {
            if (Camera.main != null)
            {
                headTransform = Camera.main.transform;
            }

            FindReferences();
            SubscribeToEvents();
            UpdateUI();
            SetupInstructions();
        }

        private void Update()
        {
            if (followHead && headTransform != null && mainCanvas != null)
            {
                UpdateCanvasPosition();
            }

#if !MOBILE_PREVIEW
            // Toggle UI visibility with thumbstick press
            if (OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick))
            {
                ToggleUI();
            }
#endif
        }

        private void OnDestroy()
        {
            UnsubscribeFromEvents();
        }

        private void FindReferences()
        {
            if (localServer == null)
            {
                localServer = FindObjectOfType<LocalAPIServer>();
            }

            if (markerManager == null)
            {
                markerManager = FindObjectOfType<MarkerManager>();
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

            // Create status panel
            statusPanel = CreatePanel("StatusPanel", new Vector2(0, 200), new Vector2(700, 150));
            serverStatusText = CreateText(statusPanel.transform, "ServerStatus", new Vector2(0, 40), "Server: Starting...");
            serverUrlText = CreateText(statusPanel.transform, "ServerUrl", new Vector2(0, 0), "URL: ...");
            markerCountText = CreateText(statusPanel.transform, "MarkerCount", new Vector2(0, -40), "Markers: 0");

            // Create instructions panel
            infoPanel = CreatePanel("InfoPanel", new Vector2(0, -100), new Vector2(700, 250));
            instructionsText = CreateText(infoPanel.transform, "Instructions", Vector2.zero, "");
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
            text.fontSize = 24;
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
        }

        private void UpdateCanvasPosition()
        {
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
                markerCountText.text = $"Active Markers: {markerManager.ActiveMarkerCount}";
            }
        }

        private void SetupInstructions()
        {
            if (instructionsText != null)
            {
                instructionsText.text =
                    "<b>Οδηγίες Χρήσης:</b>\n\n" +
                    "• Στείλτε POST request στο /api/coordinates\n" +
                    "• Button A: Επαναβαθμονόμηση σημείου αναφοράς\n" +
                    "• Button B: Καθαρισμός όλων των markers\n" +
                    "• Thumbstick: Εμφάνιση/Απόκρυψη UI\n\n" +
                    "<b>Παράδειγμα JSON:</b>\n" +
                    "{\"coordinates\": [{\"x\": 1.0, \"y\": 0.5, \"z\": 2.0}]}";
            }
        }

        private void ToggleUI()
        {
            showUI = !showUI;
            if (mainCanvas != null)
            {
                mainCanvas.gameObject.SetActive(showUI);
            }
        }

        // Event Handlers
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
    }
}
