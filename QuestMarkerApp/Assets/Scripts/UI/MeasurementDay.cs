using System.Collections.Generic;
using UnityEngine;
using QuestMarkerApp.Core;

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// «Ξεκινάω μετρήσεις»: one short flow for a thesis measurement day in the field, in place
    /// of three settings in two menu sections. Conditions from a list (not a 6-value cycle),
    /// one yes/no for changes on site, then the anchor field test switches on and, when the
    /// alignment is already confirmed, the accuracy check too (otherwise the alignment list
    /// opens: the accuracy check is switched on from the menu once the k control points are
    /// placed, since while it runs touches no longer place points). «Τέλος μετρήσεων» switches
    /// both off. Session-only, like the tools themselves.
    /// In practice only the accuracy check runs (the anchor test never logs practice).
    /// </summary>
    public static class MeasurementDay
    {
        public static bool Active { get; private set; }

        /// <summary>Why it cannot start now, or null.</summary>
        public static string Unavailable(ProjectAlignment a)
        {
            if (a == null || !a.HasProject) return L.T("Δεν υπάρχει έργο", "No project");
            if (WorkMode.IsOffice && !a.IsPractice) return L.T("Μόνο στο πεδίο", "Field only");
            return null;
        }

        /// <summary>Menu readout: what runs, short enough for the value column.</summary>
        public static string Summary(ProjectAlignment a)
        {
            if (!Active) return Unavailable(a) ?? L.T("συνθήκες · anchors · ακρίβεια", "conditions · anchors · accuracy");
            string acc = AlignmentAccuracy.Active ? L.T(" · ακρίβεια", " · accuracy") : "";
            string anc = AnchorFieldTest.Enabled ? " · anchors" : "";
            return AnchorFieldTest.ConditionName + anc + acc;
        }

        public static void Begin(FlowPanel flow, ProjectAlignment a)
        {
            if (flow == null || Unavailable(a) != null) return;
            PickCondition(flow, a, () =>
            {
                if (a.IsPractice) { Start(flow, a); return; }
                var items = new List<FlowItem>
                {
                    new FlowItem { Label = L.T("Όχι", "No"), Sub = L.T("Ο χώρος είναι όπως την τελευταία φορά", "The site is as it was last time"), Tag = false },
                    new FlowItem { Label = L.T("Ναι", "Yes"), Sub = L.T("Εκσκαφές, σκαλωσιές, μηχανήματα από την τελευταία φορά", "Excavation, scaffolding, machines since last time"), Tag = true }
                };
                flow.ShowList(L.T("Μετρήσεις", "Measurements"),
                    L.T("Άλλαξε κάτι στο εργοτάξιο;", "Has anything changed on site?"), items,
                    i => { AnchorFieldTest.SiteChanged = (bool)i.Tag; Start(flow, a); },
                    () => PickCondition(flow, a, null), AnchorFieldTest.SiteChanged ? 1 : 0);
            });
        }

        /// <summary>The conditions from a list; <paramref name="then"/> null just closes (the menu's «Συνθήκες» row).</summary>
        public static void PickCondition(FlowPanel flow, ProjectAlignment a, System.Action then)
        {
            var items = new List<FlowItem>();
            for (int i = 0; i < AnchorFieldTest.Conditions.Length; i++)
                items.Add(new FlowItem { Label = AnchorFieldTest.ConditionLabel(i), Tag = i });
            flow.ShowList(L.T("Μετρήσεις", "Measurements"), L.T("Συνθήκες τώρα", "Conditions now"), items, i =>
            {
                AnchorFieldTest.Condition = (int)i.Tag;
                if (then != null) then();
                else flow.Close();
            }, flow.Close, AnchorFieldTest.Condition);
        }

        private static void Start(FlowPanel flow, ProjectAlignment a)
        {
            Active = true;
            if (!a.IsPractice) AnchorFieldTest.Enabled = true;
            flow.Close();
            if (AlignmentAccuracy.Unavailable(a) == null) AlignmentAccuracy.SetActive(true, a);
            // Straight to the touching: the accuracy targets, or the control points to place first.
            a.SetMode(true);
            Debug.Log($"[MeasurementDay] Started · {AnchorFieldTest.ConditionTag} · site changed {AnchorFieldTest.SiteChanged}");
        }

        public static void End(ProjectAlignment a)
        {
            Active = false;
            AnchorFieldTest.Enabled = false;
            if (a != null) AlignmentAccuracy.SetActive(false, a);
            Debug.Log("[MeasurementDay] Ended");
        }
    }
}
