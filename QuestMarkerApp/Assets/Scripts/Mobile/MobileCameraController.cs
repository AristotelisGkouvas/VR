using UnityEngine;

namespace QuestMarkerApp.Mobile
{
    /// <summary>
    /// Απλή 3D κάμερα για preview των markers σε κινητό (touch) ή στον Editor (ποντίκι).
    /// - 1 δάχτυλο drag / αριστερό κλικ drag: περιστροφή γύρω από το pivot
    /// - 2 δάχτυλα drag / δεξί κλικ drag: μετακίνηση (pan)
    /// - Pinch / scroll: zoom
    /// Δεν έχει καμία εξάρτηση από Meta XR SDK — δουλεύει σε κανονικό Android build.
    /// </summary>
    public class MobileCameraController : MonoBehaviour
    {
        [Header("Target")]
        [SerializeField] private Vector3 pivot = Vector3.zero;

        [Header("Orbit")]
        [SerializeField] private float distance = 6f;
        [SerializeField] private float minDistance = 1f;
        [SerializeField] private float maxDistance = 40f;
        [SerializeField] private float yaw = 0f;
        [SerializeField] private float pitch = 20f;
        [SerializeField] private float minPitch = -30f;
        [SerializeField] private float maxPitch = 85f;

        [Header("Sensitivity")]
        [SerializeField] private float rotateSpeed = 0.2f;   // μοίρες ανά pixel
        [SerializeField] private float panSpeed = 0.0015f;   // κλιμακώνεται με την απόσταση
        [SerializeField] private float pinchZoomSpeed = 0.01f;
        [SerializeField] private float scrollZoomSpeed = 0.1f;

        private Vector2 lastMousePos;

        private void Start()
        {
            ApplyTransform();
        }

        private void LateUpdate()
        {
            if (Input.touchCount > 0)
                HandleTouch();
            else
                HandleMouse();

            ApplyTransform();
        }

        private void HandleTouch()
        {
            if (Input.touchCount == 1)
            {
                Touch t = Input.GetTouch(0);
                if (t.phase == TouchPhase.Moved)
                {
                    yaw += t.deltaPosition.x * rotateSpeed;
                    pitch -= t.deltaPosition.y * rotateSpeed;
                    ClampPitch();
                }
            }
            else if (Input.touchCount >= 2)
            {
                Touch t0 = Input.GetTouch(0);
                Touch t1 = Input.GetTouch(1);

                // Pinch zoom
                Vector2 p0Prev = t0.position - t0.deltaPosition;
                Vector2 p1Prev = t1.position - t1.deltaPosition;
                float prevDist = (p0Prev - p1Prev).magnitude;
                float curDist = (t0.position - t1.position).magnitude;
                float delta = prevDist - curDist;
                Zoom(delta * pinchZoomSpeed);

                // Two-finger pan
                Vector2 avgDelta = (t0.deltaPosition + t1.deltaPosition) * 0.5f;
                Pan(avgDelta);
            }
        }

        private void HandleMouse()
        {
            if (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1))
                lastMousePos = Input.mousePosition;

            if (Input.GetMouseButton(0)) // rotate
            {
                Vector2 d = (Vector2)Input.mousePosition - lastMousePos;
                yaw += d.x * rotateSpeed;
                pitch -= d.y * rotateSpeed;
                ClampPitch();
                lastMousePos = Input.mousePosition;
            }
            else if (Input.GetMouseButton(1)) // pan
            {
                Vector2 d = (Vector2)Input.mousePosition - lastMousePos;
                Pan(d);
                lastMousePos = Input.mousePosition;
            }

            float scroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) > 0.001f)
                Zoom(-scroll * scrollZoomSpeed * 10f);
        }

        private void Zoom(float amount)
        {
            distance = Mathf.Clamp(distance + amount * distance, minDistance, maxDistance);
        }

        private void Pan(Vector2 screenDelta)
        {
            Vector3 right = transform.right;
            Vector3 up = transform.up;
            pivot -= (right * screenDelta.x + up * screenDelta.y) * panSpeed * distance;
        }

        private void ClampPitch()
        {
            pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
        }

        private void ApplyTransform()
        {
            Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 pos = pivot - (rot * Vector3.forward) * distance;
            transform.SetPositionAndRotation(pos, rot);
        }

        /// <summary>Ορίζει το κέντρο γύρω από το οποίο περιστρέφεται η κάμερα.</summary>
        public void SetPivot(Vector3 p)
        {
            pivot = p;
        }
    }
}
