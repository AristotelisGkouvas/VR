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

        [Header("Frame")]
        [Tooltip("Markers are placed relative to MarkerManager's reference point, so captured points " +
                 "are stored in that frame too. Found automatically when empty.")]
        [SerializeField] private Markers.MarkerManager markerManager;

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
        private float lastScaleRatio = 1f;
        private string currentStepMessage = "Idle";

        public bool IsCalibrated => currentCalibration != null && currentCalibration.isValid;
        public bool IsCalibrating => isCalibrating;
        public CalibrationData CurrentCalibration => currentCalibration;
        public int CapturedPointCount => calibrationPoints.Count;
        public float LastResidualRms => lastResidualRms;
        /// <summary>Quest/real XZ distance ratio of the last multi-point fit — a sanity check, never applied.</summary>
        public float LastScaleRatio => lastScaleRatio;
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

#if !MOBILE_PREVIEW
        private OVRCameraRig cachedRig;
#endif

        // RealToQuest feeds MarkerManager, which applies referencePoint.TransformPoint on top:
        // the fit must therefore map real coords into the reference frame, not world space
        // (world-space captures put every calibrated marker off by the anchor's pose).
        private Transform ReferenceFrame => markerManager != null ? markerManager.ReferencePoint : null;

        private Vector3 WorldToFrame(Vector3 world)
        {
            Transform frame = ReferenceFrame;
            return frame != null ? frame.InverseTransformPoint(world) : world;
        }

        /// <summary>Minimum horizontal spread (Σ squared offsets from the centroid, m²) to solve a heading.</summary>
        private const double MinSpread = 0.25 * 0.25;

        /// <summary>
        /// Real coordinates travel as float: a projected E/N (EGSA87 ~4 200 000, UTM up to
        /// 10 000 000) keeps only 0.25–1 m. This calibration is for local metres; survey grids
        /// belong in /api/project, which keeps them in double around an origin.
        /// </summary>
        private static void WarnIfLarge(Vector3 real)
        {
            const float limit = 20000f; // float ulp at 20 km is 2 mm
            if (Mathf.Abs(real.x) > limit || Mathf.Abs(real.y) > limit || Mathf.Abs(real.z) > limit)
                Debug.LogWarning($"[Calibration] Real coordinate {real} is beyond ±{limit} m: float precision is lost " +
                                 "(cm to m). Subtract a site origin first, or load the points as a project (/api/project).");
        }

        private void Start()
        {
            if (markerManager == null) markerManager = FindAnyObjectByType<Markers.MarkerManager>();
            // Captured points are in the reference frame; points from before a frame change
            // would be mixed with ones after it in one fit.
            if (markerManager != null) markerManager.OnReferencePointChanged += OnReferenceFrameChanged;
            SetupLaserPointer();
            LoadSavedCalibration();
        }

        private void OnDestroy()
        {
            if (markerManager != null) markerManager.OnReferencePointChanged -= OnReferenceFrameChanged;
        }

        private void OnReferenceFrameChanged(Transform _)
        {
            if (!isCalibrating || calibrationPoints.Count == 0) return;
            calibrationPoints.Clear();
            Debug.LogWarning("[Calibration] Reference point changed during calibration — captured points discarded");
            SetStep($"Reference point changed — start again: Step 1/{minimumCalibrationPoints}");
        }

        private void Update()
        {
            if (isCalibrating)
            {
                UpdateLaserPointer();

#if !MOBILE_PREVIEW
                if (!UI.UiState.InputBlocked && OVRInput.GetDown(calibrateButton, UI.Hands.Dominant))
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
            lastScaleRatio = 1f;

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

            WarnIfLarge(realWorldCoords);
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
        /// Add a calibration point with both positions known. <paramref name="questPosition"/>
        /// must be in the marker reference frame (see ReferenceFrame), not world space.
        /// </summary>
        public void AddCalibrationPoint(Vector3 questPosition, Vector3 realWorldPosition, CoordinateSystem system = CoordinateSystem.Local)
        {
            if (calibrationPoints.Count >= maximumCalibrationPoints)
            {
                Debug.LogWarning($"[Calibration] Maximum of {maximumCalibrationPoints} points reached");
                return;
            }

            WarnIfLarge(realWorldPosition);
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

            // RealToQuest(p) = R*p + originOffset, so the origin lands on the controller only
            // when the offset is taken after rotating it. Pose and heading are taken in the
            // marker reference frame (see ReferenceFrame).
            // Real +Z (North) is laid along the controller's pointing direction, then the real
            // frame is turned by a further rotationYDegrees about Y (as documented in the README):
            // R = Euler(controllerYaw + rotationY). The previous "rotationY − controllerYaw" had the
            // controller term mirrored: turning the controller 90° right swung the site 90° left.
            Transform frame = ReferenceFrame;
            Quaternion inFrame = frame != null ? Quaternion.Inverse(frame.rotation) * controller.rotation : controller.rotation;
            // Heading from the forward vector on the floor plane; eulerAngles.y is unreliable when
            // the controller points nearly straight down at the spot, so fall back to its up vector.
            Vector3 fwd = inFrame * Vector3.forward;
            if (fwd.x * fwd.x + fwd.z * fwd.z < 0.01f) fwd = inFrame * Vector3.up;
            float controllerYaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            Quaternion rotation = Quaternion.Euler(0, controllerYaw + rotationYDegrees, 0);
            WarnIfLarge(realWorldOrigin);
            currentCalibration = new CalibrationData
            {
                originOffset = WorldToFrame(controller.position) - rotation * realWorldOrigin,
                rotationOffset = rotation,
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
        /// WGS84 lat/lon (degrees, ellipsoidal height) → local metres around the origin:
        /// X = East, Y = Up, Z = North on the tangent plane at the origin (exact ECEF → ENU, the
        /// same as TerrawebImport / terraweb_bridge.py). Valid anywhere: either hemisphere, high
        /// latitudes, across the ±180° meridian. The headset's world is flat, so Y includes the
        /// Earth-curvature drop (8 cm at 1 km). Heights must be ellipsoidal like the origin's;
        /// survey (orthometric) heights differ by the geoid undulation, a constant offset per site.
        /// </summary>
        public static Vector3 GpsToLocalMeters(double latitude, double longitude, double altitude, GPSReferenceOrigin origin)
        {
            // The old equirectangular version used a spherical 111 320 m/deg: 0.29 % too long
            // north-south at 38° N (2.9 m per km), 0.13 % too short east-west, and a point across
            // the ±180° meridian landed ~40 000 km away.
            TerrawebImport.Enu(latitude, longitude, altitude, origin.latitude, origin.longitude, origin.altitude,
                out double east, out double north, out double up);
            return new Vector3((float)east, (float)up, (float)north);
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
            Prefs.Save();

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
                questPosition = WorldToFrame(pointPosition),
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

                // Least-squares yaw over all points on the XZ plane (same derivation as
                // ProjectAlignment.Solve). Unity's Euler(0,θ,0) maps
                // (x,z) → (x cosθ + z sinθ, −x sinθ + z cosθ).
                double sCos = 0, sSin = 0, realSpread = 0, questSpread = 0;
                foreach (var p in validPoints)
                {
                    float px = p.realWorldPosition.x - realCentroid.x, pz = p.realWorldPosition.z - realCentroid.z;
                    float mx = p.questPosition.x - questCentroid.x, mz = p.questPosition.z - questCentroid.z;
                    sCos += mx * px + mz * pz;
                    sSin += mx * pz - mz * px;
                    realSpread += px * px + pz * pz;
                    questSpread += mx * mx + mz * mz;
                }

                // Same guard as ProjectAlignment: two points closer than ~35 cm (sum of squared
                // offsets < 0.25²) leave the heading to noise — 1 cm of tip error over 1.4 cm
                // (the old 0.0001 m² limit) is a ±35° swing.
                if (realSpread >= MinSpread && questSpread >= MinSpread)
                {
                    float angle = (float)(Math.Atan2(sSin, sCos) * 180.0 / Math.PI);
                    Quaternion rotation = Quaternion.Euler(0, angle, 0);

                    // XZ distance ratio as a sanity check only: a mismatch shows up in the
                    // residual rather than being absorbed into the transform (never scale Y).
                    lastScaleRatio = (float)Math.Sqrt(questSpread / realSpread);
                    if (Mathf.Abs(lastScaleRatio - 1f) > 0.02f)
                        Debug.LogWarning($"[Calibration] Quest/real XZ scale ratio {lastScaleRatio:F4} differs from 1 — check the real coordinates");

                    currentCalibration = new CalibrationData
                    {
                        originOffset = questCentroid - rotation * realCentroid,
                        rotationOffset = rotation,
                        scale = Vector3.one,
                        isValid = true,
                        calibrationTimeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        pointsUsed = validPoints.Count
                    };
                    currentCalibration.SyncQuaternionFields();
                }
                else
                {
                    Debug.LogWarning("[Calibration] Points are too close together on the XZ plane — cannot compute rotation");
                    SetStep("Points too close together (< ~0.35 m) — capture points further apart");
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
                Debug.Log($"[Calibration] Complete with {validPoints.Count} points, residual RMS={lastResidualRms:F4}m, XZ scale ratio {lastScaleRatio:F4}");
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
            // Called every frame while calibrating: a scene search per frame is a Quest hitch.
            if (cachedRig == null) cachedRig = FindAnyObjectByType<OVRCameraRig>();
            if (cachedRig != null)
            {
                return UI.Hands.DominantAnchor(cachedRig);
            }
#endif

            return Headset.Camera != null ? Headset.Camera.transform : null;
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
                // new Material(null) throws, which would abort Start before the saved calibration loads.
                Shader laserShader = Shader.Find("Sprites/Default");
                if (laserShader != null) laserPointer.material = new Material(laserShader);
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
            currentCalibration.frameVersion = CalibrationData.ReferenceFrameVersion;
            string json = JsonUtility.ToJson(currentCalibration);
            PlayerPrefs.SetString("CalibrationData", json);
            Prefs.Save();
        }

        private void LoadSavedCalibration()
        {
            if (!PlayerPrefs.HasKey("CalibrationData")) return;

            try
            {
                string json = PlayerPrefs.GetString("CalibrationData");
                currentCalibration = JsonUtility.FromJson<CalibrationData>(json);

                // Saves without frameVersion were fitted in world space; RealToQuest is now read
                // in the reference-anchor frame, so applying one would shift every calibrated
                // marker by the anchor's pose. Drop it and ask for a new calibration instead.
                if (currentCalibration != null && currentCalibration.frameVersion < CalibrationData.ReferenceFrameVersion)
                {
                    Debug.LogWarning("[Calibration] Saved calibration predates anchor-relative calibration (world space) — " +
                                     "discarded; calibrate again");
                    currentCalibration = null;
                    PlayerPrefs.DeleteKey("CalibrationData");
                    Prefs.Save();
                    SetStep("Saved calibration was from an older version — please calibrate again");
                    return;
                }

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

        /// <summary>Saves carrying this value map real coords into the reference-anchor frame.</summary>
        public const int ReferenceFrameVersion = 2;
        /// <summary>
        /// Frame of originOffset/rotation: 0 (absent in old saves) = world space, which is no
        /// longer valid; <see cref="ReferenceFrameVersion"/> = MarkerManager reference anchor.
        /// </summary>
        public int frameVersion;

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
