using System;
using UnityEngine;
using QuestMarkerApp.Core;

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// Which hand points, clicks and places (dominant) and which holds the list (off-hand),
    /// plus button reads that only listen to the Touch controllers — with controllers put
    /// down, hand tracking maps pinches onto Button.One/Three, which must never set an origin.
    /// </summary>
    public static class Hands
    {
        private const string PrefLeftHanded = "ui.leftHanded";
        private static bool? leftHanded;

        public static bool LeftHanded
        {
            get => leftHanded ?? (bool)(leftHanded = PlayerPrefs.GetInt(PrefLeftHanded, 0) == 1);
            set
            {
                bool changed = LeftHanded != value;
                leftHanded = value;
                PlayerPrefs.SetInt(PrefLeftHanded, value ? 1 : 0);
                PlayerPrefs.Save();
                if (changed) OnChanged?.Invoke();
            }
        }

        /// <summary>Raised when the dominant hand changes, for text built once that names buttons or sides.</summary>
        public static event Action OnChanged;

        // Button and side names for hint text, following the dominant hand.
        /// <summary>A / X on the pointing hand.</summary>
        public static string PrimaryName => LeftHanded ? "X" : "A";
        /// <summary>B / Y on the pointing hand.</summary>
        public static string SecondaryName => LeftHanded ? "Y" : "B";
        /// <summary>X / A on the other hand.</summary>
        public static string OffHandPrimaryName => LeftHanded ? "A" : "X";
        /// <summary>Y / B on the other hand.</summary>
        public static string OffHandSecondaryName => LeftHanded ? "B" : "Y";
        // In English the side names are plain lower-case adjectives: "the right controller".
        /// <summary>Genitive: "του δεξιού / αριστερού χειριστηρίου"; English "right" / "left".</summary>
        public static string DominantSideName => LeftHanded ? L.T("αριστερού", "left") : L.T("δεξιού", "right");
        /// <summary>Nominative: "Δεξί" / "Αριστερό"; English "Right" / "Left".</summary>
        public static string DominantSide => LeftHanded ? L.T("Αριστερό", "Left") : L.T("Δεξί", "Right");
        /// <summary>Genitive of the other hand: "του αριστερού / δεξιού χειριστηρίου"; English "left" / "right".</summary>
        public static string OffHandSideName => LeftHanded ? L.T("δεξιού", "right") : L.T("αριστερού", "left");

#if !MOBILE_PREVIEW
        public static OVRInput.Controller Dominant => LeftHanded ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;
        public static OVRInput.Controller OffHand => LeftHanded ? OVRInput.Controller.RTouch : OVRInput.Controller.LTouch;

        public static Transform DominantAnchor(OVRCameraRig rig) =>
            rig == null ? null : LeftHanded ? rig.leftControllerAnchor : rig.rightControllerAnchor;

        public static Transform OffHandAnchor(OVRCameraRig rig) =>
            rig == null ? null : LeftHanded ? rig.rightControllerAnchor : rig.leftControllerAnchor;

        public static bool DominantConnected => OVRInput.IsControllerConnected(Dominant);

        /// <summary>Index trigger of the pointing hand.</summary>
        public static bool TriggerDown() => OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, Dominant);

        public static bool GripDown() => OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger, Dominant);
        public static bool GripHeld() => OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, Dominant);
        /// <summary>Grip of the other hand: grabs the model preview (tabletop / 1:1).</summary>
        public static bool OffHandGripHeld() => OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OffHand);

        public static float DominantStickY() => OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, Dominant).y;
        public static float OffHandStickY() => OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OffHand).y;
        /// <summary>Either stick, for list navigation that shouldn't care which hand is free.</summary>
        public static float AnyStickY() => DominantStickY() + OffHandStickY();
        public static float DominantStickX() => OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, Dominant).x;
        public static float OffHandStickX() => OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OffHand).x;
        public static float AnyStickX() => Mathf.Clamp(DominantStickX() + OffHandStickX(), -1f, 1f);

        /// <summary>A / X on the pointing hand (controller-specific: never a hand-tracking pinch).</summary>
        public static bool DominantPrimaryDown() => OVRInput.GetDown(OVRInput.Button.One, Dominant);
        /// <summary>B / Y on the pointing hand.</summary>
        public static bool DominantSecondaryDown() => OVRInput.GetDown(OVRInput.Button.Two, Dominant);
        /// <summary>X / A on the other hand (opens the main menu; closes the alignment list while it is open).</summary>
        public static bool OffHandPrimaryDown() => OVRInput.GetDown(OVRInput.Button.One, OffHand);
        public static bool OffHandSecondaryDown() => OVRInput.GetDown(OVRInput.Button.Two, OffHand);

        /// <summary>
        /// Thumbstick click on the other hand: push-to-talk for voice commands. Only meaningful
        /// with both controllers held — with the left one pocketed the right stick click opens
        /// the menu instead (MarkerAppController), so callers check the connection first.
        /// </summary>
        public static bool OffHandStickClickDown() => OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OffHand);
        public static bool OffHandStickClickHeld() => OVRInput.Get(OVRInput.Button.PrimaryThumbstick, OffHand);
        public static bool OffHandStickClickUp() => OVRInput.GetUp(OVRInput.Button.PrimaryThumbstick, OffHand);

        /// <summary>"Select" in panels: A or X, so either hand can confirm.</summary>
        public static bool AnySelectDown() =>
            OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch)
            || OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch);

        /// <summary>"Back" in panels: B or Y.</summary>
        public static bool AnyBackDown() =>
            OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch)
            || OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch);
#endif
    }
}
