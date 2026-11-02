using UnityEngine;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// PlayerPrefs.Save() writes the whole store to disk. Menu toggles used to do that on the
    /// click, which hitched the headset. <see cref="Save"/> marks the store dirty and a hidden
    /// runner flushes it once things have been quiet for a moment, and immediately if the
    /// headset is paused or the app quits.
    /// </summary>
    public static class Prefs
    {
        private const float QuietSeconds = 0.5f;

        private static bool dirty;
        private static float due;

        public static void Save()
        {
            dirty = true;
            due = Time.realtimeSinceStartup + QuietSeconds;
        }

        public static void Flush()
        {
            if (!dirty) return;
            dirty = false;
            PlayerPrefs.Save();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Boot()
        {
            var go = new GameObject(nameof(Prefs)) { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(go);
            go.AddComponent<Runner>();
        }

        private sealed class Runner : MonoBehaviour
        {
            private void Update()
            {
                if (dirty && Time.realtimeSinceStartup >= due) Flush();
            }

            private void OnApplicationPause(bool paused)
            {
                if (paused) Flush();
            }

            private void OnApplicationQuit() => Flush();
        }
    }
}
