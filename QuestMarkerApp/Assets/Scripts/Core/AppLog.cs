using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// The app's own log lines in a file. The headset's logcat holds about the last minute, so a
    /// field session was gone by the time it could be read. Keeps the tagged lines ("[QrAlign] …"),
    /// warnings and errors in persistentDataPath/logs, one file a day for two weeks.
    /// </summary>
    public static class AppLog
    {
        public const string FolderName = "logs";
        private const long MaxBytesPerFile = 5 * 1024 * 1024;
        private const int KeepDays = 14;

        private static readonly object Gate = new object();
        private static StreamWriter writer;
        private static long written;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Begin()
        {
            if (Application.isEditor) return;
            try
            {
                string folder = Path.Combine(Application.persistentDataPath, FolderName);
                Directory.CreateDirectory(folder);
                foreach (string old in Directory.GetFiles(folder, "app-*.log"))
                    if (File.GetLastWriteTime(old) < DateTime.Now.AddDays(-KeepDays)) File.Delete(old);
                string file = Path.Combine(folder, $"app-{DateTime.Now:yyyy-MM-dd}.log");
                writer = new StreamWriter(file, true, new UTF8Encoding(false)) { AutoFlush = true };
                written = new FileInfo(file).Length;
                writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} --- start, version {Application.version}");
                Application.logMessageReceivedThreaded += OnLog;
                Application.quitting += End;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AppLog] No log file: {e.Message}");
            }
        }

        // May run on any thread, and must not log: that would call itself.
        private static void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Log && (string.IsNullOrEmpty(message) || message[0] != '[')) return;
            lock (Gate)
            {
                if (writer == null || written > MaxBytesPerFile) return;
                try
                {
                    string kind = type == LogType.Log ? "" : type + " ";
                    string line = $"{DateTime.Now:HH:mm:ss.fff} {kind}{message}";
                    if ((type == LogType.Error || type == LogType.Exception) && !string.IsNullOrEmpty(stack))
                        line += "\n" + stack.TrimEnd();
                    writer.WriteLine(line);
                    written += line.Length + 1;
                }
                catch (IOException)
                {
                    writer = null;
                }
            }
        }

        private static void End()
        {
            Application.logMessageReceivedThreaded -= OnLog;
            lock (Gate)
            {
                writer?.Dispose();
                writer = null;
            }
        }
    }
}
