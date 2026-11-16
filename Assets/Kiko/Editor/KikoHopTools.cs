#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Kiko.EditorTools
{
    public static class KikoHopTools
    {
        [MenuItem("Kiko/8a 检查轻巧跳跃轨迹")]
        public static void Check()
        {
            void Assert(bool ok, string label) { if (!ok) throw new Exception("[Kiko] hop check: " + label); }
            Assert(!KikoHop.Reachable(835, 480, 1), "reject giant launch");
            Assert(KikoHop.AlreadyThere(-.5f, 0), "consume repeated landing");
            Assert(!KikoHop.AlreadyThere(0, 10), "distinct ledges");
            int n = 0;
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            foreach (float rise in new[] { -300f, -40f, 0f, 40f, 180f })
            foreach (float dx in new[] { -240f, 0f, 240f })
            {
                var f = KikoHop.Plan(500 * scale, 600 * scale, (500 + dx) * scale, (600 - rise) * scale, scale, 176 * scale);
                f.Sample(1, out float x, out float y);
                Assert(Math.Abs(x - f.ToX) < .01f && Math.Abs(y - f.ToY) < .01f, "exact landing");
                for (int i = 0; i <= 200; i++)
                {
                    f.Sample(i / 200f, out x, out y);
                    Assert(y >= 176 * scale - .01f && y >= Math.Min(f.FromY, f.ToY) - 32 * scale - .01f, "apex/headroom");
                }
                Assert(f.Duration > .1f && f.Duration < 1.4f, "duration");
                n++;
            }
            Debug.Log($"[Kiko] hop checks PASS: {n} flights + reach/no-op checks");
        }

        [MenuItem("Kiko/8b 打包轻巧跳跃预览（当前构建场景）")]
        public static void BuildPreview()
        {
            Check();
            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0) throw new Exception("No enabled build scene; choose the current model scene first.");
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = "Builds/HopPreview/ZhixuFox.exe",
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.CompressWithLz4HC
            });
            Debug.Log($"[Kiko] HopPreview {report.summary.result}, errors {report.summary.totalErrors}, {report.summary.totalSize / 1048576f:F1} MB");
            if (Application.isBatchMode && report.summary.result != BuildResult.Succeeded) EditorApplication.Exit(1);
        }
    }
}
#endif
