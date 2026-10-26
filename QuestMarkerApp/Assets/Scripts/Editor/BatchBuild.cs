using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace QuestMarkerApp.Editor
{
    /// <summary>
    /// Command-line Quest build (the editor must be closed):
    /// Unity.exe -batchmode -quit -projectPath . -buildTarget Android
    ///   -executeMethod QuestMarkerApp.Editor.BatchBuild.BuildQuest -logFile build.log
    /// Writes quest.apk in the project folder; the exit code is non-zero when the build fails.
    /// </summary>
    public static class BatchBuild
    {
        [MenuItem("Quest Marker App/Build quest.apk")]
        public static void BuildQuest()
        {
            string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = "quest.apk",
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary s = report.summary;
            Debug.Log($"[BatchBuild] {s.result}: {s.totalSize / 1e6:F1} MB, {s.totalErrors} errors, {s.totalTime}");
            if (Application.isBatchMode && s.result != BuildResult.Succeeded) EditorApplication.Exit(1);
        }
    }
}
