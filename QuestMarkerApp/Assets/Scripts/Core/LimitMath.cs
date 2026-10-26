using System;
using System.Collections.Generic;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// Closeness to alert limits (feature 10), pure arithmetic: no Unity, no JSON, no I/O.
    ///
    /// Which tier a value has reached follows the backend's evaluator line for line
    /// (terraweb2-backend alerts/services.py): bands resolve as in <c>_limits_for</c>, breaches
    /// as in <c>_classify_reading</c> — strictly <c>value &gt; max</c> / <c>value &lt; min</c>, a value
    /// equal to a bound is inside it, tiers walked worst-first. The headset never shows this
    /// tier as the sensor's state (that is the server's <c>current_alert_state</c>); it is used
    /// for "next tier" and to notice a disagreement.
    ///
    /// On top of that comes U, the "% of limit" (docs/proposals/displacement-visualization.md
    /// §2.1), which only sets the length of a bar:
    ///   centre c = 0 when 0 lies inside the outermost bounds, else their midpoint;
    ///   per side with a bound b (the worst tier that defines a bound on that side):
    ///   u_side = (v − c) / (b − c);  u = max(u_max side, u_min side, 0).
    /// So with c = 0: v / max for v ≥ 0, v / min for v &lt; 0; moving away from a one-sided
    /// limit reads 0 %. A one-sided limit that excludes 0 has no centre: U stays undefined.
    /// </summary>
    public static class LimitMath
    {
        public const int None = 0, Reference = 1, Alert = 2, Alarm = 3;

        /// <summary>The backend's tier names (sensors/enums.py AlertTier), by rank.</summary>
        public static readonly string[] TierNames = { "none", "reference", "alert", "alarm" };

        public static int Rank(string tier)
        {
            switch (tier)
            {
                case "alarm": return Alarm;
                case "alert": return Alert;
                case "reference": return Reference;
                default: return None;
            }
        }

        public struct Bounds
        {
            public double? Min, Max;
            public bool Defined => Min.HasValue || Max.HasValue;
        }

        /// <summary>One field's thresholds: <see cref="Tiers"/>[rank], index 0 unused.</summary>
        public sealed class FieldLimit
        {
            public readonly Bounds[] Tiers = new Bounds[4];

            public bool Any => Tiers[Reference].Defined || Tiers[Alert].Defined || Tiers[Alarm].Defined;

            /// <summary>The worst tier with a max (or min) bound, and that bound; 0 when none.</summary>
            public int TopTier(bool max, out double bound)
            {
                for (int t = Alarm; t >= Reference; t--)
                {
                    double? b = max ? Tiers[t].Max : Tiers[t].Min;
                    if (b.HasValue) { bound = b.Value; return t; }
                }
                bound = 0;
                return None;
            }
        }

        /// <summary>
        /// A depth band of a vertical sensor's limit. With <see cref="Values"/> set it matches by
        /// membership only; otherwise [From, To) — half-open, as the backend.
        /// </summary>
        public sealed class Band
        {
            public double? From, To;
            public List<double> Values;
            public readonly Dictionary<string, FieldLimit> Fields = new Dictionary<string, FieldLimit>();
        }

        public sealed class LimitSet
        {
            public readonly Dictionary<string, FieldLimit> Fields = new Dictionary<string, FieldLimit>();
            public readonly List<Band> Bands = new List<Band>();
            public bool IsEmpty => Fields.Count == 0 && Bands.Count == 0;
        }

        /// <summary>One reading: field codename → value. Index is a vertical sensor's depth.</summary>
        public sealed class Reading
        {
            public readonly Dictionary<string, double> Values = new Dictionary<string, double>();
            public double? Index;
        }

        public sealed class FieldResult
        {
            public string Field;
            public double? Index;
            public double Value;
            /// <summary>Fraction of the limit used (1 = at the top bound); null = not measurable.</summary>
            public double? U;
            /// <summary>The tier U is measured against (its bound is 100 %).</summary>
            public int TopTier;
            /// <summary>Tier breached by the backend's rule (None when inside every bound).</summary>
            public int Reached;
            public string ReachedBound;
            public double ReachedLimit;
            /// <summary>Nearest bound not yet breached: its tier (None when there is none), side and value.</summary>
            public int Next;
            public string NextBound;
            public double NextLimit;
            /// <summary>Distance to <see cref="NextLimit"/>, in the field's own unit (≥ 0).</summary>
            public double Margin;
            /// <summary>Each tier's bound on U's side, on U's scale (alarm usually 1.0); null if absent.</summary>
            public readonly double?[] Ticks = new double?[4];
        }

        public sealed class SensorResult
        {
            public readonly List<FieldResult> Fields = new List<FieldResult>();
            /// <summary>The field with the highest U (null when no field has one).</summary>
            public FieldResult Driving;
            public double? U => Driving?.U;
            /// <summary>Worst tier reached over every field and reading.</summary>
            public int Reached;
        }

        // ------------------------------------------------------------------
        //  Backend rules
        // ------------------------------------------------------------------

        /// <summary><c>_band_matches</c>: no index never matches; values = membership; else [from, to).</summary>
        public static bool BandMatches(Band band, double? index)
        {
            if (!index.HasValue) return false;
            if (band.Values != null) return band.Values.Contains(index.Value);
            double i = index.Value;
            if (band.From.HasValue && i < band.From.Value) return false;
            if (band.To.HasValue && i >= band.To.Value) return false;   // half-open
            return true;
        }

        /// <summary>
        /// <c>_limits_for</c>: the top-level fields, with the FIRST matching band replacing the whole
        /// entry of every field it names (no tier-by-tier merge).
        /// </summary>
        public static Dictionary<string, FieldLimit> Effective(LimitSet set, double? index,
            Dictionary<string, FieldLimit> into = null)
        {
            var effective = into ?? new Dictionary<string, FieldLimit>();
            effective.Clear();
            foreach (var kv in set.Fields) effective[kv.Key] = kv.Value;
            foreach (Band band in set.Bands)
            {
                if (!BandMatches(band, index)) continue;
                foreach (var kv in band.Fields) effective[kv.Key] = kv.Value;
                break;
            }
            return effective;
        }

        /// <summary>
        /// <c>_classify_reading</c> for one field: worst tier first, max checked before min,
        /// strict comparisons. Returns the tier reached (None when inside every bound).
        /// </summary>
        public static int Classify(FieldLimit limit, double value, out string bound, out double limitValue)
        {
            for (int t = Alarm; t >= Reference; t--)
            {
                Bounds b = limit.Tiers[t];
                if (!b.Defined) continue;
                if (b.Max.HasValue && value > b.Max.Value) { bound = "max"; limitValue = b.Max.Value; return t; }
                if (b.Min.HasValue && value < b.Min.Value) { bound = "min"; limitValue = b.Min.Value; return t; }
            }
            bound = null;
            limitValue = 0;
            return None;
        }

        // ------------------------------------------------------------------
        //  Closeness
        // ------------------------------------------------------------------

        public static FieldResult Evaluate(string field, FieldLimit limit, double value, double? index = null)
        {
            var r = new FieldResult { Field = field, Index = index, Value = value };
            r.Reached = Classify(limit, value, out r.ReachedBound, out r.ReachedLimit);

            // Next: the nearest bound of a tier above the one reached. Those are all unbreached,
            // so v ≤ max and v ≥ min there; ties go to the lower tier.
            r.Next = None;
            double best = double.MaxValue;
            for (int t = r.Reached + 1; t <= Alarm; t++)
            {
                Bounds b = limit.Tiers[t];
                if (b.Max.HasValue && b.Max.Value - value < best)
                {
                    best = b.Max.Value - value; r.Next = t; r.NextBound = "max"; r.NextLimit = b.Max.Value;
                }
                if (b.Min.HasValue && value - b.Min.Value < best)
                {
                    best = value - b.Min.Value; r.Next = t; r.NextBound = "min"; r.NextLimit = b.Min.Value;
                }
            }
            r.Margin = r.Next == None ? 0 : Math.Max(0, best);

            int topMax = limit.TopTier(true, out double hi);
            int topMin = limit.TopTier(false, out double lo);
            if (topMax == None && topMin == None) return r;

            double c;
            bool zeroInside = (topMin == None || lo < 0) && (topMax == None || hi > 0);
            if (zeroInside) c = 0;
            else if (topMax != None && topMin != None) c = 0.5 * (lo + hi);
            else return r; // one-sided and 0 outside it: no centre to measure from

            double? uMax = topMax != None && hi != c ? (value - c) / (hi - c) : (double?)null;
            double? uMin = topMin != None && lo != c ? (value - c) / (lo - c) : (double?)null;
            if (!uMax.HasValue && !uMin.HasValue) return r;

            bool maxSide = uMax.HasValue && (!uMin.HasValue || uMax.Value >= uMin.Value);
            double u = maxSide ? uMax.Value : uMin.Value;
            r.U = Math.Max(0, u);
            r.TopTier = maxSide ? topMax : topMin;
            double top = maxSide ? hi : lo;
            for (int t = Reference; t <= Alarm; t++)
            {
                double? b = maxSide ? limit.Tiers[t].Max : limit.Tiers[t].Min;
                if (!b.HasValue) continue;
                double pos = (b.Value - c) / (top - c);
                if (pos >= 0) r.Ticks[t] = pos;
            }
            return r;
        }

        /// <summary>
        /// One sensor's latest readings (one, or a vertical survey's depths) against its limit.
        /// Only fields named in the limit count, as in the backend; non-finite values are skipped.
        /// </summary>
        public static SensorResult Evaluate(LimitSet set, IReadOnlyList<Reading> readings)
        {
            var result = new SensorResult();
            if (set == null || set.IsEmpty || readings == null) return result;
            var effective = new Dictionary<string, FieldLimit>();
            foreach (Reading reading in readings)
            {
                if (reading == null) continue;
                Effective(set, reading.Index, effective);
                foreach (var kv in effective)
                {
                    if (kv.Value == null || !reading.Values.TryGetValue(kv.Key, out double v)) continue;
                    if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                    FieldResult f = Evaluate(kv.Key, kv.Value, v, reading.Index);
                    result.Fields.Add(f);
                    if (f.Reached > result.Reached) result.Reached = f.Reached;
                    if (f.U.HasValue && (result.Driving == null || f.U.Value > result.Driving.U.Value))
                        result.Driving = f;
                }
            }
            return result;
        }
    }
}
