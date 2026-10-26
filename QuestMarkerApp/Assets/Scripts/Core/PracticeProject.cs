using System;
using System.Globalization;
using QuestMarkerApp.API;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// Office practice with two printed targets (docs/practice-targets.html): a built-in fake
    /// project in a local metric frame, so alignment, sensor cards and navigation can be
    /// learnt without Terraweb or a site. P1 and P2 lie 3.00 m apart on the floor; seven demo
    /// sensors stand within 3 m of P1 (so the panel's "suggested" next point stays P2) at
    /// 0.8–1.8 m above it.
    ///
    /// It goes through the same path as a Terraweb project (ProjectAlignment.SetProject, then
    /// OpenIfNeeded), styled with TerrawebImport.Style, so the panel, cards and navigation
    /// behave exactly as on site. It is kept apart from real data:
    /// - ProjectAlignment.IsPractice: placements are never read from or written to disk, and
    ///   the LAN status reports no project;
    /// - MarkerManager leaves its markers out of GET /api/coordinates; SessionManager already
    ///   leaves every "prj:" marker out of sessions and undo history;
    /// - no sensorId and terrawebProjectId 0: the card, live alerts and history never call
    ///   Terraweb for it;
    /// - leaving clears it and restores the last real project saved on the headset.
    /// </summary>
    public static class PracticeProject
    {
        /// <summary>Shown as the project name in the menu and the alignment panel.</summary>
        public const string Name = "ΕΞΑΣΚΗΣΗ / PRACTICE";
        /// <summary>Distance P1–P2 on the printed sheet's instructions.</summary>
        public const float Baseline = 3.00f;

        private static ProjectAlignment alignment;

        /// <summary>The practice project is the one in the room.</summary>
        public static bool IsActive => alignment != null && alignment.IsPractice;

        /// <summary>Raised after entering or leaving, for panels that show a practice badge.</summary>
        public static event Action Changed;

        public static void Enter(ProjectAlignment target)
        {
            if (target == null) return;
            alignment = target;
            target.SetProject(Build(), practice: true);
            target.OpenIfNeeded(); // straight into the alignment panel, as after a Terraweb load
            UnityEngine.Debug.Log("[Practice] Practice project loaded (not saved, not shared)");
            Changed?.Invoke();
        }

        public static void Leave()
        {
            if (!IsActive) return;
            alignment.LeavePractice();
            UnityEngine.Debug.Log("[Practice] Practice project cleared");
            Changed?.Invoke();
        }

        // ------------------------------------------------------------------
        //  Data
        // ------------------------------------------------------------------

        // Local frame: E/N/H in metres, P1 at (1000, 1000, 100) on the floor. Names follow the
        // field habit of short codes; "PR-" marks every one as practice in the room labels.
        private static ProjectRequest Build()
        {
            string practice = L.T("εξάσκηση", "practice");
            string today = DateTime.UtcNow.AddHours(-3).ToString("o", CultureInfo.InvariantCulture);
            string lastWeek = DateTime.UtcNow.AddDays(-6).ToString("o", CultureInfo.InvariantCulture);

            ProjectPoint Target(string id, double e, double n) => new ProjectPoint
            {
                id = id, label = id, e = e, n = n, h = 100.0,
                icon = "target", color = "#FFD400",
                typeLabel = L.T("Στόχος ελέγχου", "Control target") + " · " + practice,
                alertState = "none",
                notes = L.T($"ΕΞΑΣΚΗΣΗ: τυπωμένος στόχος. Το P1 και το P2 απέχουν {Baseline:F2} m (κέντρο με κέντρο).",
                    $"PRACTICE: printed target. P1 and P2 are {Baseline:F2} m apart (centre to centre).")
            };

            ProjectPoint Sensor(string id, string typeEn, string typeEl, double e, double n, double h,
                string state, string date, string valueEl, string valueEn)
            {
                (string icon, string color) = TerrawebImport.Style(typeEn, true);
                return new ProjectPoint
                {
                    id = id, label = id, e = e, n = n, h = h,
                    icon = icon, color = color,
                    typeLabel = L.T(typeEl, typeEn) + " · " + practice,
                    alertState = state,
                    latestDate = date,
                    // No sensorId: the card never asks Terraweb, and shows these made-up values.
                    notes = L.T("ΕΞΑΣΚΗΣΗ, όχι πραγματική μέτρηση. ", "PRACTICE, not a real reading. ")
                        + L.T(valueEl, valueEn)
                };
            }

            return new ProjectRequest
            {
                name = Name,
                crs = "local practice frame (m)",
                terrawebProjectId = 0,
                gridYawDeg = 0f,
                points = new[]
                {
                    // Control targets first: the panel starts on P1 and moves on to P2.
                    Target("P1", 1000.0, 1000.0),
                    Target("P2", 1000.0 + Baseline, 1000.0),
                    Sensor("PR-INC1", "Inclinometer", "Κλισιόμετρο", 1001.5, 1002.0, 101.2, "none", today,
                        "Μετακίνηση κορυφής 4.2 mm (όριο προειδοποίησης 10 mm).", "Top displacement 4.2 mm (alert limit 10 mm)."),
                    Sensor("PR-PZ1", "Piezometer", "Πιεζόμετρο", 999.4, 1001.6, 100.8, "reference", lastWeek,
                        "Στάθμη νερού −3.10 m (μέτρηση αναφοράς).", "Water level −3.10 m (reference reading)."),
                    Sensor("PR-CM1", "Crack meter", "Ρωγμόμετρο", 1002.5, 1001.5, 101.5, "alert", today,
                        "Άνοιγμα ρωγμής 1.8 mm (προειδοποίηση από 1.5 mm).", "Crack opening 1.8 mm (alert from 1.5 mm)."),
                    Sensor("PR-TLT1", "Tiltmeter", "Κλισιόμετρο επιφάνειας", 1002.0, 998.5, 101.4, "alarm", today,
                        "Κλίση 0.42° (συναγερμός από 0.35°).", "Tilt 0.42° (alarm from 0.35°)."),
                    Sensor("PR-SG1", "Strain gauge", "Μηκυνσιόμετρο", 1000.0, 998.0, 101.0, "none", today,
                        "Παραμόρφωση 85 με (όριο 200 με).", "Strain 85 µε (limit 200 µε)."),
                    Sensor("PR-TMP1", "Thermometer", "Θερμόμετρο", 999.2, 998.6, 101.8, "none", today,
                        "Θερμοκρασία σκυροδέματος 18.6 °C.", "Concrete temperature 18.6 °C."),
                    Sensor("PR-PRS1", "3D prism", "Πρίσμα 3D", 1000.5, 1002.5, 101.6, "alert", lastWeek,
                        "Καθίζηση 12 mm (προειδοποίηση από 10 mm).", "Settlement 12 mm (alert from 10 mm).")
                }
            };
        }
    }
}
