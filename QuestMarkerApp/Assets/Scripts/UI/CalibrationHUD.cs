using UnityEngine;
using QuestMarkerApp.Core;

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// World-space / OnGUI step guide for multi-point calibration + residual.
    /// </summary>
    public class CalibrationHUD : MonoBehaviour
    {
        [SerializeField] private CoordinateCalibration calibration;
        [SerializeField] private bool useOnGUI = true;
        [SerializeField] private bool onlyWhenCalibrating = false;

        private string step = "";
        private float residual;
        private bool calibrated;
        private bool calibrating;

        private void Start()
        {
            if (calibration == null)
                calibration = FindObjectOfType<CoordinateCalibration>();

            if (calibration != null)
            {
                calibration.OnStepChanged += OnStep;
                calibration.OnCalibrationComplete += OnComplete;
                calibration.OnCalibrationReset += OnReset;
                step = calibration.CurrentStepMessage;
            }
        }

        private void OnDestroy()
        {
            if (calibration != null)
            {
                calibration.OnStepChanged -= OnStep;
                calibration.OnCalibrationComplete -= OnComplete;
                calibration.OnCalibrationReset -= OnReset;
            }
        }

        private void Update()
        {
            if (calibration == null) return;
            calibrating = calibration.IsCalibrating;
            calibrated = calibration.IsCalibrated;
            residual = calibration.LastResidualRms;
        }

        private void OnStep(string msg) => step = msg;
        private void OnComplete(CalibrationData d)
        {
            residual = d != null ? d.residualRmsMeters : 0f;
            step = calibration != null ? calibration.CurrentStepMessage : "Done";
        }
        private void OnReset() => step = "Calibration reset";

        private void OnGUI()
        {
            if (!useOnGUI) return;
            if (onlyWhenCalibrating && !calibrating && string.IsNullOrEmpty(step)) return;

            float w = Mathf.Min(Screen.width * 0.8f, 720f);
            float x = (Screen.width - w) * 0.5f;
            float y = Screen.height * 0.08f;

            GUI.skin.box.fontSize = 22;
            GUI.skin.label.fontSize = 20;

            string status = calibrating ? "CALIBRATING"
                : calibrated ? $"CALIBRATED (RMS {residual:F3} m)"
                : "NOT CALIBRATED";

            string box =
                $"Calibration: {status}\n" +
                $"{step}\n\n" +
                "Flow: X start → aim + trigger → POST /api/calibrate (quickCalibrate:false) → repeat\n" +
                "Quick: POST /api/calibrate {realX,realY,realZ,quickCalibrate:true}";

            GUI.Box(new Rect(x, y, w, 140f), box);
        }
    }
}
