using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// Talks to the Terraweb2 backend straight from the headset.
    ///
    /// Backend facts this relies on (terraweb2-backend):
    ///  - POST /api/auth/login/ {username,password} → {access, refresh}; access lives 15 min,
    ///    refresh 7 days, POST /api/auth/refresh/ {refresh} → {access}.
    ///  - Every /api/sensors/ endpoint is scoped to profile.current_project, so choosing a
    ///    project PATCHes /api/auth/profile/ {current_project_id} — the same switch the web
    ///    app makes, so the user's web session follows it.
    ///  - List endpoints return a bare array unless ?page is sent, then {count, results}.
    /// Only the refresh token, server/user names and a self-signed server's certificate hash
    /// are persisted, never the password.
    /// </summary>
    public class TerrawebClient : MonoBehaviour
    {
        private const string PrefServer = "tw.server";
        private const string PrefUser = "tw.user";
        private const string PrefRefresh = "tw.refresh";
        private const string PrefUntrusted = "tw.untrusted";
        // Trust-on-first-use pin: SHA-256 of the certificate seen on the first request to a
        // host that is allowed an untrusted certificate. Key is PrefPin + host.
        private const string PrefPin = "tw.pin.";
        private static string CertificateChangedError => L.T(
            "Το πιστοποιητικό του server άλλαξε — έλεγξε τη σύνδεση ή καθάρισε τον server από τις ρυθμίσεις.",
            "The server's certificate changed — check the connection or clear the server in settings.");
        public const string DefaultServer = "https://demo.iqsoft.xyz";
        // Earlier builds defaulted to the bare-IP server; carry such installs over to the default.
        private const string LegacyServer = "https://89.116.27.151";

        public string Server { get; private set; }
        public string Username { get; private set; }
        /// <summary>Accept a self-signed certificate from <see cref="Server"/> (site servers often have one).</summary>
        public bool AllowUntrustedCertificate { get; private set; }
        public bool IsLoggedIn => !string.IsNullOrEmpty(refreshToken);
        public int? CurrentProjectId { get; private set; }
        public string CurrentProjectName { get; private set; }

        private string accessToken;
        private string refreshToken;
        private long lastResponseCode;
        private readonly Dictionary<int, JObject> typeCache = new Dictionary<int, JObject>();
        // One refresh at a time: parallel requests that all hit 401 share it instead of each
        // rotating the refresh token (the second rotation is rejected and used to log out).
        private bool refreshing;
        private float refreshStartedAt;
        private readonly List<Action<bool>> refreshWaiters = new List<Action<bool>>();
        // These coroutines run on the caller's MonoBehaviour: if it is disabled mid-refresh the
        // coroutine just stops and "refreshing" would stay set for good, parking every later
        // request. Past the request timeout (15 s) plus slack, a refresh counts as abandoned.
        private const float RefreshStaleSeconds = 25f;
        private bool RefreshInFlight => refreshing && Time.unscaledTime - refreshStartedAt < RefreshStaleSeconds;

        private void Awake()
        {
            Server = UpgradeToHttps(PlayerPrefs.GetString(PrefServer, DefaultServer));
            Username = PlayerPrefs.GetString(PrefUser, "");
            refreshToken = Unprotect(PlayerPrefs.GetString(PrefRefresh, ""));
            if (Server == LegacyServer)
            {
                Server = DefaultServer;
                refreshToken = ""; // a token from the old server means nothing here
                PlayerPrefs.SetString(PrefServer, Server);
                PlayerPrefs.DeleteKey(PrefRefresh);
                PlayerPrefs.DeleteKey(PrefUntrusted);
                PlayerPrefs.DeleteKey(PinKey(LegacyServer));
                PlayerPrefs.Save();
            }
            AllowUntrustedCertificate = PlayerPrefs.GetInt(PrefUntrusted, NeedsUntrusted(Server) ? 1 : 0) == 1;
        }

        /// <summary>
        /// A bare-IP https server almost always carries a self-signed certificate; a named host
        /// (like the default) has a real one and gets normal certificate validation.
        /// </summary>
        public static bool NeedsUntrusted(string server)
        {
            if (!Uri.TryCreate(server, UriKind.Absolute, out Uri uri)) return false;
            return uri.Scheme == Uri.UriSchemeHttps && uri.HostNameType != UriHostNameType.Dns;
        }

        private IEnumerator Start()
        {
            if (string.IsNullOrEmpty(refreshToken)) yield break;
            yield return RefreshAccess(_ => { });
            if (IsLoggedIn) yield return GetProfile(_ => { }, _ => { });
        }

        public void SetServer(string server, bool allowUntrusted)
        {
            server = (server ?? "").Trim().TrimEnd('/');
            if (server.Length > 0 && !server.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                server = "https://" + server;
            server = UpgradeToHttps(server);
            if (server != Server)
            {
                Logout();
                // Leaving a server forgets its pinned certificate: coming back pins afresh.
                if (!string.IsNullOrEmpty(Server)) PlayerPrefs.DeleteKey(PinKey(Server));
            }
            Server = server;
            AllowUntrustedCertificate = allowUntrusted;
            PlayerPrefs.SetString(PrefServer, Server);
            PlayerPrefs.SetInt(PrefUntrusted, allowUntrusted ? 1 : 0);
            PlayerPrefs.Save();
        }

        /// <summary>Credentials never travel in the clear: a typed http:// server becomes https://.</summary>
        private static string UpgradeToHttps(string server)
        {
            if (string.IsNullOrEmpty(server) || !server.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return server;
            string upgraded = "https://" + server.Substring("http://".Length);
            Debug.Log($"[Terraweb] Server {server} upgraded to {upgraded}: credentials are never sent over plain http");
            return upgraded;
        }

        private static string PinKey(string server)
        {
            string host = Uri.TryCreate(server, UriKind.Absolute, out Uri uri) ? uri.Host : server;
            return PrefPin + (host ?? "").ToLowerInvariant();
        }

        /// <summary>The user logs out: the session and the offline copy of its replies go.</summary>
        public void Logout()
        {
            Logout(true);
            OfflineCache.Clear();
            ServingOffline = false;
        }

        /// <param name="revoke">False when the refresh token is already known dead on the server.</param>
        private void Logout(bool revoke)
        {
            string access = accessToken, refresh = refreshToken, server = Server;
            // Local state goes first and unconditionally: a dead network must not keep a
            // session alive on the headset. The server-side revocation is fire and forget.
            accessToken = refreshToken = null;
            CurrentProjectId = null;
            CurrentProjectName = null;
            typeCache.Clear();
            PlayerPrefs.DeleteKey(PrefRefresh);
            PlayerPrefs.Save();
            if (revoke && !string.IsNullOrEmpty(refresh) && isActiveAndEnabled)
                StartCoroutine(RevokeOnServer(server, access, refresh));
        }

        /// <summary>
        /// POST auth/logout/ {refresh} blacklists the refresh token, but the endpoint itself
        /// needs a valid access token. With none (or an expired one) the refresh token mints
        /// one first — and if that rotates the refresh token, the rotated one is the one revoked.
        /// Runs on captured tokens only: the instance is already logged out.
        /// </summary>
        private IEnumerator RevokeOnServer(string server, string access, string refresh)
        {
            var body = new JObject { ["refresh"] = refresh };
            if (!string.IsNullOrEmpty(access))
            {
                bool expired = false;
                yield return Send("POST", "auth/logout/", body, true, _ => { }, _ => expired = lastResponseCode == 401,
                    true, access);
                if (!expired) yield break;
            }
            if (Server != server) yield break; // the user switched servers meanwhile
            JToken result = null;
            yield return Send("POST", "auth/refresh/", body, false, r => result = r, _ => { });
            access = (string)(result as JObject)?["access"];
            if (string.IsNullOrEmpty(access) || Server != server) yield break; // refresh token already invalid
            string rotated = (string)(result as JObject)?["refresh"];
            if (!string.IsNullOrEmpty(rotated)) body["refresh"] = rotated;
            yield return Send("POST", "auth/logout/", body, true, _ => { }, _ => { }, true, access);
        }

        // ------------------------------------------------------------------
        //  Auth
        // ------------------------------------------------------------------

        public IEnumerator Login(string username, string password, Action<string> onError, Action onDone)
        {
            var body = new JObject { ["username"] = username, ["password"] = password };
            JToken result = null;
            string error = null;
            yield return Send("POST", "auth/login/", body, false, r => result = r, e => error = e);
            if (error != null) { onError(error); yield break; }

            var tokens = result as JObject;
            string access = (string)tokens?["access"], refresh = (string)tokens?["refresh"];
            if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh))
            {
                onError(L.T("Μη έγκυρη απάντηση σύνδεσης από τον server", "Invalid login response from the server"));
                yield break;
            }
            accessToken = access;
            refreshToken = refresh;
            Username = username;
            PlayerPrefs.SetString(PrefUser, username);
            PlayerPrefs.SetString(PrefRefresh, Protect(refreshToken));
            PlayerPrefs.Save();
            onDone();
        }

        private IEnumerator RefreshAccess(Action<bool> done)
        {
            if (RefreshInFlight)
            {
                // Join the refresh already in flight; its outcome is ours too.
                bool? outcome = null;
                Action<bool> waiter = r => outcome = r;
                refreshWaiters.Add(waiter);
                while (outcome == null && RefreshInFlight) yield return null;
                if (outcome == null)
                {
                    // The owner of that refresh was stopped: give up rather than wait forever.
                    refreshWaiters.Remove(waiter);
                    done(false);
                    yield break;
                }
                done(outcome.Value);
                yield break;
            }
            if (string.IsNullOrEmpty(refreshToken)) { done(false); yield break; }

            refreshing = true;
            refreshStartedAt = Time.unscaledTime;
            string used = refreshToken;
            JToken result = null;
            yield return Send("POST", "auth/refresh/", new JObject { ["refresh"] = used }, false,
                r => result = r, _ => { });
            var tokens = result as JObject;
            if (refreshToken == used) // not logged out meanwhile
            {
                accessToken = (string)tokens?["access"];
                string rotated = (string)tokens?["refresh"];
                if (!string.IsNullOrEmpty(rotated))
                {
                    refreshToken = rotated;
                    PlayerPrefs.SetString(PrefRefresh, Protect(rotated));
                    PlayerPrefs.Save();
                }
                // Only a rejected refresh token (expired after 7 days) logs out; a dropped
                // network on site must not throw the session away. Waiters never log out:
                // this single call is the one Logout per failed refresh.
                if (accessToken == null && (lastResponseCode == 400 || lastResponseCode == 401)) Logout(false);
            }
            bool refreshed = accessToken != null;
            refreshing = false;
            var waiters = new List<Action<bool>>(refreshWaiters);
            refreshWaiters.Clear();
            done(refreshed);
            foreach (Action<bool> w in waiters) w(refreshed);
        }

        // ------------------------------------------------------------------
        //  Calls
        // ------------------------------------------------------------------

        public IEnumerator GetProfile(Action<JObject> ok, Action<string> fail) =>
            Get("auth/profile/", t =>
            {
                var profile = t as JObject;
                if (profile == null) { fail(L.T("Μη έγκυρη απάντηση προφίλ", "Invalid profile response")); return; }
                var current = profile["current_project"] as JObject;
                CurrentProjectId = (int?)current?["id"];
                CurrentProjectName = (string)current?["name"];
                ok(profile);
            }, fail);

        public IEnumerator GetProjects(Action<JArray> ok, Action<string> fail) =>
            Get("project/", t => ok(Results(t)), fail);

        public IEnumerator SelectProject(int id, string name, Action ok, Action<string> fail)
        {
            void Selected()
            {
                CurrentProjectId = id;
                CurrentProjectName = name;
                typeCache.Clear(); // sensor types are per project database
                ok();
            }
            yield return Send("PATCH", "auth/profile/", new JObject { ["current_project_id"] = id }, true,
                _ => Selected(),
                error =>
                {
                    // Offline with the project's replies on file: carry on from them.
                    if (lastResponseCode == 0 && OfflineCache.Has(Server)) { ServingOffline = true; Selected(); }
                    else fail(error);
                });
        }

        public IEnumerator GetAreas(Action<JArray> ok, Action<string> fail) =>
            Get("sensors/areas/", t => ok(Results(t)), fail);

        public IEnumerator GetSensors(string areaId, Action<JArray> ok, Action<string> fail) =>
            Get(string.IsNullOrEmpty(areaId) ? "sensors/sensors/" : "sensors/sensors/?area=" + Uri.EscapeDataString(areaId),
                t => ok(Results(t)), fail);

        /// <summary>
        /// Sensor count without downloading the sensors: a paginated request returns {count, ...}.
        /// </summary>
        public IEnumerator GetSensorCount(string areaId, Action<int> ok, Action<string> fail) =>
            Get("sensors/sensors/?page=1&page_size=1" + (string.IsNullOrEmpty(areaId) ? "" : "&area=" + Uri.EscapeDataString(areaId)),
                t => ok((int?)(t as JObject)?["count"] ?? Results(t).Count), fail);

        /// <summary>One sensor, for its current alert state when its card opens.</summary>
        public IEnumerator GetSensor(string sensorId, Action<JObject> ok, Action<string> fail) =>
            Get($"sensors/sensors/{Uri.EscapeDataString(sensorId)}/", t => ok(t as JObject), fail);

        /// <summary>Newest first.</summary>
        public IEnumerator GetMeasurements(string sensorId, int count, Action<JArray> ok, Action<string> fail) =>
            Get($"sensors/measurements/?sensor={Uri.EscapeDataString(sensorId)}&ordering=-meas_date&page=1&page_size={count}",
                t => ok(Results(t)), fail);

        public IEnumerator GetSensorType(int typeId, Action<JObject> ok, Action<string> fail)
        {
            if (typeCache.TryGetValue(typeId, out JObject cached)) { ok(cached); yield break; }
            yield return Get($"sensors/types/{typeId}/", t =>
            {
                var type = t as JObject;
                if (type == null) { fail(L.T("Μη έγκυρη απάντηση τύπου αισθητήρα", "Invalid sensor type response")); return; }
                typeCache[typeId] = type;
                ok(type);
            }, fail);
        }

        /// <summary>
        /// Active alert events of the current project, a bare array. A sensor may carry several
        /// (one per breached bound); its tier is the highest among them.
        /// </summary>
        public IEnumerator GetActiveAlertEvents(Action<JArray> ok, Action<string> fail) =>
            Get("alerts/events/?active=true", t => ok(Results(t)), fail);

        /// <summary>
        /// The current project's active limits, every page (the endpoint always paginates, up to
        /// 10,000 a page: one request in practice). Each carries is_current; the caller keeps
        /// those. Field-crew accounts may lack CanViewLimits: a 403 calls
        /// <paramref name="forbidden"/>, not <paramref name="fail"/>.
        /// </summary>
        public IEnumerator GetLimits(Action<JArray> ok, Action forbidden, Action<string> fail)
        {
            const int MaxPages = 5;
            var all = new JArray();
            for (int page = 1; page <= MaxPages; page++)
            {
                JToken result = null;
                string error = null;
                bool denied = false;
                // lastResponseCode is read inside the callback, in the same frame Send set it.
                yield return Get($"sensors/limits/?is_active=true&page={page}&page_size=10000",
                    t => result = t, e => { if (lastResponseCode == 403) denied = true; else error = e; });
                if (denied) { forbidden(); yield break; }
                if (error != null) { fail(error); yield break; }
                foreach (JToken l in Results(result)) all.Add(l);
                if ((result as JObject)?["next"] == null || result["next"].Type == JTokenType.Null) break;
            }
            ok(all);
        }

        /// <summary>
        /// Readings of an area (or the whole project) since <paramref name="dateFromIso"/>, newest
        /// first, one page: the latest values of many sensors in a single request.
        /// </summary>
        public IEnumerator GetRecentMeasurements(string areaId, string dateFromIso, int pageSize,
            Action<JArray, int> ok, Action<string> fail) =>
            Get("sensors/measurements/?ordering=-meas_date&page=1&page_size=" + pageSize
                + (string.IsNullOrEmpty(areaId) ? "" : "&area=" + Uri.EscapeDataString(areaId))
                + (string.IsNullOrEmpty(dateFromIso) ? "" : "&date_from=" + Uri.EscapeDataString(dateFromIso)),
                t => ok(Results(t), (int?)(t as JObject)?["count"] ?? Results(t).Count), fail);

        private static JArray Results(JToken t) => t as JArray ?? ((t as JObject)?["results"] as JArray) ?? new JArray();

        /// <summary>Replies from this size up are parsed off the main thread, bytes.</summary>
        private const int ParseOnWorkerFrom = 64 * 1024;

        // UTF-8 without a leading BOM, as DownloadHandler.text gives it.
        private static string Decode(byte[] data)
        {
            int skip = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
            return Encoding.UTF8.GetString(data, skip, data.Length - skip);
        }

        /// <summary>
        /// The last replies came from the offline copy (no network): what the user sees is as
        /// old as <see cref="OfflineDataDate"/>. Cleared by the next live reply.
        /// </summary>
        public bool ServingOffline { get; private set; }
        /// <summary>When the oldest reply served offline was saved (UTC).</summary>
        public DateTime OfflineDataDate { get; private set; } = DateTime.MaxValue;

        // Every GET reply is kept on the headset (OfflineCache); with no network the same
        // request is answered from it, so a project loaded once in the office opens on site.
        private IEnumerator Get(string path, Action<JToken> ok, Action<string> fail)
        {
            string failed = null;
            yield return Send("GET", path, null, true, ok, e => failed = e);
            if (failed == null) yield break;
            if (lastResponseCode == 0 && OfflineCache.TryLoad(Server, path, out string text, out DateTime saved))
            {
                JToken cached = null;
                try { cached = JToken.Parse(text); }
                catch (Exception e) { Debug.LogWarning($"[Terraweb] Offline copy of {path} unreadable: {e.Message}"); }
                if (cached != null)
                {
                    if (!ServingOffline) Debug.Log("[Terraweb] No network: answering from the offline copy");
                    ServingOffline = true;
                    if (saved < OfflineDataDate) OfflineDataDate = saved;
                    ok(cached);
                    yield break;
                }
            }
            fail(failed);
        }

        // ------------------------------------------------------------------
        //  Transport
        // ------------------------------------------------------------------

        // bearer: an explicit access token (logout after the instance is cleared); otherwise
        // the current one, refreshed first when missing.
        private IEnumerator Send(string method, string path, JObject body, bool auth,
            Action<JToken> ok, Action<string> fail, bool retried = false, string bearer = null)
        {
            if (string.IsNullOrEmpty(Server)) { fail(L.T("Δεν έχει οριστεί server", "No server set")); yield break; }
            if (auth && bearer == null && string.IsNullOrEmpty(accessToken))
            {
                bool refreshed = false;
                yield return RefreshAccess(r => refreshed = r);
                if (!refreshed)
                {
                    fail(lastResponseCode == 0
                        ? L.T("Χωρίς σύνδεση δικτύου — δοκίμασε ξανά", "No network connection — try again")
                        : L.T("Χρειάζεται σύνδεση", "Login required"));
                    yield break;
                }
            }

            string usedToken = bearer ?? accessToken;
            using (var req = new UnityWebRequest($"{Server}/api/{path}", method))
            {
                req.downloadHandler = new DownloadHandlerBuffer();
                if (body != null)
                {
                    req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body.ToString()));
                    req.SetRequestHeader("Content-Type", "application/json");
                }
                if (auth) req.SetRequestHeader("Authorization", "Bearer " + usedToken);
                PinnedServerCertificate pin = null;
                string pinKey = null;
                if (AllowUntrustedCertificate)
                {
                    // The stored pin is read here, on the main thread: ValidateCertificate may
                    // run on a worker thread where PlayerPrefs is off limits.
                    pinKey = PinKey(Server);
                    pin = new PinnedServerCertificate(PlayerPrefs.GetString(pinKey, ""));
                    req.certificateHandler = pin;
                }
                req.timeout = 15; // on site a dead link should fail fast, with a retry

                yield return req.SendWebRequest();
                lastResponseCode = req.responseCode;

                if (pin != null)
                {
                    if (pin.Rejected)
                    {
                        fail(CertificateChangedError);
                        yield break;
                    }
                    if (pin.Pinned == null && !string.IsNullOrEmpty(pin.ObservedHash))
                    {
                        // First contact with this host: remember the certificate it showed.
                        PlayerPrefs.SetString(pinKey, pin.ObservedHash);
                        PlayerPrefs.Save();
                        Debug.Log($"[Terraweb] Pinned certificate of {Server} on first use");
                    }
                }

                if (req.responseCode == 401 && auth && !retried && bearer == null)
                {
                    // Expired after 15 min: refresh once and retry. Only drop the token this
                    // request carried — a parallel request may already have refreshed it.
                    if (accessToken == usedToken) accessToken = null;
                    yield return Send(method, path, body, auth, ok, fail, true);
                    yield break;
                }

                if (req.result != UnityWebRequest.Result.Success)
                {
                    fail(Describe(req));
                    yield break;
                }

                // A large reply (limits, measurement pages: up to megabytes) is decoded and parsed
                // on a worker thread: on the main thread it hitched the headset for a frame or more.
                JToken parsed = null;
                Exception parseError = null;
                byte[] data = req.downloadHandler.data;
                if (data == null || data.Length == 0) parsed = new JObject();
                else if (data.Length < ParseOnWorkerFrom)
                {
                    try { parsed = JToken.Parse(Decode(data)); }
                    catch (Exception e) { parseError = e; }
                }
                else
                {
                    Task<JToken> parse = Task.Run(() => JToken.Parse(Decode(data)));
                    while (!parse.IsCompleted) yield return null;
                    if (parse.IsFaulted) parseError = parse.Exception?.GetBaseException();
                    else parsed = parse.Result;
                }
                if (parsed == null)
                {
                    fail(L.T("Μη έγκυρη απάντηση: ", "Invalid response: ") + parseError?.Message);
                    yield break;
                }
                if (method == "GET")
                {
                    OfflineCache.Save(Server, path, data);
                    ServingOffline = false;
                    OfflineDataDate = DateTime.MaxValue;
                }
                // A throwing callback must surface as an error, not silently end this coroutine.
                try { ok(parsed); }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Terraweb] {method} {path}: callback failed — {e}");
                    fail(L.T("Σφάλμα επεξεργασίας απάντησης: ", "Error handling the response: ") + e.Message);
                }
            }
        }

        private static string Describe(UnityWebRequest req)
        {
            if (req.result == UnityWebRequest.Result.ConnectionError)
                return L.T("Δεν υπάρχει σύνδεση με τον server (", "Cannot reach the server (") + req.error + ")";
            string detail = null;
            try { detail = (string)JToken.Parse(req.downloadHandler.text)?["detail"]; }
            catch { /* not JSON */ }
            if (req.responseCode == 401) return detail ?? L.T("Λάθος στοιχεία σύνδεσης", "Wrong username or password");
            return $"HTTP {req.responseCode}: {detail ?? req.error}";
        }

        // The refresh token at rest: AES with a key bound to this device, so a copied
        // preferences file is useless elsewhere. (Not a defence against root on the device
        // itself — that would need the Android Keystore.)
        private static byte[] DeviceKey()
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return sha.ComputeHash(Encoding.UTF8.GetBytes("terraweb.quest|" + SystemInfo.deviceUniqueIdentifier));
        }

        private static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            using (var aes = System.Security.Cryptography.Aes.Create())
            {
                aes.Key = DeviceKey();
                aes.GenerateIV();
                byte[] data = Encoding.UTF8.GetBytes(plain);
                byte[] cipher = aes.CreateEncryptor().TransformFinalBlock(data, 0, data.Length);
                var all = new byte[aes.IV.Length + cipher.Length];
                Buffer.BlockCopy(aes.IV, 0, all, 0, aes.IV.Length);
                Buffer.BlockCopy(cipher, 0, all, aes.IV.Length, cipher.Length);
                return "v1:" + Convert.ToBase64String(all);
            }
        }

        private static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            if (!stored.StartsWith("v1:")) return stored; // token saved by an older build
            try
            {
                byte[] all = Convert.FromBase64String(stored.Substring(3));
                using (var aes = System.Security.Cryptography.Aes.Create())
                {
                    aes.Key = DeviceKey();
                    var iv = new byte[16];
                    Buffer.BlockCopy(all, 0, iv, 0, 16);
                    aes.IV = iv;
                    byte[] plain = aes.CreateDecryptor().TransformFinalBlock(all, 16, all.Length - 16);
                    return Encoding.UTF8.GetString(plain);
                }
            }
            catch
            {
                return ""; // unreadable → log in again
            }
        }

        /// <summary>
        /// Trust-on-first-use for a self-signed server certificate. Only attached when the user
        /// left "untrusted certificate" on for that server. With no pin stored yet the first
        /// certificate seen is accepted and reported through <see cref="ObservedHash"/> for the
        /// caller to store; afterwards only that exact certificate passes.
        /// ValidateCertificate may run off the main thread, so this class touches no Unity API:
        /// the stored pin comes in through the constructor and the result goes out via fields.
        /// </summary>
        private class PinnedServerCertificate : CertificateHandler
        {
            /// <summary>Stored SHA-256 (hex) of the host's certificate, or null when none yet.</summary>
            public string Pinned { get; }
            /// <summary>SHA-256 (hex) of the certificate the server presented, once validated.</summary>
            public string ObservedHash { get; private set; }
            /// <summary>True when a pin exists and the presented certificate did not match it.</summary>
            public bool Rejected { get; private set; }

            public PinnedServerCertificate(string pinned)
            {
                Pinned = string.IsNullOrEmpty(pinned) ? null : pinned;
            }

            protected override bool ValidateCertificate(byte[] certificateData)
            {
                if (certificateData == null || certificateData.Length == 0) return false;
                string hash;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    hash = BitConverter.ToString(sha.ComputeHash(certificateData)).Replace("-", "");
                if (ObservedHash == null) ObservedHash = hash;
                if (Pinned == null) return true; // first use: trust, the caller stores the hash
                if (string.Equals(hash, Pinned, StringComparison.OrdinalIgnoreCase)) return true;
                Rejected = true;
                return false;
            }
        }
    }
}
