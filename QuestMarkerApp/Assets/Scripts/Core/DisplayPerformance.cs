using UnityEngine;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// Refresh rate and fixed foveated rendering, chosen in Μενού › Ρυθμίσεις so they can be
    /// compared on the headset without a rebuild. Defaults keep the old behaviour (72 Hz, no
    /// foveation): 90 Hz is smoother for passthrough overlays and stick walking, but if the
    /// app cannot hold it the judder is worse than a steady 72, and it costs battery and heat.
    /// Measure with OVR Metrics Tool before leaving it on.
    /// </summary>
    public class DisplayPerformance : MonoBehaviour
    {
        public static readonly float[] Rates = { 72f, 90f };
        public static readonly string[] FoveationNames = { "Ανενεργό", "Χαμηλό", "Μεσαίο", "Υψηλό" };

        private const string RateKey = "ui.refreshHz";
        private const string FoveationKey = "ui.foveation";

        /// <summary>Requested refresh rate in Hz (72 or 90).</summary>
        public static float RefreshRate
        {
            get => PlayerPrefs.GetFloat(RateKey, 72f);
            set { PlayerPrefs.SetFloat(RateKey, value); PlayerPrefs.Save(); }
        }

        /// <summary>0 off, 1 low, 2 medium, 3 high (dynamic: the runtime lowers it while the GPU is idle).</summary>
        public static int Foveation
        {
            get => Mathf.Clamp(PlayerPrefs.GetInt(FoveationKey, 0), 0, FoveationNames.Length - 1);
            set { PlayerPrefs.SetInt(FoveationKey, Mathf.Clamp(value, 0, FoveationNames.Length - 1)); PlayerPrefs.Save(); }
        }

        /// <summary>The rate the display actually runs at (0 when unknown, e.g. in the editor).</summary>
        public static float ActualRate
        {
            get
            {
#if !MOBILE_PREVIEW
                return OVRManager.display != null ? OVRManager.display.displayFrequency : 0f;
#else
                return 0f;
#endif
            }
        }

        public static bool IsAvailable(float hz)
        {
#if !MOBILE_PREVIEW
            float[] available = OVRManager.display != null ? OVRManager.display.displayFrequenciesAvailable : null;
            if (available == null) return false;
            foreach (float f in available)
                if (Mathf.Abs(f - hz) < 0.5f) return true;
#endif
            return false;
        }

        /// <summary>Next rate this headset supports, wrapping round (72 → 90 → 72).</summary>
        public static float NextRate(float current)
        {
            int start = 0;
            for (int i = 0; i < Rates.Length; i++)
                if (Mathf.Abs(Rates[i] - current) < 0.5f) start = i;
            for (int step = 1; step <= Rates.Length; step++)
            {
                float candidate = Rates[(start + step) % Rates.Length];
                if (IsAvailable(candidate)) return candidate;
            }
            return current;
        }

        private float nextCheck;

        private void Update()
        {
#if !MOBILE_PREVIEW
            // Re-asserted periodically like BoundaryControl: the runtime can drop the request
            // across a pause/resume, and a menu change lands within a second.
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 1f;

            OVRDisplay display = OVRManager.display;
            float rate = RefreshRate;
            if (display != null && IsAvailable(rate) && Mathf.Abs(display.displayFrequency - rate) > 0.5f)
            {
                display.displayFrequency = rate;
                Debug.Log($"[Display] Refresh rate requested: {rate} Hz");
            }

            if (OVRManager.fixedFoveatedRenderingSupported)
            {
                var level = (OVRManager.FoveatedRenderingLevel)Foveation;
                if (OVRManager.foveatedRenderingLevel != level)
                {
                    OVRManager.foveatedRenderingLevel = level;
                    OVRManager.useDynamicFoveatedRendering = level != OVRManager.FoveatedRenderingLevel.Off;
                    Debug.Log($"[Display] Foveation: {level}");
                }
            }
#endif
        }
    }
}
