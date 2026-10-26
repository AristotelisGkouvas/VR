// Speech engines behind the voice commands (see VoiceCommands.cs).
//
// The project ships without a speech recogniser: there is no offline Greek model small
// enough to bundle, and the Meta Voice SDK is not installed. WitRecognizer is therefore
// compiled only under the META_VOICE_SDK define and is a stub otherwise; KeyboardRecognizer
// lets the grammar be exercised on the headset today by typing the command.
//
// To enable real speech recognition (Wit.ai through the Meta Voice SDK):
//   1. Packages/manifest.json: add "com.meta.xr.sdk.voice" with the same version as
//      com.meta.xr.sdk.core (it comes from the same scoped registry / package source).
//   2. https://wit.ai: create an app, language "Greek", and copy its Server Access Token.
//      A Wit app has one language: for English speech create a second app, language
//      "English", and copy its token too (optional; without it English is heard by the
//      Greek app, which transcribes English words poorly).
//   3. Unity: Oculus > Voice SDK > Settings (or Create > Voice SDK > Configuration) — paste
//      the token to create a WitConfiguration asset. Name it "WitConfiguration" and put it
//      in a Resources folder (Assets/Resources/WitConfiguration.asset), or any folder if an
//      AppVoiceExperience with the configuration assigned already sits in the scene.
//      For the English app, a second asset named "WitConfiguration_en" in Resources
//      (Assets/Resources/WitConfiguration_en.asset). It is used while the interface language
//      is English (L.En) and the Greek one otherwise; the switch takes effect on the next
//      push-to-talk press, so a language change needs no restart. Missing: Greek is used.
//   4. Project Settings > Player > Scripting Define Symbols (Android): add META_VOICE_SDK.
//   5. The recogniser finds an AppVoiceExperience in the scene, or creates one and assigns
//      the configuration loaded from Resources. Microphone permission (RECORD_AUDIO) is
//      requested on first use; the SDK adds it to the manifest.
//   Wit needs no intents for this app: only the free-text transcription is used, so the
//   grammar in VoiceCommands.Parse stays the single source of truth.
using System;
using UnityEngine;
using QuestMarkerApp.Core;
#if META_VOICE_SDK
using Meta.WitAi.Configuration;
using Meta.WitAi.Data.Configuration;
using Oculus.Voice;
#endif

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// One push-to-talk speech engine: start, stop, and the text that was heard. Events are
    /// raised on the main thread.
    /// </summary>
    public interface IVoiceRecognizer
    {
        /// <summary>False when the engine cannot run here (package missing, no configuration).</summary>
        bool Available { get; }
        /// <summary>Short state for the menu row in the interface language, e.g. why it is unavailable.</summary>
        string Status { get; }
        void StartListening();
        void StopListening();
        /// <summary>The final transcript of one listening session.</summary>
        event Action<string> OnTranscript;
        event Action<string> OnError;
    }

    /// <summary>
    /// Wit.ai through the Meta Voice SDK. Push-to-talk: the request starts streaming the
    /// microphone on <see cref="StartListening"/> and is sent on <see cref="StopListening"/>.
    /// Without the META_VOICE_SDK define this is a stub that reports itself unavailable.
    /// </summary>
    public class WitRecognizer : IVoiceRecognizer
    {
        public event Action<string> OnTranscript;
        public event Action<string> OnError;

#if META_VOICE_SDK
        private const string ConfigResource = "WitConfiguration";
        private const string ConfigResourceEn = "WitConfiguration_en";
        private readonly AppVoiceExperience voice;
        private readonly string statusEl, statusEn;
        // The Greek configuration (the scene's, or from Resources) and the optional English one.
        private readonly WitConfiguration configEl, configEn;

        /// <param name="host">Parent for the AppVoiceExperience object when one has to be created.</param>
        public WitRecognizer(Transform host)
        {
            try
            {
                configEn = Resources.Load<WitConfiguration>(ConfigResourceEn);
                voice = UnityEngine.Object.FindAnyObjectByType<AppVoiceExperience>();
                if (voice == null)
                {
                    configEl = LoadConfiguration() ?? configEn;
                    if (configEl == null)
                    {
                        statusEl = "Λείπει το WitConfiguration (Resources)";
                        statusEn = "WitConfiguration missing (Resources)";
                        return;
                    }
                    var go = new GameObject("AppVoiceExperience");
                    if (host != null) go.transform.SetParent(host, false);
                    voice = go.AddComponent<AppVoiceExperience>();
                    voice.RuntimeConfiguration = new WitRuntimeConfiguration { witConfiguration = Wanted() };
                }
                else if (voice.RuntimeConfiguration == null || voice.RuntimeConfiguration.witConfiguration == null)
                {
                    configEl = LoadConfiguration() ?? configEn;
                    if (configEl == null)
                    {
                        statusEl = "Το AppVoiceExperience δεν έχει WitConfiguration";
                        statusEn = "AppVoiceExperience has no WitConfiguration";
                        voice = null;
                        return;
                    }
                    voice.RuntimeConfiguration = new WitRuntimeConfiguration { witConfiguration = Wanted() };
                }
                else
                {
                    // The scene's own configuration is the Greek one; English swaps in on demand.
                    configEl = voice.RuntimeConfiguration.witConfiguration;
                    ApplyLanguage();
                }
                voice.VoiceEvents.OnFullTranscription.AddListener(t => OnTranscript?.Invoke(t ?? ""));
                voice.VoiceEvents.OnError.AddListener((code, message) =>
                    OnError?.Invoke(string.IsNullOrEmpty(message) ? code : message));
                statusEl = statusEn = "Wit.ai";
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Voice] Wit unavailable: " + e.Message);
                voice = null;
                statusEl = "Σφάλμα Voice SDK";
                statusEn = "Voice SDK error";
            }
        }

        private static WitConfiguration LoadConfiguration()
        {
            // By name first (the documented setup), else any configuration asset in Resources
            // other than the English one.
            var config = Resources.Load<WitConfiguration>(ConfigResource);
            if (config != null) return config;
            WitConfiguration[] all = Resources.LoadAll<WitConfiguration>("");
            if (all == null) return null;
            foreach (WitConfiguration c in all)
                if (c != null && c.name != ConfigResourceEn) return c;
            return null;
        }

        // The English app while the interface is English and one is installed, else the Greek.
        private WitConfiguration Wanted() => L.En && configEn != null ? configEn : configEl;

        // Point the runtime configuration at the current language's Wit app. Only between
        // requests: a request in flight keeps the app it started with.
        private void ApplyLanguage()
        {
            if (voice == null || voice.Active) return;
            WitRuntimeConfiguration runtime = voice.RuntimeConfiguration;
            WitConfiguration wanted = Wanted();
            if (runtime == null || wanted == null || runtime.witConfiguration == wanted) return;
            runtime.witConfiguration = wanted;
            voice.RuntimeConfiguration = runtime;
            Debug.Log("[Voice] Wit configuration: " + wanted.name);
        }

        public bool Available => voice != null;
        public string Status => L.T(statusEl, statusEn);

        public void StartListening()
        {
            if (voice == null) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
            {
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
                OnError?.Invoke(L.T("Χρειάζεται άδεια μικροφώνου", "Microphone permission needed"));
                return;
            }
#endif
            if (voice.Active) voice.Deactivate();
            // The interface language may have changed since the last request.
            ApplyLanguage();
            // Immediately: push-to-talk must not wait for voice-activity detection to trigger.
            voice.ActivateImmediately();
        }

        public void StopListening()
        {
            // Deactivate stops the microphone and still sends what was recorded for transcription.
            if (voice != null && voice.Active) voice.Deactivate();
        }
#else
        public WitRecognizer(Transform host) { }
        public bool Available => false;
        public string Status => L.T("Χρειάζεται Meta Voice SDK (Wit.ai)", "Needs Meta Voice SDK (Wit.ai)");
        public void StartListening() { }
        public void StopListening() { }
#endif
    }

    /// <summary>
    /// Fallback "recogniser": the command is typed on the in-app keyboard. Lets the grammar,
    /// the sensor matching and the replies be tested on the headset before a speech engine
    /// is installed. The keyboard has Latin keys only, so the grammar also accepts
    /// greeklish / English keywords ("vres K5", "find K5").
    /// </summary>
    public class KeyboardRecognizer : IVoiceRecognizer
    {
        public event Action<string> OnTranscript;
        public event Action<string> OnError;

        public bool Available => true;
        public string Status => L.T("Πληκτρολόγιο", "Keyboard");

        public void StartListening()
        {
            if (VrKeyboard.IsOpen) return;
            VrKeyboard.Shared.Open(L.T("Φωνητική εντολή (πληκτρολόγηση)", "Voice command (typed)"), "", false,
                text => OnTranscript?.Invoke(text ?? ""),
                () => OnError?.Invoke(""));
        }

        /// <summary>Nothing to stop: the keyboard closes itself with OK or Άκυρο.</summary>
        public void StopListening() { }
    }
}
