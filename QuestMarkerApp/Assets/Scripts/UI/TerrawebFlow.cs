using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using QuestMarkerApp.API;
using QuestMarkerApp.Core;
using QuestMarkerApp.Markers;

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// Headset-only path into Terraweb: log in, pick a project, pick an area (or the whole
    /// project), and its sensors load straight into the alignment panel — no PC needed.
    /// Every network reply checks it still belongs to the open flow: closing or backing out
    /// mid-load must not let a late reply reopen the panel or wipe an alignment.
    /// </summary>
    public class TerrawebFlow : MonoBehaviour
    {
        /// <summary>More than this many icons is unreadable and slow; ask for an area instead.</summary>
        private const int MaxSensors = 800;
        private const string Eyebrow = "Terraweb";

        private TerrawebClient client;
        private FlowPanel panel;
        private ProjectAlignment alignment;
        private MarkerManager markers;

        private readonly List<FlowField> loginFields = new List<FlowField>();
        private string projectName;
        private int selectedProjectId;
        private int session;

        // The last project/area that reached the room, so the menu can reload it (alert states,
        // history) without the lists. Remembered across launches alongside the saved project.
        private const string PrefLastProject = "tw.last.project";
        private const string PrefLastProjectName = "tw.last.projectName";
        private const string PrefLastArea = "tw.last.area";
        private const string PrefLastAreaName = "tw.last.areaName";
        private const string PrefLastLoaded = "tw.last.loaded";
        private string lastAreaId, lastAreaName;
        private DateTime lastLoaded = DateTime.MinValue;

        /// <summary>A reload makes sense: logged in, and the loaded project is the one in the room.</summary>
        public bool CanReload =>
            client != null && client.IsLoggedIn && selectedProjectId != 0 && lastLoaded != DateTime.MinValue
            && alignment != null && alignment.HasProject && alignment.TerrawebProjectId == selectedProjectId;

        /// <summary>"μόλις τώρα", "πριν 12 λεπτά", "πριν 3 ώρες", "χθες"...</summary>
        public string LoadedAgo()
        {
            if (lastLoaded == DateTime.MinValue) return "";
            TimeSpan ago = DateTime.UtcNow - lastLoaded;
            if (ago.TotalMinutes < 1) return L.T("μόλις τώρα", "just now");
            if (ago.TotalMinutes < 60)
                return L.En ? ((int)ago.TotalMinutes == 1 ? "1 min ago" : $"{(int)ago.TotalMinutes} min ago")
                    : $"πριν {(int)ago.TotalMinutes} λεπτά";
            if (ago.TotalHours < 24)
                return L.En ? ((int)ago.TotalHours == 1 ? "1 hour ago" : $"{(int)ago.TotalHours} hours ago")
                    : $"πριν {(int)ago.TotalHours} ώρες";
            return ago.TotalDays < 2 ? L.T("χθες", "yesterday")
                : L.En ? $"{(int)ago.TotalDays} days ago" : $"πριν {(int)ago.TotalDays} ημέρες";
        }

        /// <summary>When the loaded data was fetched (UTC); MinValue before the first load.</summary>
        public DateTime LastLoaded => lastLoaded;

        // The session a background refresh runs in: no panel, no alignment prompt, and a
        // failure only logs. Any other flow step bumps the session, which ends it.
        private int quietSession = -1;

        /// <summary>A background refresh (RefreshQuietly) is in flight.</summary>
        public bool IsRefreshing => quietSession == session;

        /// <summary>
        /// Re-fetch the loaded project's sensors without any panel, e.g. so the model's sensor
        /// dots follow Terraweb. Does nothing unless a reload makes sense or one is running.
        /// </summary>
        public void RefreshQuietly()
        {
            if (IsRefreshing || !CanReload || FlowPanel.IsOpen) return;
            session++;
            quietSession = session;
            Debug.Log("[Terraweb] Refreshing sensor positions in the background");
            LoadSensors(lastAreaId, lastAreaName);
        }

        /// <summary>Reload the same project and area from the main menu.</summary>
        public void Reload()
        {
            session++;
            if (!client.IsLoggedIn) { ShowLogin(null); return; }
            if (!CanReload) { LoadProjects(); return; }
            LoadSensors(lastAreaId, lastAreaName);
        }

        private void RememberLoaded(string areaId, string areaName)
        {
            lastAreaId = areaId;
            lastAreaName = areaName;
            lastLoaded = DateTime.UtcNow;
            PlayerPrefs.SetInt(PrefLastProject, selectedProjectId);
            PlayerPrefs.SetString(PrefLastProjectName, projectName ?? "");
            PlayerPrefs.SetString(PrefLastArea, areaId ?? "");
            PlayerPrefs.SetString(PrefLastAreaName, areaName ?? "");
            PlayerPrefs.SetString(PrefLastLoaded, lastLoaded.Ticks.ToString());
            PlayerPrefs.Save();
        }

        private void RestoreLoaded()
        {
            selectedProjectId = PlayerPrefs.GetInt(PrefLastProject, 0);
            projectName = PlayerPrefs.GetString(PrefLastProjectName, "");
            lastAreaId = PlayerPrefs.GetString(PrefLastArea, "");
            if (lastAreaId == "") lastAreaId = null;
            lastAreaName = PlayerPrefs.GetString(PrefLastAreaName, "");
            if (lastAreaName == "") lastAreaName = null;
            lastLoaded = long.TryParse(PlayerPrefs.GetString(PrefLastLoaded, ""), out long ticks)
                ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.MinValue;
        }

        public void Init(TerrawebClient terraweb, FlowPanel flowPanel, ProjectAlignment projectAlignment,
            MarkerManager markerManager)
        {
            client = terraweb;
            panel = flowPanel;
            alignment = projectAlignment;
            markers = markerManager;
            RestoreLoaded();
            panel.OnClosed += OnPanelClosed;
        }

        private void OnPanelClosed()
        {
            session++; // anything still in flight is now stale
            if (loginFields.Count > 2) loginFields[2].Value = ""; // never leave a password behind
        }

        private void OnDestroy()
        {
            if (panel != null) panel.OnClosed -= OnPanelClosed;
        }

        /// <summary>Entry point from the main menu.</summary>
        public void Begin()
        {
            session++;
            if (client.IsLoggedIn) LoadProjects();
            else ShowLogin(null);
        }

        // Wraps a network callback so it does nothing once the user has moved on.
        private Action<T> Live<T>(Action<T> body)
        {
            int s = session;
            return value => { if (s == session) body(value); };
        }

        private Action Live(Action body)
        {
            int s = session;
            return () => { if (s == session) body(); };
        }

        // Loading pages: B cancels (closes, which invalidates the session).
        private void Busy(string title, string heading, string text = "")
        {
            if (IsRefreshing) return;
            panel.ShowMessage(Eyebrow, title, heading, text, true, panel.Close);
        }

        // ------------------------------------------------------------------
        //  Login
        // ------------------------------------------------------------------

        private void ShowLogin(string error)
        {
            if (loginFields.Count == 0)
            {
                loginFields.Add(new FlowField { Label = "Server", Value = client.Server, Placeholder = TerrawebClient.DefaultServer });
                loginFields.Add(new FlowField { Label = "Χρήστης", Value = client.Username, Placeholder = "username" });
                loginFields.Add(new FlowField { Label = "Κωδικός", Secret = true, Placeholder = "••••••" });
            }
            // The fields are kept between visits: relabel them for the current language.
            loginFields[1].Label = L.T("Χρήστης", "User");
            loginFields[2].Label = L.T("Κωδικός", "Password");
            string login = L.T("Σύνδεση", "Log in");
            string title = error == null ? login : login + " · " + error;
            panel.ShowForm(Eyebrow, title, loginFields, login, Submit, panel.Close);
        }

        private void Submit(List<FlowField> fields)
        {
            string server = fields[0].Value, user = fields[1].Value, password = fields[2].Value;
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password))
            {
                ShowLogin(L.T("συμπλήρωσε χρήστη και κωδικό", "enter user and password"));
                return;
            }
            string target = string.IsNullOrWhiteSpace(server) ? TerrawebClient.DefaultServer : server.Trim();
            if (!target.StartsWith("http", StringComparison.OrdinalIgnoreCase)) target = "https://" + target;
            client.SetServer(target, TerrawebClient.NeedsUntrusted(target));
            fields[2].Value = ""; // the request has its own copy; nothing keeps the password
            Busy(L.T("Σύνδεση", "Log in"), L.T("Σύνδεση…", "Logging in…"), client.AllowUntrustedCertificate
                ? client.Server + L.T("\n(!) διεύθυνση IP: το πιστοποιητικό γίνεται δεκτό την πρώτη φορά και μετά ελέγχεται",
                    "\n(!) IP address: the certificate is accepted the first time and checked after that")
                : client.Server);
            StartCoroutine(client.Login(user.Trim(), password,
                Live<string>(error => panel.ShowMessage(Eyebrow, L.T("Σύνδεση", "Log in"), L.T("Η σύνδεση απέτυχε", "Login failed"), error, false,
                    () => ShowLogin(null))),
                Live(LoadProjects)));
        }

        // ------------------------------------------------------------------
        //  Project → area → sensors
        // ------------------------------------------------------------------

        private void LoadProjects()
        {
            Busy(L.T("Έργα", "Projects"), L.T("Φόρτωση έργων…", "Loading projects…"));
            StartCoroutine(client.GetProfile(
                Live<JObject>(_ => StartCoroutine(client.GetProjects(Live<JArray>(ShowProjects), Fail(LoadProjects)))),
                Fail(LoadProjects)));
        }

        private void ShowProjects(JArray projects)
        {
            var items = new List<FlowItem>();
            foreach (JToken p in projects)
            {
                if (!(p is JObject) || !((int?)p["id"] is int id))
                {
                    Debug.LogWarning("[Terraweb] Project without id skipped: " + (string)(p as JObject)?["name"]);
                    continue;
                }
                items.Add(new FlowItem
                {
                    Label = (string)p["name"],
                    Sub = string.IsNullOrEmpty((string)p["description"]) ? $"id {id}" : (string)p["description"],
                    Value = client.CurrentProjectId == id ? L.T("τρέχον", "current") : "",
                    Tag = p
                });
            }
            items.Add(new FlowItem { Label = L.T("Αποσύνδεση", "Log out"), Sub = client.Username, Tag = "logout" });

            panel.ShowList(Eyebrow, L.T("Διάλεξε έργο", "Pick a project"), items, item =>
            {
                if (item.Tag as string == "logout")
                {
                    client.Logout();
                    ShowLogin(null);
                    return;
                }
                var p = (JToken)item.Tag;
                if (!((int?)p["id"] is int id))
                {
                    Debug.LogWarning("[Terraweb] Project without id cannot be selected: " + (string)p["name"]);
                    return;
                }
                projectName = (string)p["name"];
                Busy(projectName, L.T("Επιλογή έργου…", "Selecting project…"),
                    L.T("Το ίδιο έργο γίνεται τρέχον και στο web του χρήστη.", "It also becomes the current project on the user's web app."));
                selectedProjectId = id;
                StartCoroutine(client.SelectProject(id, projectName, Live(LoadAreas), Fail(LoadProjects)));
            }, panel.Close);
        }

        private void LoadAreas()
        {
            Busy(projectName, L.T("Φόρτωση περιοχών…", "Loading areas…"));
            StartCoroutine(client.GetAreas(Live<JArray>(ShowAreas), Fail(LoadProjects)));
        }

        private void ShowAreas(JArray areas)
        {
            var items = new List<FlowItem>();
            foreach (JToken a in areas)
                items.Add(new FlowItem { Label = (string)a["name"], Sub = (string)a["description"], Tag = a });
            // Whole project last: on a big project it is the slow, usually refused choice.
            items.Add(new FlowItem { Label = L.T("Όλο το έργο", "Whole project"), Sub = L.T($"Έως {MaxSensors} αισθητήρες", $"Up to {MaxSensors} sensors"), Tag = null });

            panel.ShowList(Eyebrow, projectName, items, item =>
            {
                var area = item.Tag as JToken;
                LoadSensors((string)area?["area_id"], area != null ? (string)area["name"] : null);
            }, LoadProjects);
        }

        private void LoadSensors(string areaId, string areaName)
        {
            string scope = areaName == null ? projectName : $"{projectName} · {areaName}";
            string loadedAreaId = areaId, loadedAreaName = areaName;
            Busy(scope, L.T("Έλεγχος πλήθους…", "Counting sensors…"));
            // Ask for the count first: Line4Demo's whole project is 15,209 sensors (tens of MB of
            // JSON) — downloading that only to refuse it froze the headset.
            StartCoroutine(client.GetSensorCount(areaId, Live<int>(count =>
            {
                if (count > MaxSensors && IsRefreshing) { quietSession = -1; return; }
                if (count > MaxSensors)
                {
                    panel.ShowMessage(Eyebrow, scope, L.T($"Πολλοί αισθητήρες ({count})", $"Too many sensors ({count})"),
                        L.T($"Διάλεξε μια περιοχή — πάνω από {MaxSensors} εικονίδια δεν διαβάζονται στον χώρο.",
                            $"Pick an area — over {MaxSensors} icons are unreadable in the room."),
                        false, LoadAreas);
                    return;
                }
                Busy(scope, L.T($"Φόρτωση {count} αισθητήρων…", count == 1 ? "Loading 1 sensor…" : $"Loading {count} sensors…"));
                StartCoroutine(client.GetSensors(areaId,
                    Live<JArray>(sensors => StartCoroutine(LoadHistory(scope, sensors, loadedAreaId, loadedAreaName))),
                    Fail(LoadAreas)));
            }), Fail(LoadAreas)));
        }

        /// <summary>Above this many survey sensors the history download is skipped (too slow).</summary>
        private const int MaxHistorySensors = 200;
        private const int HistoryDepth = 500;   // newest readings per sensor (the API's page cap)
        private const int Parallel = 6;

        // The survey history of position-measuring sensors (types with x/y/z or dx/dy fields):
        // their measured positions place them precisely, and the displacement arrows / time
        // machine replay it. Other sensor families (piezometers, …) load as before.
        private System.Collections.IEnumerator LoadHistory(string scope, JArray sensors, string areaId, string areaName)
        {
            int token = session;
            SensorHistory.Clear(selectedProjectId);

            var typeIds = new HashSet<int>();
            foreach (JToken s in sensors)
                if (((bool?)s["is_active"] ?? true) && (int?)s["type"]?["type_id"] is int t && t > 0) typeIds.Add(t);
            var surveyTypes = new HashSet<int>();
            foreach (int t in typeIds)
            {
                JObject type = null;
                yield return client.GetSensorType(t, o => type = o, _ => { });
                if (token != session) yield break;
                var codes = new HashSet<string>();
                if (type?["fields"] is JArray fields)
                    foreach (JToken f in fields) codes.Add((string)f["codename"]);
                if ((codes.Contains("x") && codes.Contains("y")) || (codes.Contains("dx") && codes.Contains("dy")))
                    surveyTypes.Add(t);
            }

            var ids = new List<string>();
            foreach (JToken s in sensors)
                if (((bool?)s["is_active"] ?? true) && surveyTypes.Contains((int?)s["type"]?["type_id"] ?? 0))
                    ids.Add((string)s["sensor_id"]);

            if (ids.Count > 0 && ids.Count <= MaxHistorySensors)
            {
                int next = 0, done = 0, running = 0, shown = -1;
                while (done < ids.Count)
                {
                    while (running < Parallel && next < ids.Count)
                    {
                        string id = ids[next++];
                        running++;
                        StartCoroutine(client.GetMeasurements(id, HistoryDepth,
                            list =>
                            {
                                // A reply from a cancelled load must not land in the project
                                // opened since; the counters always advance or "n / N" sticks.
                                try { if (token == session) SensorHistory.Add(id, list); }
                                finally { running--; done++; }
                            },
                            _ => { running--; done++; }));
                    }
                    if (done != shown) Busy(scope, L.T("Ιστορικό μετρήσεων…", "Reading history…"),
                        L.En ? $"{shown = done} / {ids.Count} sensors" : $"{shown = done} / {ids.Count} αισθητήρες");
                    yield return null;
                    if (token != session) yield break;
                }
            }
            else if (ids.Count > MaxHistorySensors)
            {
                Debug.Log($"[Terraweb] {ids.Count} survey sensors — history skipped (limit {MaxHistorySensors})");
            }
            if (token != session) yield break;
            // Alert limits and the latest values they are judged on (after the history, which
            // already holds most 3D points' values). Never blocks the load: no permission or an
            // error only leaves the limit views off.
            Busy(scope, L.T("Όρια συναγερμού…", "Alert limits…"));
            yield return SensorLimits.Load(client, this, selectedProjectId, sensors, areaId, () => token == session);
            if (token != session) yield break;
            Apply(scope, sensors, areaId, areaName);
        }

        private void Apply(string scope, JArray sensors, string areaId, string areaName)
        {
            ProjectRequest project = TerrawebImport.ToProject(scope, sensors, false, out int skipped);
            project.terrawebProjectId = selectedProjectId;
            int n = project.points.Length;
            bool quiet = IsRefreshing;
            quietSession = -1;
            if (n == 0 && quiet) return; // keep what is there
            if (n == 0)
            {
                panel.ShowMessage(Eyebrow, scope, L.T("Κανένας αισθητήρας με θέση", "No sensor with a position"),
                    L.T($"{sensors.Count} αισθητήρες, κανένας με θέση x/y/z (μέτρηση ή συντεταγμένες αισθητήρα).",
                        sensors.Count == 1
                            ? "1 sensor, without an x/y/z position (measured or the sensor's own)."
                            : $"{sensors.Count} sensors, none with an x/y/z position (measured or the sensor's own)."),
                    false, LoadAreas);
                return;
            }

            // Only this project's sensors in the room: earlier test or bridge markers go.
            // ProjectAlignment keeps a saved alignment per project, so re-loading the same
            // project (e.g. to refresh alert states) keeps its control points.
            markers.ClearAllMarkers();
            alignment.SetProject(project);
            RememberLoaded(areaId, areaName);
            if (!quiet)
            {
                panel.Close();
                alignment.OpenIfNeeded(); // a reload of an aligned project leaves the panel shut
            }
            if (skipped > 0)
                Debug.Log($"[Terraweb] {scope}: {n} sensors loaded, {skipped} without x/y/z skipped");
        }

        private Action<string> Fail(Action back) =>
            Live<string>(error =>
            {
                if (IsRefreshing)
                {
                    quietSession = -1;
                    Debug.LogWarning("[Terraweb] Background refresh failed: " + error);
                    return;
                }
                if (!client.IsLoggedIn) { ShowLogin(L.T("η σύνδεση έληξε", "session expired")); return; }
                panel.ShowMessage(Eyebrow, L.T("Σφάλμα", "Error"), L.T("Κάτι πήγε στραβά", "Something went wrong"), error, false, back);
            });
    }
}
