using UnityEngine;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// The one <c>Camera.main</c> lookup per frame. Dozens of LateUpdate methods used to
    /// each ask for it; on Quest that search showed up in the profiler.
    /// </summary>
    public static class Headset
    {
        private static Camera cam;
        private static int frame = -1;

        public static Camera Camera
        {
            get
            {
                int f = Time.frameCount;
                if (f != frame || cam == null)
                {
                    frame = f;
                    cam = Camera.main;
                }
                return cam;
            }
        }
    }
}
