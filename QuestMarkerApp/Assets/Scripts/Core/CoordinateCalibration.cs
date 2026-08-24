using System;
using System.Collections.Generic;
using UnityEngine;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// Handles calibration between real-world coordinates and Quest local space.
    /// Allows user to point at known reference points with controllers.
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
        public event Action<string> OnStepChanged;

        // Calibration state
        private bool isCalibrating = false;
        private CalibrationData currentCalibration;
        private readonly List<CalibrationPoint> calibrationPoints = new List<CalibrationPoint>();
        private float lastResidualRms;
        private string currentStepMessage = "Idle";

        public bool IsCalibrated => currentCalibration != null && currentCalibration.isValid;
        public bool IsCalibrating => isCalibrating;
        public CalibrationData CurrentCalibration => currentCalibration;
        public int CapturedPointCount => calibrationPoints.Count;
        public float LastResidualRms => lastResidualRms;
        public string CurrentStepMessage => currentStepMessage;
        public int PointsWithRealCoords
        {
            get
            {
                int n = 0;
                for (int i = 0; i < calibrationPoints.Count; i++)
                {
                    if (calibrationPoints[i].hasRealCoordinates) n++;
                }
                return n;
            }
        }

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
            lastResidualRms = 0f;

            if (laserPointer != null)
            {
                laserPointer.enabled = true;
            }

            SetStep($"Step 1/{minimumCalibrationPoints}: Point at a known location and press trigger");
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
        /// Snapshot for API / UI status reporting.
        /// </summary>
        public CalibrationStatus GetStatus()
        {
            return new CalibrationStatus
            {
                isCalibrating = isCalibrating,
                isCalibrated = IsCalibrated,
                capturedPoints = calibrationPoints.Count,
                pointsWithRealCoords = PointsWithRealCoords,
                pointsUsed = currentCalibration != null ? currentCalibration.pointsUsed : 0,
                minimumRequired = minimumCalibrationPoints
            };
        }

        /// <summary>
        /// Add a calibration point with known real-world coordinates.
        /// Called from API when user provides real coordinates for the captured point.
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

            int withReal = PointsWithRealCoords;
            if (withReal >= minimumCalibrationPoints)
            {
                SetStep("Computing transform…");
                CalculateCalibration();
            }
            else
            {
                SetStep($"Step {withReal + 1}/{minimumCalibrationPoints}: Capture next point (trigger), then send real coords");
            }
        }

        /// <summary>
        /// Add a calibration point with both positions known
        /// </summary>
        public void AddCalibrationPoint(Vector3 questPosition, Vector3 realWorldPosition, CoordinateSystem system = CoordinateSystem.Local)
        {
            if (calibrationPoints.Count >= maximumCalibrationPoints)
            {
                Debug.LogWarning($"[Calibration] Maximum of {maximumCalibrationPoints} points reached");
                return;
            }

            CalibrationPoint point = new CalibrationPoint
            {
                questPosition = questPosition,
                realWorldPosition = realWorldPosition,
                coordinateSystem = system,
                hasRealCoordinates = true,
                timestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
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
                calibrationTimeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                pointsUsed = 1
            };
            currentCalibration.SyncQuaternionFields();

            lastResidualRms = 0f;
            SaveCalibration();
            StopCalibration();
            SetStep($"Quick calibration OK (residual n/a, single point)");
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

            currentCalibration.EnsureQuaternion();

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

            currentCalibration.EnsureQuaternion();

            Vector3 translated = questPos - currentCalibration.originOffset;
            Vector3 rotated = Quaternion.Inverse(currentCalibration.rotationOffset) * translated;

            float sx = Mathf.Abs(currentCalibration.scale.x) > 1e-6f ? currentCalibration.scale.x : 1f;
            float sy = Mathf.Abs(currentCalibration.scale.y) > 1e-6f ? currentCalibration.scale.y : 1f;
            float sz = Mathf.Abs(currentCalibration.scale.z) > 1e-6f ? currentCalibration.scale.z : 1f;

            return new Vector3(rotated.x / sx, rotated.y / sy, rotated.z / sz);
        }

        /// <summary>
        /// Convert GPS lat/lon (degrees) to local meters relative to a GPS reference,
        /// then apply the real-world → Quest transform if calibrated.
        /// </summary>
        public Vector3 GpsToQuest(double latitude, double longitude, double altitude, GPSReferenceOrigin origin)
        {
            Vector3 localMeters = GpsToLocalMeters(latitude, longitude, altitude, origin);
            return RealToQuest(localMeters);
        }

        /// <summary>
        /// Equirectangular approximation: good for room/site scale (&lt; ~5 km).
        /// X = East (m), Y = Up (m), Z = North (m).
        /// </summary>
        public static Vector3 GpsToLocalMeters(double latitude, double longitude, double altitude, GPSReferenceOrigin origin)
        {
            const double metersPerDegLat = 111320.0;
            double latRad = origin.latitude * (Math.PI / 180.0);
            double metersPerDegLon = metersPerDegLat * Math.Cos(latRad);

            float x = (float)((longitude - origin.longitude) * metersPerDegLon);
            float y = (float)(altitude - origin.altitude);
            float z = (float)((latitude - origin.latitude) * metersPerDegLat);
            return new Vector3(x, y, z);
        }

        /// <summary>
        /// Reset calibration
        /// </summary>
        public void ResetCalibration()
        {
            currentCalibration = null;
            calibrationPoints.Clear();
            isCalibrating = false;

            if (laserPointer != null)
                laserPointer.enabled = false;

            lastResidualRms = 0f;
            PlayerPrefs.DeleteKey("CalibrationData");
            PlayerPrefs.Save();

            SetStep("Calibration reset");
            OnCalibrationReset?.Invoke();
            Debug.Log("[Calibration] Calibration reset");
        }

        private void SetStep(string msg)
        {
            currentStepMessage = msg ?? "";
            OnStepChanged?.Invoke(currentStepMessage);
        }

        private void CaptureCalibrationPoint()
        {
            if (calibrationPoints.Count >= maximumCalibrationPoints)
            {
                Debug.LogWarning($"[Calibration] Maximum of {maximumCalibrationPoints} points reached");
                return;
            }

            Transform controller = GetActiveController();
            if (controller == null) return;

            Vector3 pointPosition;
            if (Physics.Raycast(controller.position, controller.forward, out RaycastHit hit, laserLength))
            {
                pointPosition = hit.point;
            }
            else
            {
                pointPosition = controller.position + controller.forward * laserLength;
            }

            CalibrationPoint point = new CalibrationPoint
            {
                questPosition = pointPosition,
                hasRealCoordinates = false,
                timestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            calibrationPoints.Add(point);

            if (calibrationMarkerPrefab != null)
            {
                GameObject marker = Instantiate(calibrationMarkerPrefab, pointPosition, Quaternion.identity);
                Destroy(marker, 30f);
            }

            Debug.Log($"[Calibration] Captured point {calibrationPoints.Count} at Quest position: {pointPosition}");
            SetStep($"Point {calibrationPoints.Count} captured — POST /api/calibrate with realX/realY/realZ (quickCalibrate:false)");
            Debug.Log("[Calibration] Now send real-world coordinates via API: POST /api/calibrate");
        }

        private void CalculateCalibration()
        {
            var validPoints = calibrationPoints.FindAll(p => p.hasRealCoordinates);
            if (validPoints.Count < minimumCalibrationPoints)
            {
                Debug.LogWarning($"[Calibration] Need at least {minimumCalibrationPoints} points with real coordinates");
                return;
            }

            if (validPoints.Count == 1)
            {
                var p = validPoints[0];
                currentCalibration = new CalibrationData
                {
                    originOffset = p.questPosition - p.realWorldPosition,
                    rotationOffset = Quaternion.identity,
                    scale = Vector3.one,
                    isValid = true,
                    calibrationTimeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    pointsUsed = 1
                };
                currentCalibration.SyncQuaternionFields();
            }
            else
            {
                Vector3 questCentroid = Vector3.zero;
                Vector3 realCentroid = Vector3.zero;

                foreach (var p in validPoints)
                {
                    questCentroid += p.questPosition;
                    realCentroid += p.realWorldPosition;
                }
                questCentroid /= validPoints.Count;
                realCentroid /= validPoints.Count;

                Vector3 questDir = (validPoints[1].questPosition - validPoints[0].questPosition);
                Vector3 realDir = (validPoints[1].realWorldPosition - validPoints[0].realWorldPosition);

                questDir.y = 0;
                realDir.y = 0;

                if (questDir.sqrMagnitude > 0.0001f && realDir.sqrMagnitude > 0.0001f)
                {
                    float angle = Vector3.SignedAngle(realDir.normalized, questDir.normalized, Vector3.up);

                    // Optional scale from inter-point distance ratio (XZ plane)
                    float questDist = questDir.magnitude;
                    float realDist = realDir.magnitude;
                    float uniformScale = (useAveraging && realDist > 0.01f)
                        ? questDist / realDist
                        : 1f;

                    currentCalibration = new CalibrationData
                    {
                        originOffset = questCentroid - Quaternion.Euler(0, angle, 0) * (realCentroid * uniformScale),
                        rotationOffset = Quaternion.Euler(0, angle, 0),
                        scale = Vector3.one * uniformScale,
                        isValid = true,
                        calibrationTimeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        pointsUsed = validPoints.Count
                    };
                    currentCalibration.SyncQuaternionFields();
                }
                else
                {
                    Debug.LogWarning("[Calibration] Points are too close or collinear on XZ plane — cannot compute rotation");
                    return;
                }
            }

            if (currentCalibration != null && currentCalibration.isValid)
            {
                lastResidualRms = ComputeResidualRms(validPoints);
                currentCalibration.residualRmsMeters = lastResidualRms;
                SaveCalibration();
                StopCalibration();
                SetStep($"Calibrated OK — residual RMS {lastResidualRms:F3} m ({validPoints.Count} pts)");
                OnCalibrationComplete?.Invoke(currentCalibration);
                Debug.Log($"[Calibration] Complete with {validPoints.Count} points, residual RMS={lastResidualRms:F4}m");
            }
        }

        private float ComputeResidualRms(List<CalibrationPoint> validPoints)
        {
            if (currentCalibration == null || validPoints == null || validPoints.Count == 0)
                return 0f;

            double sumSq = 0;
            int n = 0;
            foreach (var p in validPoints)
            {
                Vector3 predicted = RealToQuest(p.realWorldPosition);
                float err = Vector3.Distance(predicted, p.questPosition);
                sumSq += err * err;
                n++;
            }
            return n == 0 ? 0f : (float)Math.Sqrt(sumSq / n);
        }

        private Transform GetActiveController()
        {
            if (rightController != null)
                return rightController;

#if !MOBILE_PREVIEW
            var ovrRig = FindObjectOfType<OVRCameraRig>();
            if (ovrRig != null)
            {
                return ovrRig.rightHandAnchor;
            }
#endif

            return Camera.main != null ? Camera.main.transform : null;
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

            currentCalibration.SyncQuaternionFields();
            string json = JsonUtility.ToJson(currentCalibration);
            PlayerPrefs.SetString("CalibrationData", json);
            PlayerPrefs.Save();
        }

        private void LoadSavedCalibration()
        {
            if (!PlayerPrefs.HasKey("CalibrationData")) return;

            try
            {
                string json = PlayerPrefs.GetString("CalibrationData");
                currentCalibration = JsonUtility.FromJson<CalibrationData>(json);

                // Migrate pre-1.2 saves where the rotation was a serialized Quaternion
                // ("rotationOffset") instead of rotX..rotW component fields.
                if (currentCalibration != null && !json.Contains("\"rotX\"") && json.Contains("\"rotationOffset\""))
                {
                    var legacy = JsonUtility.FromJson<LegacyCalibrationData>(json);
                    if (legacy != null)
                    {
                        currentCalibration.rotationOffset = legacy.rotationOffset;
                        currentCalibration.SyncQuaternionFields();
                    }
                }

                if (currentCalibration != null && currentCalibration.isValid)
                {
                    currentCalibration.EnsureQuaternion();
                    SaveCalibration(); // persist in the new component-field format
                    Debug.Log("[Calibration] Loaded saved calibration");
                }
                else
                {
                    currentCalibration = null;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Calibration] Failed to load saved calibration: {e.Message}");
                currentCalibration = null;
            }
        }

        /// <summary>Pre-1.2 on-disk format: rotation stored as a Quaternion field.</summary>
        [Serializable]
        private class LegacyCalibrationData
        {
            public Quaternion rotationOffset = Quaternion.identity;
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
        /// <summary>Unix ms — JsonUtility-safe (DateTime is not).</summary>
        public long timestampUnixMs;
    }

    /// <summary>
    /// Calibration transformation data.
    /// Quaternion is stored as float components so JsonUtility round-trips correctly.
    /// </summary>
    [Serializable]
    public class CalibrationData
    {
        public Vector3 originOffset;
        public Vector3 scale;
        public bool isValid;
        public long calibrationTimeUnixMs;
        public int pointsUsed;
        public float residualRmsMeters;

        // Quaternion components for JsonUtility persistence
        public float rotX, rotY, rotZ, rotW = 1f;

        [NonSerialized]
        public Quaternion rotationOffset = Quaternion.identity;

        public void SyncQuaternionFields()
        {
            rotX = rotationOffset.x;
            rotY = rotationOffset.y;
            rotZ = rotationOffset.z;
            rotW = rotationOffset.w;
        }

        public void EnsureQuaternion()
        {
            if (Mathf.Abs(rotX) + Mathf.Abs(rotY) + Mathf.Abs(rotZ) + Mathf.Abs(rotW) < 1e-6f)
            {
                rotationOffset = Quaternion.identity;
                rotW = 1f;
            }
            else
            {
                rotationOffset = new Quaternion(rotX, rotY, rotZ, rotW);
            }
        }
    }

    /// <summary>
    /// Lightweight status for API responses / UI.
    /// </summary>
    [Serializable]
    public class CalibrationStatus
    {
        public bool isCalibrating;
        public bool isCalibrated;
        public int capturedPoints;
        public int pointsWithRealCoords;
        public int pointsUsed;
        public int minimumRequired;
    }

    /// <summary>
    /// GPS origin for lat/lon → local meters conversion.
    /// </summary>
    [Serializable]
    public struct GPSReferenceOrigin
    {
        public double latitude;
        public double longitude;
        public double altitude;
    }
}
