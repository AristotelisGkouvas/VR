using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// The reference anchor everything (markers, control points) is placed relative to.
    ///
    /// On Quest it is a persisted Meta spatial anchor: saved to the device by UUID and, on the
    /// next launch, loaded, re-localised in the room and bound again — so the frame survives
    /// app restarts, recenters and tracking-origin changes, and an alignment stays valid. The
    /// OVRSpatialAnchor component keeps its transform glued to the room every frame.
    /// Without a localised anchor (no anchor support, other room, failure) it falls back to the
    /// last saved pose in tracking space and reports <see cref="IsPersistentAnchor"/> false, so
    /// callers know the frame may have moved.
    /// </summary>
    public class SpatialAnchorManager : MonoBehaviour
    {
        [Header("Anchor Settings")]
        [SerializeField] private bool autoCreateReferenceAnchor = true;
        [SerializeField] private bool persistAnchors = true;
        [Tooltip("Seconds to wait for the saved anchor to localise in the room before giving up.")]
        [SerializeField] private double localizeTimeout = 8.0;


        public event Action<Transform> OnReferenceAnchorCreated;
        public event Action<Transform> OnReferenceAnchorLoaded;
        public event Action<string> OnAnchorError;

        private const string PrefUuid = "anchor.reference.uuid";
        private const string PrefPose = "SpatialAnchors_reference"; // legacy key kept for the pose fallback

        private Transform referenceAnchor;
        private string referenceUuid;
        private bool isInitialized;
        private bool persistent;
#if !MOBILE_PREVIEW
        private OVRSpatialAnchor ovrAnchor;
#endif

        public Transform ReferenceAnchor => referenceAnchor;
        public bool IsInitialized => isInitialized;
        public string ReferenceAnchorUuid => referenceUuid;

        /// <summary>
        /// True when the reference is a Meta spatial anchor localised in this room: anything
        /// placed relative to it is where it was left, across restarts and recenters.
        /// </summary>
        public bool IsPersistentAnchor
        {
            get
            {
#if !MOBILE_PREVIEW
                return persistent && ovrAnchor != null && ovrAnchor.Localized;
#else
                return false;
#endif
            }
        }

        public bool AutoCreateReferenceAnchor { get => autoCreateReferenceAnchor; set => autoCreateReferenceAnchor = value; }
        public bool PersistAnchors { get => persistAnchors; set => persistAnchors = value; }

        private IEnumerator Start()
        {
            // Let tracking settle so the fallback pose / a new anchor isn't made at the origin.
            yield return new WaitForSeconds(0.5f);

            bool loaded = false;
            if (persistAnchors)
            {
                yield return LoadPersistentAnchor(ok => loaded = ok);
                if (!loaded) loaded = LoadSavedPose();
            }
            if (!loaded && autoCreateReferenceAnchor) CreateReferenceAnchorAtCurrentPosition();
            isInitialized = true;
        }

        // ------------------------------------------------------------------
        //  Create
        // ------------------------------------------------------------------

        public void CreateReferenceAnchorAtCurrentPosition()
        {
            Camera cam = Headset.Camera;
            if (cam == null)
            {
                CreateReferenceAnchor(Vector3.zero, Quaternion.identity);
                return;
            }
            Vector3 position = cam.transform.position;
            position.y = 0f;
            CreateReferenceAnchor(position, Quaternion.Euler(0, cam.transform.eulerAngles.y, 0));
        }

        public void ResetReferenceAnchor() => CreateReferenceAnchorAtCurrentPosition();

        public void CreateReferenceAnchor(Vector3 position, Quaternion rotation)
        {
            ReplaceAnchorObject(position, rotation, out GameObject anchorObj);
            persistent = false;
            referenceUuid = null;

#if !MOBILE_PREVIEW
            ovrAnchor = anchorObj.AddComponent<OVRSpatialAnchor>();
            if (persistAnchors) StartCoroutine(SaveWhenCreated(ovrAnchor));
#endif
            SavePose(position, rotation);
            OnReferenceAnchorCreated?.Invoke(referenceAnchor);
            Debug.Log($"[SpatialAnchor] Reference anchor created at {position}");
        }

        private void ReplaceAnchorObject(Vector3 position, Quaternion rotation, out GameObject anchorObj)
        {
#if !MOBILE_PREVIEW
            // Erase the previous saved anchor so the device doesn't collect stale ones.
            if (ovrAnchor != null && ovrAnchor.Created) ovrAnchor.EraseAnchorAsync();
            ovrAnchor = null;
#endif
            if (referenceAnchor != null) Destroy(referenceAnchor.gameObject);

            anchorObj = new GameObject("ReferenceAnchor");
            anchorObj.transform.SetPositionAndRotation(position, rotation);
            referenceAnchor = anchorObj.transform;
        }

#if !MOBILE_PREVIEW
        private IEnumerator SaveWhenCreated(OVRSpatialAnchor anchor)
        {
            var created = anchor.WhenCreatedAsync();
            while (!created.IsCompleted) yield return null;
            // Replaced meanwhile (a second reset, or the saved pose / anchor loaded): its
            // GameObject is gone and this is not a failure worth reporting to the user.
            if (anchor != ovrAnchor) yield break;
            if (anchor == null || !created.GetResult())
            {
                Fail(L.T("Το spatial anchor δεν δημιουργήθηκε (υποστήριξη anchors / άδεια;)",
                    "Spatial anchor not created (anchor support / permission?)"));
                yield break;
            }

            var save = anchor.SaveAnchorAsync();
            while (!save.IsCompleted) yield return null;
            if (anchor == null || anchor != ovrAnchor) yield break; // replaced meanwhile
            if (!save.GetResult().Success)
            {
                Fail(L.T("Αποθήκευση spatial anchor απέτυχε: ", "Saving the spatial anchor failed: ") + save.GetResult().Status);
                yield break;
            }

            referenceUuid = anchor.Uuid.ToString();
            persistent = true;
            // The UUID we overwrite was the only record of the previous saved anchor (one that
            // failed to localise, so it was never bound and ReplaceAnchorObject could not erase
            // it): erase it by UUID rather than leave it orphaned on the headset.
            if (Guid.TryParse(PlayerPrefs.GetString(PrefUuid, ""), out Guid previous)
                && previous != Guid.Empty && previous != anchor.Uuid)
                OVRSpatialAnchor.EraseAnchorsAsync(Array.Empty<OVRSpatialAnchor>(), new[] { previous });
            PlayerPrefs.SetString(PrefUuid, referenceUuid);
            Prefs.Save();
            Debug.Log($"[SpatialAnchor] Saved reference anchor {referenceUuid}");
        }
#endif

        // ------------------------------------------------------------------
        //  Load
        // ------------------------------------------------------------------

        private IEnumerator LoadPersistentAnchor(Action<bool> done)
        {
#if !MOBILE_PREVIEW
            string saved = PlayerPrefs.GetString(PrefUuid, "");
            if (!Guid.TryParse(saved, out Guid uuid) || uuid == Guid.Empty) { done(false); yield break; }

            var found = new List<OVRSpatialAnchor.UnboundAnchor>();
            var load = OVRSpatialAnchor.LoadUnboundAnchorsAsync(new[] { uuid }, found);
            while (!load.IsCompleted) yield return null;
            if (!load.GetResult().Success || found.Count == 0)
            {
                Debug.LogWarning($"[SpatialAnchor] Saved anchor {uuid} not found ({load.GetResult().Status})");
                done(false);
                yield break;
            }

            OVRSpatialAnchor.UnboundAnchor unbound = found[0];
            if (!unbound.Localized)
            {
                var localize = unbound.LocalizeAsync(localizeTimeout);
                while (!localize.IsCompleted) yield return null;
                if (!localize.GetResult())
                {
                    Debug.LogWarning("[SpatialAnchor] Saved anchor did not localise (different room?)");
                    done(false);
                    yield break;
                }
            }

            // Bind on the same frame the component is created (SDK requirement).
            ReplaceAnchorObject(Vector3.zero, Quaternion.identity, out GameObject anchorObj);
            ovrAnchor = anchorObj.AddComponent<OVRSpatialAnchor>();
            try
            {
                unbound.BindTo(ovrAnchor);
            }
            catch (Exception e)
            {
                Fail(L.T("Σύνδεση spatial anchor απέτυχε: ", "Attaching the spatial anchor failed: ") + e.Message);
                done(false);
                yield break;
            }

            referenceUuid = uuid.ToString();
            persistent = true;
            yield return null; // let the component write its first tracked pose
            OnReferenceAnchorLoaded?.Invoke(referenceAnchor);
            Debug.Log($"[SpatialAnchor] Reference anchor {uuid} restored and localised");
            done(true);
#else
            done(false);
            yield break;
#endif
        }

        // Fallback: the last pose in tracking space. Valid only if the tracking origin didn't move.
        private bool LoadSavedPose()
        {
            if (!TryParsePose(PlayerPrefs.GetString(PrefPose, ""), out Vector3 pos, out Quaternion rot)) return false;
            ReplaceAnchorObject(pos, rot, out GameObject anchorObj);
            persistent = false;
            referenceUuid = null;
#if !MOBILE_PREVIEW
            // Pin a fresh spatial anchor at the fallback pose. Without it, one failed
            // localisation (space data reset, another room) left every later launch on the
            // pose-only path for good: the saved pose always loads, so no anchor was ever
            // created again. ProjectAlignment still re-checks this frame before trusting it.
            ovrAnchor = anchorObj.AddComponent<OVRSpatialAnchor>();
            if (persistAnchors) StartCoroutine(SaveWhenCreated(ovrAnchor));
#endif
            OnReferenceAnchorLoaded?.Invoke(referenceAnchor);
            Debug.Log("[SpatialAnchor] Reference restored from saved pose only (not a spatial anchor); pinning a new anchor there");
            return true;
        }

        // ------------------------------------------------------------------
        //  Status / persistence helpers
        // ------------------------------------------------------------------

        public string GetStatusJson()
        {
            Vector3 p = referenceAnchor != null ? referenceAnchor.position : Vector3.zero;
            string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
            return "{" +
                   "\"initialized\":" + (isInitialized ? "true" : "false") + "," +
                   "\"persistent\":" + (IsPersistentAnchor ? "true" : "false") + "," +
                   "\"hasReference\":" + (referenceAnchor != null ? "true" : "false") + "," +
                   "\"referenceUuid\":\"" + (referenceUuid ?? "") + "\"," +
                   "\"position\":{\"x\":" + F(p.x) + ",\"y\":" + F(p.y) + ",\"z\":" + F(p.z) + "}}";
        }

        private void SavePose(Vector3 position, Quaternion rotation)
        {
            if (!persistAnchors) return;
            string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
            PlayerPrefs.SetString(PrefPose,
                $"{F(position.x)},{F(position.y)},{F(position.z)},{F(rotation.x)},{F(rotation.y)},{F(rotation.z)},{F(rotation.w)}");
            Prefs.Save();
        }

        private static bool TryParsePose(string data, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (string.IsNullOrEmpty(data)) return false;
            string[] parts = data.Split(',');
            if (parts.Length < 7) return false;
            var v = new float[7];
            for (int i = 0; i < 7; i++)
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return false;
            position = new Vector3(v[0], v[1], v[2]);
            rotation = new Quaternion(v[3], v[4], v[5], v[6]);
            return true;
        }

        private void Fail(string message)
        {
            OnAnchorError?.Invoke(message);
            Debug.LogWarning("[SpatialAnchor] " + message);
        }

    }
}
