using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// Terraweb's GET replies on the headset's own storage (app-private), one file per server
    /// and request: loading a project once while online, in the office, is the "offline pack"
    /// (sensors, history, limits); on a site with no signal TerrawebClient answers the same
    /// requests from here. Files older than <see cref="KeepDays"/> are dropped at start.
    /// </summary>
    public static class OfflineCache
    {
        private const int KeepDays = 60;
        private static string dir;
        private static bool pruned;
        // Bumped by Clear: a write still queued from before must not bring a reply back.
        private static int generation;

        private static string Dir
        {
            get
            {
                if (dir != null) return dir;
                dir = Path.Combine(Application.persistentDataPath, "terraweb_offline");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        // Per server and per project: the API answers /sensors/... for the account's current
        // project, so the same path means different data for two projects.
        private static string FileFor(string server, int projectId, string path)
        {
            using (var sha = SHA1.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes((server ?? "") + "|" + projectId + "|" + path));
                var sb = new StringBuilder(server == null ? "x_" : Prefix(server)).Append(projectId).Append('_');
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return Path.Combine(Dir, sb.Append(".json").ToString());
            }
        }

        // One prefix per server, so Has can tell whether anything of it is on file.
        private static string Prefix(string server)
        {
            using (var sha = SHA1.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(server));
                return h[0].ToString("x2") + h[1].ToString("x2") + "_";
            }
        }

        /// <summary>Writes the reply off the main thread (a measurement page can be megabytes).</summary>
        public static void Save(string server, int projectId, string path, byte[] data)
        {
            if (string.IsNullOrEmpty(server) || data == null || data.Length == 0) return;
            string file;
            try { file = FileFor(server, projectId, path); }
            catch (Exception e) { Debug.LogWarning("[Offline] " + e.Message); return; }
            Prune();
            int gen = generation;
            Task.Run(() =>
            {
                try
                {
                    if (gen != generation) return; // cleared (logged out) since
                    string tmp = file + ".tmp";
                    File.WriteAllBytes(tmp, data);
                    if (File.Exists(file)) File.Delete(file);
                    File.Move(tmp, file);
                }
                catch (Exception e) { Debug.LogWarning("[Offline] Could not keep a reply: " + e.Message); }
            });
        }

        public static bool TryLoad(string server, int projectId, string path, out string text, out DateTime savedUtc)
        {
            text = null;
            savedUtc = DateTime.MinValue;
            try
            {
                string file = FileFor(server, projectId, path);
                if (!File.Exists(file)) return false;
                byte[] data = File.ReadAllBytes(file);
                int skip = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
                text = Encoding.UTF8.GetString(data, skip, data.Length - skip);
                savedUtc = File.GetLastWriteTimeUtc(file);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Offline] " + e.Message);
                return false;
            }
        }

        /// <summary>Anything of this project on this server on file.</summary>
        public static bool Has(string server, int projectId)
        {
            if (string.IsNullOrEmpty(server)) return false;
            try { return Directory.GetFiles(Dir, Prefix(server) + projectId + "_*.json").Length > 0; }
            catch { return false; }
        }

        /// <summary>Newest reply on file for this server (UTC), or null: when the pack was last refreshed.</summary>
        public static DateTime? Newest(string server)
        {
            if (string.IsNullOrEmpty(server)) return null;
            // The menu asks twice a second: a directory scan each time is wasted on a pack
            // that changes only when a project loads.
            if (server == newestServer && Time.unscaledTime < newestUntil) return newest;
            newestServer = server;
            newestUntil = Time.unscaledTime + 5f;
            newest = Scan(server);
            return newest;
        }

        private static string newestServer;
        private static float newestUntil;
        private static DateTime? newest;

        private static DateTime? Scan(string server)
        {
            try
            {
                DateTime? newest = null;
                foreach (string f in Directory.GetFiles(Dir, Prefix(server) + "*.json"))
                {
                    DateTime t = File.GetLastWriteTimeUtc(f);
                    if (newest == null || t > newest) newest = t;
                }
                return newest;
            }
            catch { return null; }
        }

        /// <summary>Drops every reply on file (logging out: the next user must not read them).</summary>
        public static void Clear()
        {
            generation++;
            try
            {
                foreach (string f in Directory.GetFiles(Dir)) File.Delete(f);
            }
            catch (Exception e) { Debug.LogWarning("[Offline] Clear: " + e.Message); }
        }

        private static void Prune()
        {
            if (pruned) return;
            pruned = true;
            string d = Dir;
            Task.Run(() =>
            {
                try
                {
                    DateTime limit = DateTime.UtcNow.AddDays(-KeepDays);
                    foreach (string f in Directory.GetFiles(d))
                        if (File.GetLastWriteTimeUtc(f) < limit) File.Delete(f);
                }
                catch (Exception e) { Debug.LogWarning("[Offline] Prune: " + e.Message); }
            });
        }
    }
}
