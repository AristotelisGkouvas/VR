using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using QuestMarkerApp.Core;

namespace QuestMarkerApp.Models
{
    /// <summary>
    /// The design models on the headset: every file in persistentDataPath/models with its
    /// sidecar "&lt;file&gt;.json" (written by the LAN upload handler) that says how to read it:
    /// units, which axis is up, whether it is georeferenced, an explicit origin and the
    /// Terraweb project it belongs to. A textured model (OBJ + MTL + images, sent as one zip)
    /// is a package: a subfolder holding exactly one .obj, described by its "model.json".
    /// Nothing is parsed here beyond the sidecar, so the menu can list fifty models
    /// instantly; ModelOverlay does the heavy load on request.
    /// </summary>
    public class ModelStore
    {
        private const string LastKey = "model.last";
        private static readonly string[] ModelExtensions = { ".obj", ".dxf", ".ifc" };
        /// <summary>Sidecar file inside a package folder.</summary>
        public const string PackageSidecar = "model.json";
        /// <summary>Prefix of the folder a zip is extracted into before it replaces the package; never listed.</summary>
        public const string ExtractingPrefix = ".extract-";

        public class ModelInfo
        {
            /// <summary>Display name: the sidecar's "name", else the file name without extension.</summary>
            public string Name;
            public string Path;
            /// <summary>"obj" | "dxf" | "ifc" (from the sidecar, else the extension).</summary>
            public string Format;
            public long Bytes;
            public DateTime Modified;
            public ImportOptions Options;
            /// <summary>Terraweb project id as text, or null when the model is not tied to one.</summary>
            public string ProjectKey;
            public int ProjectId;
            /// <summary>Name of the file as uploaded (the sidecar's "sourceFile"), for the list.</summary>
            public string SourceFile;
            /// <summary>True for a package folder (OBJ + MTL + images); <see cref="Path"/> is then its .obj.</summary>
            public bool IsPackage;
            /// <summary>The package folder, null for a single-file model.</summary>
            public string Folder;

            public override string ToString() => $"{Name} ({Format}, {Bytes / 1024} KB)";
        }

        /// <summary>persistentDataPath/models, created on first use so an upload never fails on a fresh device.</summary>
        public static string Directory
        {
            get
            {
                string dir = System.IO.Path.Combine(Application.persistentDataPath, "models");
                try { System.IO.Directory.CreateDirectory(dir); }
                catch (Exception e) { Debug.LogWarning($"[ModelStore] Cannot create {dir}: {e.Message}"); }
                return dir;
            }
        }

        /// <summary>Name of the model last loaded into the overlay (PlayerPrefs), so a restart can bring it back.</summary>
        public static string LastLoadedName
        {
            get => PlayerPrefs.GetString(LastKey, "");
            set
            {
                if (string.IsNullOrEmpty(value)) PlayerPrefs.DeleteKey(LastKey);
                else PlayerPrefs.SetString(LastKey, value);
                Prefs.Save();
            }
        }

        /// <summary>All models on the device, newest first.</summary>
        public static List<ModelInfo> List()
        {
            var result = new List<ModelInfo>();
            string dir = Directory;
            string[] files;
            try { files = System.IO.Directory.GetFiles(dir); }
            catch (Exception e)
            {
                Debug.LogWarning($"[ModelStore] Cannot list {dir}: {e.Message}");
                return result;
            }
            foreach (string file in files)
            {
                string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                if (Array.IndexOf(ModelExtensions, ext) < 0) continue; // sidecars, .meta, partial uploads
                try { result.Add(Describe(file)); }
                catch (Exception e) { Debug.LogWarning($"[ModelStore] Skipping {file}: {e.Message}"); }
            }

            string[] folders;
            try { folders = System.IO.Directory.GetDirectories(dir); }
            catch (Exception e)
            {
                Debug.LogWarning($"[ModelStore] Cannot list folders of {dir}: {e.Message}");
                folders = Array.Empty<string>();
            }
            foreach (string folder in folders)
            {
                try
                {
                    ModelInfo package = DescribePackage(folder);
                    if (package != null) result.Add(package);
                }
                catch (Exception e) { Debug.LogWarning($"[ModelStore] Skipping {folder}: {e.Message}"); }
            }

            result.Sort((a, b) => b.Modified.CompareTo(a.Modified));
            return result;
        }

        /// <summary>The single .obj at the top of a package folder, or null when it is not a package (none, several, or mid-extraction).</summary>
        public static string PackageObj(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            string leaf = System.IO.Path.GetFileName(folder.TrimEnd('/', '\\'));
            if (leaf.StartsWith(ExtractingPrefix, StringComparison.Ordinal)) return null;
            string[] objs;
            try { objs = System.IO.Directory.GetFiles(folder, "*.obj", SearchOption.TopDirectoryOnly); }
            catch { return null; }
            // GetFiles("*.obj") also matches ".objx" on some platforms: check the extension exactly.
            string found = null;
            foreach (string f in objs)
            {
                if (!string.Equals(System.IO.Path.GetExtension(f), ".obj", StringComparison.OrdinalIgnoreCase)) continue;
                if (found != null) return null;
                found = f;
            }
            return found;
        }

        /// <summary>The model called <paramref name="name"/> (display name, file name or package folder name), or null.</summary>
        public static ModelInfo Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (ModelInfo m in List())
            {
                if (string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(System.IO.Path.GetFileName(m.Path), name, StringComparison.OrdinalIgnoreCase)
                    || (m.IsPackage && string.Equals(System.IO.Path.GetFileName(m.Folder), name, StringComparison.OrdinalIgnoreCase)))
                    return m;
            }
            return null;
        }

        /// <summary>Removes the model file and its sidecar (or the whole package folder); forgets it as the last loaded one.</summary>
        public static void Delete(ModelInfo info)
        {
            if (info == null || string.IsNullOrEmpty(info.Path)) return;
            if (info.IsPackage)
            {
                try
                {
                    if (!string.IsNullOrEmpty(info.Folder) && System.IO.Directory.Exists(info.Folder))
                        System.IO.Directory.Delete(info.Folder, true);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ModelStore] Delete {info.Folder} failed: {e.Message}");
                }
                if (string.Equals(LastLoadedName, info.Name, StringComparison.OrdinalIgnoreCase)) LastLoadedName = null;
                return;
            }
            try
            {
                if (File.Exists(info.Path)) File.Delete(info.Path);
                foreach (string side in SidecarCandidates(info.Path))
                    if (File.Exists(side)) File.Delete(side);
                // An OBJ's material library sits next to it under the same base name.
                string mtl = System.IO.Path.ChangeExtension(info.Path, ".mtl");
                if (File.Exists(mtl)) File.Delete(mtl);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ModelStore] Delete {info.Path} failed: {e.Message}");
            }
            if (string.Equals(LastLoadedName, info.Name, StringComparison.OrdinalIgnoreCase)) LastLoadedName = null;
        }

        // "<file>.json" is what the upload handler writes; "<base>.json" is accepted for files
        // copied over by hand.
        private static IEnumerable<string> SidecarCandidates(string path)
        {
            yield return path + ".json";
            yield return System.IO.Path.ChangeExtension(path, ".json");
        }

        private static ModelInfo Describe(string path)
        {
            var fi = new FileInfo(path);
            var info = new ModelInfo
            {
                Name = System.IO.Path.GetFileNameWithoutExtension(path),
                Path = path,
                Format = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
                Bytes = fi.Length,
                Modified = fi.LastWriteTime,
                Options = ImportOptions.Default
            };
            ApplySidecar(info, SidecarCandidates(path));
            return info;
        }

        // A package is named after its folder (the upload's safe name) unless model.json says
        // otherwise; its size is the whole folder, textures included, since that is what it
        // costs on the device.
        private static ModelInfo DescribePackage(string folder)
        {
            string obj = PackageObj(folder);
            if (obj == null) return null;
            long bytes = 0;
            foreach (string f in System.IO.Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
            {
                try { bytes += new FileInfo(f).Length; } catch { /* vanished meanwhile */ }
            }
            string sidecar = System.IO.Path.Combine(folder, PackageSidecar);
            var info = new ModelInfo
            {
                Name = System.IO.Path.GetFileName(folder.TrimEnd('/', '\\')),
                Path = obj,
                Format = "obj",
                Bytes = bytes,
                Modified = File.Exists(sidecar) ? File.GetLastWriteTime(sidecar) : File.GetLastWriteTime(obj),
                Options = ImportOptions.Default,
                IsPackage = true,
                Folder = folder
            };
            ApplySidecar(info, new[] { sidecar });
            // The sidecar may say "obj" or nothing else useful; a package is only ever an OBJ.
            if (info.Format != "obj" && info.Format != "ifc") info.Format = "obj";
            return info;
        }

        private static void ApplySidecar(ModelInfo info, IEnumerable<string> candidates)
        {
            JObject side = null;
            foreach (string candidate in candidates)
            {
                if (!File.Exists(candidate)) continue;
                try { side = JObject.Parse(File.ReadAllText(candidate)); }
                catch (Exception ex) { Debug.LogWarning($"[ModelStore] Bad sidecar {candidate}: {ex.Message}"); }
                break;
            }
            if (side == null) return;

            string name = (string)side["name"];
            if (!string.IsNullOrWhiteSpace(name)) info.Name = name.Trim();
            string format = (string)side["format"];
            if (!string.IsNullOrWhiteSpace(format)) info.Format = format.Trim().ToLowerInvariant();
            info.SourceFile = (string)side["sourceFile"];

            ImportOptions o = ImportOptions.Default;
            float? scale = (float?)side["unitScale"];
            if (scale.HasValue && scale.Value > 0f) o.UnitScale = scale.Value;
            string up = (string)side["up"];
            o.ZUp = !string.Equals(up, "y", StringComparison.OrdinalIgnoreCase);
            bool? geo = (bool?)side["georeferenced"];
            if (geo.HasValue) o.Georeferenced = geo.Value;
            double? e = (double?)side["originE"], n = (double?)side["originN"], h = (double?)side["originH"];
            if (e.HasValue && n.HasValue && h.HasValue)
            {
                o.HasOrigin = true;
                o.OriginE = e.Value; o.OriginN = n.Value; o.OriginH = h.Value;
            }
            info.Options = o;

            int? project = (int?)side["projectId"];
            if (project.HasValue && project.Value > 0)
            {
                info.ProjectId = project.Value;
                info.ProjectKey = project.Value.ToString();
            }
        }
    }
}
