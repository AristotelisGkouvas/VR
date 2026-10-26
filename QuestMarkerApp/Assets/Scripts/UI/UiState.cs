using UnityEngine;

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// Which floating panel owns the controller buttons right now. A modal panel consumes
    /// sticks and buttons; everything else (alignment, shortcuts, card picking) stands down.
    /// </summary>
    public static class UiState
    {
        private static int lastCloseFrame = -1;

        public static bool AnyModal => MainMenu.IsOpen || FlowPanel.IsOpen || SensorCard.IsOpen || VrKeyboard.IsOpen;

        /// <summary>Every panel calls this when it closes.</summary>
        public static void NoteClosed() => lastCloseFrame = Time.frameCount;

        /// <summary>
        /// World input (shortcuts, placing points, opening cards) must stand down: a panel is up,
        /// a panel was just closed by this very press (Update order between panels and the
        /// controller is undefined, so B closing the menu could otherwise also "clear all"),
        /// or a panel is being carried with the grip.
        /// </summary>
        public static bool InputBlocked =>
            AnyModal || lastCloseFrame == Time.frameCount || VrPointer.IsDragging || VrPointer.ConsumedThisFrame;

        /// <summary>
        /// Context key for the controller callouts. Navigation, time machine and route can run
        /// together; B belongs to the one on top — navigation, then the time machine, then the
        /// route (SensorNavigator / DisplacementView / InspectionRoute follow the same order).
        /// </summary>
        public static string Context(Core.ProjectAlignment project)
        {
            if (VrKeyboard.IsOpen) return "keyboard";
            if (FlowPanel.IsOpen) return FlowPanel.IsMessage ? "message" : FlowPanel.IsForm ? "form" : "list";
            if (SensorCard.IsOpen) return "card";
            if (MainMenu.IsOpen) return "menu";
            if (project != null && project.IsModeActive) return "align";
            if (SensorNavigator.IsActive) return "nav";
            if (DisplacementView.IsActive) return "time";
            if (InspectionRoute.IsActive) return "route";
            if (project != null && project.HasProject) return "project";
            return "basic";
        }
    }
}
