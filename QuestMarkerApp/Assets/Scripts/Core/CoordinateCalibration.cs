using System;
using UnityEngine;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// Handles calibration between real-world coordinates and Quest local space
    /// Allows user to point at known reference points with controllers
    /// </summary>
    public class CoordinateCalibration : MonoBehaviour
    {
        [Header("Calibration Settings")]
        [SerializeField] private int minimumCalibrationPoints = 2;
        [SerializeField] private int maximumCalibrationPoints = 4;
        [SerializeField] private bool useAveraging = true;

        [Header("Controller Reference")]
        [SerializeField] private Transform rightController;
        [SerializeField] private Transform leftController;
#if !MOBILE_PREVIEW
        [SerializeField] private OVRInput.Button calibrateButton = OVRInput.Button.PrimaryIndexTrigger;
#endif

        [Header("Visual Feedback")]
        [SerializeField] private LineRenderer laserPointer;
        [SerializeField] private GameObject calibrationMarkerPrefab;
        [SerializeField] private float laserLength = 10f;

        [Header("Debug")]
        [SerializeField] private bool showDebugInfo = true;

        // Events
        public event Action<CalibrationData> OnCalibrationComplete;
        public event Action<int> OnCalibrationPointAdded;
        public event Action OnCalibrationReset;

        // Calibration state
        private bool isCalibrating = false;
        private CalibrationData currentCalibration;
        private System.Collections.Generic.List<CalibrationPoint> calibrationPoints = new System.Collections.Generic.List<CalibrationPoint>();

        public bool IsCalibrated => currentCalibration != null && currentCalibration.isValid;
        public CalibrationData CurrentCalibration => currentCalibration;

        private void Start()
        {
            SetupLaserPointer();
            LoadSavedCalibration();
        }

        private void Update()
        {
            if (isCalibrating)
            {
                UpdateLaserPointer();

#if !MOBILE_PREVIEW
                // Check for calibration button press
                if (OVRInput.GetDown(calibrateButton, OVRInput.Controller.RTouch))
                {
                    CaptureCalibrationPoint();
                }
#endif
            }
        }

        /// <summary>
        /// Start calibration mode
        /// </summary>
        public void StartCalibration()
        {
            isCalibrating = true;
            calibrationPoints.Clear();

            if (laserPointer != null)
            {
                laserPointer.enabled = true;
            }

            Debug.Log("[Calibration] Calibration mode started. Point at known locations and press trigger.");
        }

        /// <summary>
        /// Stop calibration mode
        /// </summary>
        public void StopCalibration()
        {
            isCalibrating = false;

            if (laserPointer != null)
            {
                laserPointer.enabled = false;
            }
        }

        /// <summary>
        /// Add a calibration point with known real-world coordinates
        /// Called from API when user provides real coordinates for the captured point
        /// </summary>
        public void SetRealCoordinatesForLastPoint(Vector3 realWorldCoords, CoordinateSystem system = CoordinateSystem.Local)
        {
            if (calibrationPoints.Count == 0)
            {
                Debug.LogWarning("[Calibration] No captured point to assign coordinates to");
                return;
            }

            var lastPoint = calibrationPoints[calibrationPoints.Count - 1];
            lastPoint.realWorldPosition = realWorldCoords;
            lastPoint.coordinateSystem = system;
            lastPoint.hasRealCoordinates = true;
            calibrationPoints[calibrationPoints.Count - 1] = lastPoint;

            Debug.Log($"[Calibration] Point {calibrationPoints.Count} set: Quest({lastPoint.questPosition}) -> Real({realWorldCoords})");

            OnCalibrationPointAdded?.Invoke(calibrationPoints.Count);

            // Check if we have enough points
            if (calibrationPoints.Count >= minimumCalibrationPoints)
            {
                CalculateCalibration();
            }
        }

        /// <summary>
        /// Add a calibration point with both positions known
        /// </summary>
        public void AddCalibrationPoint(Vector3 questPosition, Vector3 realWorldPosition, CoordinateSystem system = CoordinateSystem.Local)
        {
            CalibrationPoint point = new CalibrationPoint
            {
                questPosition = questPosition,
                realWorldPosition = realWorldPosition,
                coordinateSystem = system,
                hasRealCoordinates = true,
                timestamp = DateTime.Now
            };

            calibrationPoints.Add(point);
            OnCalibrationPointAdded?.Invoke(calibrationPoints.Count);

            if (calibrationPoints.Count >= minimumCalibrationPoints)
            {
                CalculateCalibration();
            }
        }

        /// <summary>
        /// Quick calibration with single point (origin alignment)
        /// </summary>
        public void QuickCalibrate(Vector3 realWorldOrigin, float rotationYDegrees = 0)
        {
            Transform controller = GetActiveController();
            if (controller == null)
            {
                Debug.LogError("[Calibration] No controller available");
                return;
            }

            currentCalibration = new CalibrationData
            {
                originOffset = controller.position - realWorldOrigin,
                rotationOffset = Quaternion.Euler(0, rotationYDegrees - controller.eulerAngles.y, 0),
                scale = Vector3.one,
                isValid = true,
                calibrationTime = DateTime.Now,
                pointsUsed = 1
            };

            SaveCalibration();
            OnCalibrationComplete?.Invoke(currentCalibration);

            Debug.Log($"[Calibration] Quick calibration complete. Origin offset: {currentCalibration.originOffset}");
        }

        /// <summary>
        /// Convert real-world coordinates to Quest local coordinates
        /// </summary>
        public Vector3 RealToQuest(Vector3 realWorldPos)
        {
            if (!IsCalibrated)
            {
                Debug.LogWarning("[Calibration] Not calibrated, returning raw coordinates");
                return realWorldPos;
            }

            // Apply inverse transformation
            Vector3 scaled = Vector3.Scale(realWorldPos, currentCalibration.scale);
            Vector3 rotated = currentCalibration.rotationOffset * scaled;
            Vector3 translated = rotated + currentCalibration.originOffset;

            return translated;
        }

        /// <summary>
        /// Convert Quest local coordinates to real-world coordinates
        /// </summary>
        public Vector3 QuestToReal(Vector3 questPos)
        {
            if (!IsCalibrated)
            {
                Debug.LogWarning("[Calibration] Not calibrated, returning raw coordinates");
                return questPos;
            }

            // Apply transformation
            Vector3 translated = questPos - currentCalibration.originOffset;
            Vector3 rotated = Quaternion.Inverse(currentCalibration.rotationOffset) * translated;
            Vector3 scaled = new Vector3(
                rotated.x / currentCalibration.scale.x,
                rotated.y / currentCalibration.scale.y,
                rotated.z / currentCalibration.scale.z
            );

            return scaled;
        }

        /// <summary>
        /// Reset calibration
        /// </summary>
        public void ResetCalibration()
        {
            currentCalibration = null;
            calibrationPoints.Clear();
            PlayerPrefs.DeleteKey("CalibrationData");
            PlayerPrefs.Save();

            OnCalibrationReset?.Invoke();
            Debug.Log("[Calibration] Calibration reset");
        }

        private void CaptureCalibrationPoint()
        {
            Transform controller = GetActiveController();
            if (controller == null) return;

            // Raycast to find where user is pointing
            Vector3 pointPosition;
            if (Physics.Raycast(controller.position, controller.forward, out RaycastHit hit, laserLength))
            {
                pointPosition = hit.point;
            }
            else
            {
                // Use point at laser end
                pointPosition = controller.position + controller.forward * laserLength;
            }

            CalibrationPoint point = new CalibrationPoint
            {
                questPosition = pointPosition,
                hasRealCoordinates = false,
                timestamp = DateTime.Now
            };

            calibrationPoints.Add(point);

            // Visual feedback
            if (calibrationMarkerPrefab != null)
            {
                GameObject marker = Instantiate(calibrationMarkerPrefab, pointPosition, Quaternion.identity);
                Destroy(marker, 30f); // Auto-destroy after 30 seconds
            }

            Debug.Log($"[Calibration] Captured point {calibrationPoints.Count} at Quest position: {pointPosition}");
            Debug.Log("[Calibration] Now send real-world coordinates via API: POST /api/calibrate");
        }

        private void CalculateCalibration()
        {
            // Filter points with real coordinates
            var validPoints = calibrationPoints.FindAll(p => p.hasRealCoordinates);
            if (validPoints.Count < minimumCalibrationPoints)
            {
                Debug.LogWarning($"[Calibration] Need at least {minimumCalibrationPoints} points with real coordinates");
                return;
            }

            // Calculate transformation using least squares (simplified for 2 points)
            if (validPoints.Count == 1)
            {
                // Single point - only translation
                var p = validPoints[0];
                currentCalibration = new CalibrationData
                {
                    originOffset = p.questPosition - p.realWorldPosition,
                    rotationOffset = Quaternion.identity,
                    scale = Vector3.one,
                    isValid = true,
                    calibrationTime = DateTime.Now,
                    pointsUsed = 1
                };
            }
            else
            {
                // Multiple points - calculate rotation and translation
                Vector3 questCentroid = Vector3.zero;
                Vector3 realCentroid = Vector3.zero;

                foreach (var p in validPoints)
                {
                    questCentroid += p.questPosition;
                    realCentroid += p.realWorldPosition;
                }
                questCentroid /= validPoints.Count;
                realCentroid /= validPoints.Count;

                // Calculate rotation (simplified - assumes Y-up and mainly horizontal rotation)
                if (validPoints.Count >= 2)
                {
                    Vector3 questDir = (validPoints[1].questPosition - validPoints[0].questPosition).normalized;
                    Vector3 realDir = (validPoints[1].realWorldPosition - validPoints[0].realWorldPosition).normalized;

                    // Project to XZ plane for Y rotation
                    questDir.y = 0;
                    realDir.y = 0;

                    if (questDir.magnitude > 0.01f && realDir.magnitude > 0.01f)
                    {
                        float angle = Vector3.SignedAngle(realDir.normalized, questDir.normalized, Vector3.up);
                        currentCalibration = new CalibrationData
                        {
                            originOffset = questCentroid - Quaternion.Euler(0, angle, 0) * realCentroid,
                            rotationOffset = Quaternion.Euler(0, angle, 0),
                            scale = Vector3.one,
                            isValid = true,
                            calibrationTime = DateTime.Now,
                            pointsUsed = validPoints.Count
                        };
                    }
                }
            }

            if (currentCalibration != null && currentCalibration.isValid)
            {
                SaveCalibration();
                StopCalibration();
                OnCalibrationComplete?.Invoke(currentCalibration);
                Debug.Log($"[Calibration] Calibration complete with {validPoints.Count} points");
            }
        }

        private Transform GetActiveController()
        {
            if (rightController != null)
                return rightController;

#if !MOBILE_PREVIEW
            // Try to find OVR controller
            var ovrRig = FindObjectOfType<OVRCameraRig>();
            if (ovrRig != null)
            {
                return ovrRig.rightHandAnchor;
            }
#endif

            return Camera.main?.transform;
        }

        private void SetupLaserPointer()
        {
            if (laserPointer == null)
            {
                GameObject laserObj = new GameObject("CalibrationLaser");
                laserObj.transform.SetParent(transform);

                laserPointer = laserObj.AddComponent<LineRenderer>();
                laserPointer.startWidth = 0.005f;
                laserPointer.endWidth = 0.002f;
                laserPointer.positionCount = 2;
                laserPointer.material = new Material(Shader.Find("Sprites/Default"));
                laserPointer.startColor = Color.green;
                laserPointer.endColor = Color.yellow;
                laserPointer.enabled = false;
            }
        }

        private void UpdateLaserPointer()
        {
            Transform controller = GetActiveController();
            if (controller == null || laserPointer == null) return;

            Vector3 start = controller.position;
            Vector3 end = start + controller.forward * laserLength;

            if (Physics.Raycast(start, controller.forward, out RaycastHit hit, laserLength))
            {
                end = hit.point;
            }

            laserPointer.SetPosition(0, start);
            laserPointer.SetPosition(1, end);
        }

        private void SaveCalibration()
        {
            if (currentCalibration == null) return;

            string json = JsonUtility.ToJson(currentCalibration);
            PlayerPrefs.SetString("CalibrationData", json);
            PlayerPrefs.Save();
        }

        private void LoadSavedCalibration()
        {
            if (PlayerPrefs.HasKey("CalibrationData"))
            {
                string json = PlayerPrefs.GetString("CalibrationData");
                currentCalibration = JsonUtility.FromJson<CalibrationData>(json);

                if (currentCalibration != null && currentCalibration.isValid)
                {
                    Debug.Log("[Calibration] Loaded saved calibration");
                }
            }
        }
    }

    /// <summary>
    /// Coordinate system types
    /// </summary>
    public enum CoordinateSystem
    {
        Local,      // Meters from a local origin
        GPS,        // Latitude/Longitude/Altitude
        UTM,        // Universal Transverse Mercator
        ECEF        // Earth-Centered Earth-Fixed
    }

    /// <summary>
    /// Single calibration point
    /// </summary>
    [Serializable]
    public struct CalibrationPoint
    {
        public Vector3 questPosition;
        public Vector3 realWorldPosition;
        public CoordinateSystem coordinateSystem;
        public bool hasRealCoordinates;
        public DateTime timestamp;
    }

    /// <summary>
    /// Calibration transformation data
    /// </summary>
    [Serializable]
    public class CalibrationData
    {
        public Vector3 originOffset;
        public Quaternion rotationOffset;
        public Vector3 scale;
        public bool isValid;
        public DateTime calibrationTime;
        public int pointsUsed;
    }
}
