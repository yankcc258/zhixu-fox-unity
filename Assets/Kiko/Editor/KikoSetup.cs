// 一键搭项目（菜单「Kiko」，或者命令行 -executeMethod）：
//   ① Kiko/1 项目设置：URP（关 HDR、开 4x MSAA）、D3D11、关 flip model（透明窗口要求）、窗口模式、后台运行
//   ② Kiko/2 材质换成 lilToon：把 VRM 里的 MToon 材质按参数换成 lilToon，存到 Assets/Kiko/Materials 并重映射
//   ③ Kiko/3 生成场景：相机（透明背景）、灯、希狐、KikoPet
//   ④ Kiko/4 打包：Builds/ZhixuFox/ZhixuFox.exe
// 命令行（特蕾莎用）：
//   Unity.exe -batchmode -projectPath . -executeMethod Kiko.EditorTools.KikoSetup.Step1 -quit -logFile step1.log
//   Unity.exe -batchmode -projectPath . -executeMethod Kiko.EditorTools.KikoSetup.Step2 -quit -logFile step2.log
// 第 1 步改了渲染管线，lilToon 要重新编一遍着色器，所以分成两次启动。
using System.IO;
using System.Linq;
using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Kiko.EditorTools
{
    public static class KikoSetup
    {
        const string Root = "Assets/Kiko";
        const string VrmPath = Root + "/Models/kiko.vrm";
        const string ScenePath = Root + "/Scenes/Pet.unity";
        const string BuildPath = "Builds/ZhixuFox/ZhixuFox.exe";

        // ---------------- ① 项目设置 ----------------
        [MenuItem("Kiko/1 项目设置（URP + 透明窗口）")]
        public static void SetupProject()
        {
            Directory.CreateDirectory(Root + "/Settings");
            var rpPath = Root + "/Settings/KikoURP.asset";
            var rp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(rpPath);
            if (!rp)
            {
                var rd = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(rd, Root + "/Settings/KikoRenderer.asset");
                rp = UniversalRenderPipelineAsset.Create(rd);
                AssetDatabase.CreateAsset(rp, rpPath);
            }
            rp.supportsHDR = false; // UniWindowController：URP 下 HDR 必须关，不然背景透明会丢
            rp.msaaSampleCount = 4;
            rp.renderScale = 1f;
            var so = new SerializedObject(rp);
            var alpha = so.FindProperty("m_AllowPostProcessAlphaOutput"); // 「Alpha Processing」
            if (alpha != null) alpha.boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(rp);

            GraphicsSettings.defaultRenderPipeline = rp;
            var q = QualitySettings.GetQualityLevel();
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = rp;
            }
            QualitySettings.SetQualityLevel(q, false);

            PlayerSettings.companyName = "yank";
            PlayerSettings.productName = "ZhixuFox";
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 }); // D3D12 不支持透明
            PlayerSettings.useFlipModelSwapchain = false; // 透明窗口要求
            PlayerSettings.runInBackground = true;
            PlayerSettings.visibleInBackground = true;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 340;
            PlayerSettings.defaultScreenHeight = 440;
            PlayerSettings.resizableWindow = false;
            PlayerSettings.allowFullscreenSwitch = false;
            PlayerSettings.SplashScreen.show = false;
            PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);

            // 只用旧的 Input Manager：没装 Input System 包时选 Both 会让 UniWindowController 编译失败（ENABLE_INPUT_SYSTEM）。改这个要重启编辑器
            var ps = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset").FirstOrDefault();
            if (ps)
            {
                var pso = new SerializedObject(ps);
                var ih = pso.FindProperty("activeInputHandler");
                if (ih != null && ih.intValue != 0) { ih.intValue = 0; pso.ApplyModifiedPropertiesWithoutUndo(); Debug.LogWarning("[Kiko] Active Input Handling 改成了 Input Manager (Old)，请重启 Unity 编辑器"); }
            }
            AssetDatabase.SaveAssets();
            // 换了渲染管线以后 lilToon 要按 URP 重新生成着色器，不然全是洋红色
            if (!EditorApplication.ExecuteMenuItem("Assets/lilToon/[Shader] Refresh shaders")) Debug.LogWarning("[Kiko] 没找到 lilToon 的 Refresh shaders 菜单，请手动点 Assets/lilToon/[Shader] Refresh shaders");
            Debug.Log("[Kiko] ① 项目设置完成：URP " + rpPath + "，D3D11，flip model 关");
        }

        // ---------------- ② MToon → lilToon ----------------
        [MenuItem("Kiko/2 材质换成 lilToon")]
        public static void ConvertMaterials()
        {
            AssetDatabase.ImportAsset(VrmPath, ImportAssetOptions.ForceUpdate);
            var importer = AssetImporter.GetAtPath(VrmPath) as ScriptedImporter;
            if (!importer) { Debug.LogError("[Kiko] 找不到 VRM 导入器：" + VrmPath + "（UniVRM 装好了吗？）"); return; }
            var lil = Shader.Find("lilToon");
            if (!lil) { Debug.LogError("[Kiko] 找不到 lilToon 着色器"); return; }
            Directory.CreateDirectory(Root + "/Materials");
            int n = 0;
            foreach (var src in AssetDatabase.LoadAllAssetRepresentationsAtPath(VrmPath).OfType<Material>())
            {
                var path = $"{Root}/Materials/{Safe(src.name)}.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (!mat) { mat = new Material(lil) { name = src.name }; AssetDatabase.CreateAsset(mat, path); }
                FromMToon(src, mat);
                EditorUtility.SetDirty(mat);
                importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), src.name), mat);
                n++;
            }
            AssetDatabase.SaveAssets();
            importer.SaveAndReimport();
            Debug.Log($"[Kiko] ② {n} 个材质换成 lilToon（{Root}/Materials），已重映射到 kiko.vrm");
        }

        static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        static float F(Material m, string p, float d = 0) => m.HasProperty(p) ? m.GetFloat(p) : d;
        static Color C(Material m, string p, Color d) => m.HasProperty(p) ? m.GetColor(p) : d;
        static Texture T(Material m, string p) => m.HasProperty(p) ? m.GetTexture(p) : null;

        /// MToon10 的参数按意思搬到 lilToon（阴影色/贴图、阴影边界和软硬、边缘光、描边、透明方式、双面）
        static void FromMToon(Material s, Material d)
        {
            d.shader = Shader.Find("lilToon");
            int alphaMode = Mathf.RoundToInt(F(s, "_AlphaMode"));
            bool outline = Mathf.RoundToInt(F(s, "_OutlineWidthMode")) > 0 && F(s, "_OutlineWidth") > 0;
            int mode = alphaMode == 1 ? 1 : alphaMode == 2 ? 2 : 0; // lilToon RenderingMode: Opaque 0 / Cutout 1 / Transparent 2
            LilSetup(d, mode, outline);

            d.SetTexture("_MainTex", T(s, "_MainTex"));
            d.SetTextureScale("_MainTex", s.HasProperty("_MainTex") ? s.GetTextureScale("_MainTex") : Vector2.one);
            d.SetTextureOffset("_MainTex", s.HasProperty("_MainTex") ? s.GetTextureOffset("_MainTex") : Vector2.zero);
            d.SetColor("_Color", C(s, "_Color", Color.white));
            d.SetFloat("_Cutoff", F(s, "_Cutoff", 0.5f));
            d.SetFloat("_Cull", F(s, "_DoubleSided") > 0.5f ? 0 : 2);

            var bump = T(s, "_BumpMap");
            d.SetFloat("_UseBumpMap", bump ? 1 : 0);
            if (bump) { d.SetTexture("_BumpMap", bump); d.SetFloat("_BumpScale", F(s, "_BumpScale", 1)); }

            // 阴影：MToon 的 shade 颜色/贴图 → lilToon 第一层阴影；toony 越高边越硬，shift 越负阴影越少
            d.SetFloat("_UseShadow", 1);
            d.SetFloat("_ShadowStrength", 1);
            d.SetColor("_ShadowColor", C(s, "_ShadeColor", new Color(0.8f, 0.8f, 0.85f)));
            var shadeTex = T(s, "_ShadeTex");
            if (shadeTex) d.SetTexture("_ShadowColorTex", shadeTex);
            float toony = F(s, "_ShadingToonyFactor", 0.9f), shift = F(s, "_ShadingShiftFactor", 0f);
            d.SetFloat("_ShadowBorder", Mathf.Clamp01(0.5f - shift * 0.5f));
            d.SetFloat("_ShadowBlur", Mathf.Clamp(1f - toony, 0.03f, 0.6f));

            var rim = C(s, "_RimColor", Color.black);
            bool useRim = rim.maxColorComponent > 0.01f;
            d.SetFloat("_UseRim", useRim ? 1 : 0);
            if (useRim) { d.SetColor("_RimColor", rim); d.SetFloat("_RimFresnelPower", F(s, "_RimFresnelPower", 3.5f)); }

            if (outline)
            {
                // MToon 世界坐标宽度（米）→ lilToon 宽度（厘米）
                d.SetFloat("_OutlineWidth", F(s, "_OutlineWidth") * 100f);
                d.SetColor("_OutlineColor", C(s, "_OutlineColor", Color.black));
            }
        }

        // lilToon 的编辑器程序集不自动引用，用反射调它的 SetupMaterialWithRenderingMode（选对 Opaque/Cutout/Transparent + 描边变体）
        static MethodInfo _lilSetup;
        static void LilSetup(Material m, int renderingMode, bool outline)
        {
            if (_lilSetup == null)
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "lilToon.Editor");
                var t = asm?.GetType("lilToon.lilToonInspector");
                _lilSetup = t?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(x => x.Name == "SetupMaterialWithRenderingMode" && x.GetParameters().Length == 7);
                if (_lilSetup == null) throw new Exception("找不到 lilToon.lilToonInspector.SetupMaterialWithRenderingMode（lilToon 版本变了？）");
            }
            var ps = _lilSetup.GetParameters();
            _lilSetup.Invoke(null, new object[]
            {
                m, Enum.ToObject(ps[1].ParameterType, renderingMode), Enum.ToObject(ps[2].ParameterType, 0), outline, false, false, false,
            });
        }

        // ---------------- ③ 场景 ----------------
        [MenuItem("Kiko/3 生成场景")]
        public static void CreateScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
            cam.allowHDR = false;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 50f;
            var camData = camGo.AddComponent<UniversalAdditionalCameraData>();
            camData.renderPostProcessing = false;

            var lightGo = new GameObject("Key Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.0f;
            light.color = new Color(1f, 0.98f, 0.96f);
            lightGo.transform.rotation = Quaternion.Euler(35f, 160f, 0f); // 从她前上方偏左打光
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.56f, 0.62f);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(VrmPath);
            if (!prefab) Debug.LogError("[Kiko] 读不到 " + VrmPath);
            else PrefabUtility.InstantiatePrefab(prefab);

            var pet = new GameObject("Kiko");
            pet.AddComponent<KikoPet>();

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            Debug.Log("[Kiko] ③ 场景 " + ScenePath);
        }

        // ---------------- ④ 打包 ----------------
        [MenuItem("Kiko/4 打包 Windows")]
        public static void Build()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BuildPath));
            var r = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = BuildPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            });
            var mb = r.summary.totalSize / 1048576.0;
            Debug.Log($"[Kiko] ④ 打包 {r.summary.result}：{BuildPath}，{mb:F1} MB，{r.summary.totalTime.TotalSeconds:F0} 秒，错误 {r.summary.totalErrors}");
            if (Application.isBatchMode && r.summary.result != BuildResult.Succeeded) EditorApplication.Exit(1);
        }

        // ---------------- 命令行入口 ----------------
        public static void Step1() => SetupProject();

        public static void Step2()
        {
            ConvertMaterials();
            CreateScene();
            Build();
        }
    }
}
