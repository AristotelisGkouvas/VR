using System.Collections;
using UnityEngine;
using QuestMarkerApp.Core;

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// Sound and controller vibration for the moments that matter: a click, a control point
    /// set or undone, the alignment locking in, sensors landing, closing in on a navigation
    /// target. The sounds are synthesised at start-up — short, soft, no asset files.
    /// </summary>
    public class Feedback : MonoBehaviour
    {
        public enum Sound { Click, Place, Undo, Success, Warn, Whoosh, Drop, Ping, Tick }

        private const int Rate = 44100;

        /// <summary>Main-menu switch, remembered across launches.</summary>
        public static bool SoundOn
        {
            get => soundOn ?? (bool)(soundOn = PlayerPrefs.GetInt("ui.sound", 1) == 1);
            set { soundOn = value; PlayerPrefs.SetInt("ui.sound", value ? 1 : 0); Prefs.Save(); }
        }
        private static bool? soundOn;

        private static Feedback instance;
        private AudioSource source;
        private AudioClip[] clips;
        private readonly float[] lastPlayed = new float[16];
#if !MOBILE_PREVIEW
        private Coroutine leftBuzz, rightBuzz;
#endif

        private static Feedback Instance
        {
            get
            {
                if (instance != null) return instance;
                var go = new GameObject("Feedback");
                DontDestroyOnLoad(go);
                instance = go.AddComponent<Feedback>();
                return instance;
            }
        }

        private void Awake()
        {
            source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            clips = new AudioClip[16];
            clips[(int)Sound.Click] = Make("click", 0.05f, (t, i) => Tone(t, 1700, 0.025f) * 0.5f + Noise(i) * Env(t, 0.004f) * 0.3f);
            clips[(int)Sound.Tick] = Make("tick", 0.03f, (t, i) => Tone(t, 2400, 0.012f) * 0.35f);
            clips[(int)Sound.Place] = Make("place", 0.28f, (t, i) =>
                Note(t, 0f, 784, 0.09f) * 0.5f + Note(t, 0.09f, 1175, 0.16f) * 0.5f);
            clips[(int)Sound.Undo] = Make("undo", 0.26f, (t, i) =>
                Note(t, 0f, 880, 0.09f) * 0.45f + Note(t, 0.09f, 587, 0.15f) * 0.45f);
            clips[(int)Sound.Success] = Make("success", 0.7f, (t, i) =>
                (Note(t, 0f, 523, 0.5f) + Note(t, 0.08f, 659, 0.5f) + Note(t, 0.16f, 784, 0.5f)
                 + Note(t, 0.24f, 1047, 0.45f)) * 0.22f);
            clips[(int)Sound.Warn] = Make("warn", 0.32f, (t, i) =>
                (Square(t, 330) * Gate(t, 0f, 0.11f) + Square(t, 330) * Gate(t, 0.17f, 0.28f)) * 0.12f);
            clips[(int)Sound.Ping] = Make("ping", 0.25f, (t, i) => Tone(t, 1320, 0.09f) * 0.4f + Tone(t, 2640, 0.04f) * 0.1f);
            clips[(int)Sound.Drop] = Make("drop", 0.08f, (t, i) =>
                Mathf.Sin(2f * Mathf.PI * (260f * t - 900f * t * t)) * Env(t, 0.03f) * 0.35f);
            clips[(int)Sound.Whoosh] = MakeWhoosh(0.9f);
        }

        // ------------------------------------------------------------------
        //  Public API
        // ------------------------------------------------------------------

        /// <param name="minGap">Seconds this sound stays quiet after playing (bursts of drops).</param>
        public static void Play(Sound sound, float volume = 1f, float minGap = 0.03f)
        {
            if (!SoundOn) return;
            Feedback f = Instance;
            int k = (int)sound;
            if (Time.unscaledTime - f.lastPlayed[k] < minGap) return;
            f.lastPlayed[k] = Time.unscaledTime;
            AudioClip clip = f.clips[k];
            if (clip != null) f.source.PlayOneShot(clip, volume);
        }

#if !MOBILE_PREVIEW
        /// <summary>Vibrate one controller for a moment (replaces any buzz already running on it).</summary>
        public static void Buzz(OVRInput.Controller controller, float amplitude, float seconds = 0.06f, float frequency = 0.5f)
        {
            Feedback f = Instance;
            bool left = controller == OVRInput.Controller.LTouch;
            Coroutine running = left ? f.leftBuzz : f.rightBuzz;
            if (running != null) f.StopCoroutine(running);
            Coroutine c = f.StartCoroutine(f.BuzzFor(controller, amplitude, seconds, frequency));
            if (left) f.leftBuzz = c; else f.rightBuzz = c;
        }

        private IEnumerator BuzzFor(OVRInput.Controller controller, float amplitude, float seconds, float frequency)
        {
            OVRInput.SetControllerVibration(frequency, Mathf.Clamp01(amplitude), controller);
            yield return new WaitForSecondsRealtime(seconds);
            OVRInput.SetControllerVibration(0f, 0f, controller);
        }
#endif

        // ------------------------------------------------------------------
        //  Synthesis
        // ------------------------------------------------------------------

        private delegate float Wave(float t, int i);

        private static AudioClip Make(string name, float seconds, Wave wave)
        {
            int n = Mathf.CeilToInt(seconds * Rate);
            var data = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)Rate;
                // 3 ms fade at both ends: no clicks from a hard start or stop.
                float edge = Mathf.Min(1f, Mathf.Min(t, seconds - t) / 0.003f);
                data[i] = Mathf.Clamp(wave(t, i) * edge, -1f, 1f);
            }
            AudioClip clip = AudioClip.Create(name, n, 1, Rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        // Noise through a low-pass that opens and closes: air rushing past.
        private static AudioClip MakeWhoosh(float seconds)
        {
            int n = Mathf.CeilToInt(seconds * Rate);
            var data = new float[n];
            var rng = new System.Random(7);
            float y = 0f;
            for (int i = 0; i < n; i++)
            {
                float u = i / (float)n;
                float bell = Mathf.Sin(Mathf.PI * u);
                float cutoff = 0.01f + 0.12f * bell * bell;
                y += cutoff * ((float)rng.NextDouble() * 2f - 1f - y);
                data[i] = y * bell * 2.2f;
            }
            AudioClip clip = AudioClip.Create("whoosh", n, 1, Rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private static float Env(float t, float decay) => Mathf.Exp(-t / decay);
        private static float Tone(float t, float hz, float decay) => Mathf.Sin(2f * Mathf.PI * hz * t) * Env(t, decay);
        private static float Square(float t, float hz) => Mathf.Sign(Mathf.Sin(2f * Mathf.PI * hz * t)) * 0.6f
                                                         + Mathf.Sin(2f * Mathf.PI * hz * t) * 0.4f;
        private static float Gate(float t, float from, float to) => t >= from && t < to ? 1f : 0f;

        // A soft bell note starting at `start`, with a touch of the octave for brightness.
        private static float Note(float t, float start, float hz, float decay)
        {
            if (t < start) return 0f;
            float u = t - start;
            float attack = Mathf.Min(1f, u / 0.004f);
            return (Mathf.Sin(2f * Mathf.PI * hz * u) + 0.25f * Mathf.Sin(4f * Mathf.PI * hz * u)) * Env(u, decay) * attack;
        }

        private static float Noise(int i)
        {
            uint x = (uint)i * 747796405u + 2891336453u;
            x = ((x >> (int)((x >> 28) + 4u)) ^ x) * 277803737u;
            return ((x >> 22) ^ x) / (float)uint.MaxValue * 2f - 1f;
        }
    }
}
