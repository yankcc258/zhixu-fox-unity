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
            rp.msaaSampleCount = 2;
            rp.renderScale = 2f; // 2 倍超采样再缩回窗口大小（Tauri 版也是这样），1080p 上头发和蕾丝才不糊
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
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
        // 当前在换哪个模型的材质（默认仓库里的 kiko.vrm；买来的模型用 Assets/LocalModels/ 下的）
        static string _vrm = VrmPath, _matDir = Root + "/Materials", _texDir = Root + "/Textures/vrm";

        [MenuItem("Kiko/2 材质换成 lilToon")]
        public static void ConvertMaterials()
        {
            _vrm = VrmPath; _matDir = Root + "/Materials"; _texDir = Root + "/Textures/vrm";
            ConvertCurrent();
        }

        static void ConvertCurrent()
        {
            string VrmPath = _vrm;
            AssetDatabase.ImportAsset(VrmPath, ImportAssetOptions.ForceUpdate);
            var importer = AssetImporter.GetAtPath(VrmPath) as ScriptedImporter;
            if (!importer) { Debug.LogError("[Kiko] 找不到 VRM 导入器：" + VrmPath + "（UniVRM 装好了吗？）"); return; }
            // 已经重映射过的话，VRM 里就读不到原来的 MToon 材质了：先把映射拿掉、重新导入，拿到原始参数再换
            var old = importer.GetExternalObjectMap().Where(kv => kv.Key.type == typeof(Material)).Select(kv => kv.Key).ToList();
            if (old.Count > 0)
            {
                foreach (var id in old) importer.RemoveRemap(id);
                importer.SaveAndReimport();
            }
            var lil = Shader.Find("lilToon");
            if (!lil) { Debug.LogError("[Kiko] 找不到 lilToon 着色器"); return; }
            PrepareExternalTextures();
            Directory.CreateDirectory(_matDir);
            int n = 0;
            foreach (var src in AssetDatabase.LoadAllAssetRepresentationsAtPath(VrmPath).OfType<Material>())
            {
                var path = $"{_matDir}/{Safe(src.name)}.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (!mat) { mat = new Material(lil) { name = src.name }; AssetDatabase.CreateAsset(mat, path); }
                FromMToon(src, mat);
                EditorUtility.SetDirty(mat);
                importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), src.name), mat);
                n++;
            }
            AssetDatabase.SaveAssets();
            importer.SaveAndReimport();
            Debug.Log($"[Kiko] ② {n} 个材质换成 lilToon（{_matDir}），已重映射到 {VrmPath}");
        }

        // ---------- 贴图换成压缩过的外部 PNG（UniVRM 导入的子资源贴图是不压缩的 RGBA32：2048² 一张 21MB，打包 157MB、内存 360MB）----------
        // Tools/extract_textures.py 把 kiko.vrm 里的图片原样导出到 Textures/vrm/ + index.json（文件名、原名、尺寸、是否法线）
        [Serializable] class TexItem { public string file; public string name; public int w; public int h; public bool normal; }
        [Serializable] class TexIndex { public TexItem[] items; }
        static TexIndex _texIndex;

        static void PrepareExternalTextures()
        {
            var json = AssetDatabase.LoadAssetAtPath<TextAsset>(_texDir + "/index.json");
            if (!json) { Debug.LogWarning("[Kiko] 没有 " + _texDir + "/index.json，贴图保持不压缩"); _texIndex = null; return; }
            _texIndex = JsonUtility.FromJson<TexIndex>(json.text);
            int changed = 0;
            foreach (var it in _texIndex.items)
            {
                var ti = AssetImporter.GetAtPath($"{_texDir}/{it.file}") as TextureImporter;
                if (!ti) continue;
                var type = it.normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                if (ti.textureType == type && ti.textureCompression == TextureImporterCompression.CompressedHQ && ti.anisoLevel == 8) continue;
                ti.textureType = type;
                ti.sRGBTexture = !it.normal;
                ti.mipmapEnabled = true;
                ti.anisoLevel = 8;
                ti.maxTextureSize = 2048;
                ti.textureCompression = TextureImporterCompression.CompressedHQ; // PC 上 = BC7（法线 BC5），约原来的 1/4
                ti.SaveAndReimport();
                changed++;
            }
            Debug.Log($"[Kiko] 外部贴图 {_texIndex.items.Length} 张（这次改了导入设置 {changed} 张）");
        }

        /// VRM 子资源贴图 → 同名同尺寸的外部 PNG（唯一匹配才换；几张 64² 的小饰品贴图同名同尺寸，就留着不换）
        static Texture Ext(Texture t)
        {
            if (!t || _texIndex == null || AssetDatabase.GetAssetPath(t) != _vrm) return t;
            var c = _texIndex.items.Where(i => i.name == t.name && i.w == t.width && i.h == t.height).ToList();
            if (c.Count != 1) return t;
            var ext = AssetDatabase.LoadAssetAtPath<Texture2D>($"{_texDir}/{c[0].file}");
            return ext ? ext : t;
        }

        static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        static float F(Material m, string p, float d = 0) => m.HasProperty(p) ? m.GetFloat(p) : d;
        static Color C(Material m, string p, Color d) => m.HasProperty(p) ? m.GetColor(p) : d;
        static Texture T(Material m, string p) => Ext(m.HasProperty(p) ? m.GetTexture(p) : null);

        /// MToon10 的参数按意思搬到 lilToon（阴影色/贴图、阴影边界和软硬、边缘光、描边、透明方式、双面）
        static void FromMToon(Material s, Material d)
        {
            d.shader = Shader.Find("lilToon");
            int alphaMode = Mathf.RoundToInt(F(s, "_AlphaMode"));
            bool outline = Mathf.RoundToInt(F(s, "_OutlineWidthMode")) > 0 && F(s, "_OutlineWidth") > 0;
            int mode = alphaMode == 1 ? 1 : alphaMode == 2 ? 2 : 0; // lilToon RenderingMode: Opaque 0 / Cutout 1 / Transparent 2
            bool tail = s.name.Contains("FoxTail");
            // 马努卡：头发/耳朵/尾巴是半透明混合 → 改成裁切（透明窗口里半透明头发会透出桌面，也不用排序）；脸上的贴花（腮红、汗、黑脸…）保持半透明
            bool manukaHair = s.name.StartsWith("Manuka_hair");
            if (manukaHair) mode = 1;
            if (s.name.StartsWith("Manuka_option")) { mode = 2; outline = false; }
            if (tail && FurTail) { mode = 7; outline = false; } // FurTwoPass：底下一层不透明裁切 + 上面半透明毛尖，不怕和裙子排序打架
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

            // 身体：40D 透肤丝袜。贴图在丝袜区域预先混进肤色（正面透肉的样子），lilToon 的「边缘阴影」只在丝袜区域把侧面压暗
            // （= Tauri 版自写着色器的 mix(肤色, 黑丝, mix(1, 0.62, facing^2))），贴图和遮罩由 tools 里的脚本从 kiko.vrm 生成
            if (s.name.Contains("Body_00_SKIN"))
            {
                var sheer = AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "/Textures/body_sheer.png");
                var mask = AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "/Textures/body_sheer_mask.png");
                if (sheer && mask)
                {
                    d.SetTexture("_MainTex", sheer);
                    d.SetFloat("_UseRimShade", 1);
                    d.SetTexture("_RimShadeMask", mask);
                    d.SetColor("_RimShadeColor", new Color(0.30f, 0.28f, 0.33f, 1f));
                    d.SetFloat("_RimShadeBorder", 0.35f);
                    d.SetFloat("_RimShadeBlur", 0.75f);
                    d.SetFloat("_RimShadeFresnelPower", 2f);
                    d.SetFloat("_RimShadeNormalStrength", 1f);
                }
                else Debug.LogWarning("[Kiko] 没找到 body_sheer.png / body_sheer_mask.png，丝袜不透");
            }

            if (outline)
            {
                // MToon 世界坐标宽度（米）→ lilToon 宽度（厘米）
                d.SetFloat("_OutlineWidth", F(s, "_OutlineWidth") * 100f);
                d.SetColor("_OutlineColor", C(s, "_OutlineColor", Color.black));
            }
            Fluffy(s, d);
            if (manukaHair)
            {
                // 改成白毛以后，原来偏棕的描边 / 边缘光换成冷灰
                d.SetColor("_OutlineColor", new Color(0.56f, 0.59f, 0.70f, 1f));
                if (d.GetFloat("_UseRim") > 0.5f) d.SetColor("_RimColor", new Color(0.75f, 0.80f, 0.95f, 1f));
                d.SetFloat("_Cutoff", 0.5f);
            }
        }

        // ---------- 头发 / 耳朵 / 尾巴的「蓬松感」（主人：看上去就是一块）----------
        // 原来的阴影色只比底色暗 9%，白毛整片一个颜色，没有体积。这里（第一版太蓝太重，白发变紫，已调淡）：
        //  ① 两层冷色阴影、边缘软（球面一样慢慢暗下去）② 边缘阴影把轮廓一圈压暗（圆鼓鼓的感觉）③ 发丝法线加强
        //  ④ 尾巴换成 lilToon 的毛皮着色器：表面长出一层层短毛（几何着色器），轮廓真的毛茸茸
        public static bool FurTail = true;
        static void Fluffy(Material s, Material d)
        {
            string n = s.name;
            bool hair = n.Contains("_HAIR") || n.StartsWith("Manuka_hair"), tail = n.Contains("FoxTail"), ear = n.Contains("CatEar");
            if (!hair && !tail && !ear) return;
            d.SetFloat("_UseShadow", 1);
            d.SetColor("_ShadowColor", new Color(0.87f, 0.89f, 0.96f, 1f));
            d.SetFloat("_ShadowBorder", 0.55f);
            d.SetFloat("_ShadowBlur", 0.45f);
            d.SetColor("_Shadow2ndColor", new Color(0.76f, 0.79f, 0.90f, 1f));
            d.SetFloat("_Shadow2ndBorder", 0.25f);
            d.SetFloat("_Shadow2ndBlur", 0.35f);
            d.SetFloat("_UseRimShade", 1);
            d.SetColor("_RimShadeColor", new Color(0.82f, 0.85f, 0.94f, 1f));
            d.SetFloat("_RimShadeBorder", 0.5f);
            d.SetFloat("_RimShadeBlur", 0.6f);
            d.SetFloat("_RimShadeFresnelPower", 2f);
            if (d.GetFloat("_UseBumpMap") > 0.5f) d.SetFloat("_BumpScale", hair ? 1.3f : 1.1f);
            if (tail && FurTail)
            {
                var noise = AssetDatabase.LoadAssetAtPath<Texture2D>("Packages/jp.lilxyzw.liltoon/Texture/lil_noise_fur.png");
                d.SetTexture("_FurNoiseMask", noise);
                d.SetTextureScale("_FurNoiseMask", new Vector2(14, 14));
                d.SetVector("_FurVector", new Vector4(0, 0, 1, 0.03f)); // 法线方向长 3cm
                d.SetFloat("_FurGravity", 0.35f);
                d.SetFloat("_FurRandomize", 0.25f);
                d.SetFloat("_FurAO", 0.3f);       // 毛根暗一点 → 有层次
                d.SetFloat("_FurLayerNum", 3);
                d.SetFloat("_FurRootOffset", -0.25f);
                d.SetFloat("_FurCutoutLength", 0.7f);
                d.SetColor("_FurRimColor", new Color(0.92f, 0.94f, 1f, 1f));
                d.SetFloat("_FurRimFresnelPower", 3f);
                // 尾巴根部（藏在裙子里那段）不长毛，免得毛从后裙扎出来；遮罩由 Tools/shape_tail.py --fur-mask 生成
                var furMaskPath = Root + "/Textures/tail_fur_mask.png";
                if (AssetImporter.GetAtPath(furMaskPath) is TextureImporter fti && fti.sRGBTexture)
                {
                    fti.sRGBTexture = false;
                    fti.textureCompression = TextureImporterCompression.Uncompressed;
                    fti.SaveAndReimport();
                }
                var furMask = AssetDatabase.LoadAssetAtPath<Texture2D>(furMaskPath);
                if (furMask) d.SetTexture("_FurLengthMask", furMask);
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
        // ---------------- 换模型：买来的模型（不进仓库）放 Assets/LocalModels/，场景也生成在那里 ----------------
        const string LocalVrm = "Assets/LocalModels/manuka.vrm", LocalScene = "Assets/LocalModels/PetLocal.unity";
        static bool UseLocal => EditorPrefs.GetBool("kiko.localModel", false) && File.Exists(LocalScene);
        static string ActiveScene => UseLocal ? LocalScene : ScenePath;

        [MenuItem("Kiko/M1 换成马努卡（LocalModels／manuka.vrm）")]
        public static void UseManuka()
        {
            if (!File.Exists(LocalVrm))
            {
                // 第一次：从桌面「to luna」里解压出来的文件夹把 MANUKA.vrm 拷进工程（Assets/LocalModels 不进 git）
                var from = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory), "to luna");
                var src = Directory.Exists(from) ? Directory.GetFiles(from, "MANUKA.vrm", SearchOption.AllDirectories).FirstOrDefault() : null;
                if (src == null) { Debug.LogError("[Kiko] 没有 " + LocalVrm + "，桌面 to luna 里也没找到 MANUKA.vrm"); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(LocalVrm));
                File.Copy(src, LocalVrm);
                Debug.Log("[Kiko] 拷贝 " + src + " → " + LocalVrm);
                AssetDatabase.Refresh();
            }
            AssetDatabase.ImportAsset(LocalVrm);
            // 有外部贴图（Tools/manuka_to_kiko.py 导出 + 改色：白毛蓝眼）就换成 lilToon 材质 + 压缩贴图
            if (File.Exists("Assets/LocalModels/Textures/index.json"))
            {
                _vrm = LocalVrm; _matDir = "Assets/LocalModels/Materials"; _texDir = "Assets/LocalModels/Textures";
                AssetDatabase.Refresh();
                ConvertCurrent();
                _vrm = VrmPath; _matDir = Root + "/Materials"; _texDir = Root + "/Textures/vrm";
            }
            CreateSceneFor(LocalVrm, LocalScene);
            AddBaseLayer();
            AddOutfit();
            EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            EditorPrefs.SetBool("kiko.localModel", true);
            Debug.Log("[Kiko] 现在用马努卡：Play / 打包 都是 " + LocalScene);
        }

        // 衣服：BOOTH 买的马努卡对应衣服（本机 Assets/Demicrown/…，不进仓库）。预制体实例放进场景叫 KikoOutfit，运行时 KikoDress 把它穿到身上。
        // 材质换成我们自己的 lilToon 裁切材质（原来是半透明模式，在透明窗口里会透出桌面）；FBX 打开 Read/Write（运行时要改 bindpose）
        // 底层内衣：原版 MANUKA.fbx 里有 Manuka_underwear_bra / _panty 两个网格（VRM 里没有）。把 FBX 拷进 LocalModels，实例放进场景叫 KikoBase，
        // 只留这两个网格，材质用已经换好的衣服材质（内衣画在 Manuka_costume 那张贴图上）；运行时 KikoDress 把它当「最里面一层衣服」穿上。
        const string BaseFbx = "Assets/LocalModels/manuka_base.fbx";
        static void AddBaseLayer()
        {
            if (!File.Exists(BaseFbx))
            {
                var from = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory), "to luna");
                var src = Directory.Exists(from) ? Directory.GetFiles(from, "MANUKA.fbx", SearchOption.AllDirectories).FirstOrDefault() : null;
                if (src == null) { Debug.LogWarning("[Kiko] 没找到 MANUKA.fbx，没有内衣底层"); return; }
                File.Copy(src, BaseFbx);
                AssetDatabase.Refresh();
            }
            if (AssetImporter.GetAtPath(BaseFbx) is ModelImporter mi && (!mi.isReadable || mi.materialImportMode != ModelImporterMaterialImportMode.None))
            {
                mi.isReadable = true; mi.materialImportMode = ModelImporterMaterialImportMode.None; mi.importAnimation = false;
                mi.SaveAndReimport();
            }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(BaseFbx);
            var mat = AssetDatabase.LoadAssetAtPath<Material>("Assets/LocalModels/Materials/Manuka_costume_mtoon.mat");
            if (!prefab || !mat) { Debug.LogWarning("[Kiko] 内衣底层：缺 FBX 或衣服材质"); return; }
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            go.name = "KikoBase";
            // 身体也从 FBX 拿一份：VRM 里的身体把「穿长袜时把腿缩掉」的形态键烤死了（腿缩成一条线，靠白裤袜当腿），
            // 换别的衣服腿就空了（主人 11-10「腿不见了」）。FBX 里的身体是完整的，换装时用它顶替 VRM 的身体。
            var bodyMat = AssetDatabase.FindAssets("Manuka_body t:Material", new[] { "Assets/LocalModels/Materials" })
                .Select(g => AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(g))).FirstOrDefault();
            int kept = 0; bool gotBody = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                bool under = r is SkinnedMeshRenderer && (r.name.Contains("underwear_bra") || r.name.Contains("underwear_panty"));
                bool body = r is SkinnedMeshRenderer && r.name == "Manuka_body" && bodyMat;
                if (!under && !body) { UnityEngine.Object.DestroyImmediate(r.gameObject); continue; }
                r.sharedMaterials = r.sharedMaterials.Select(_ => body ? bodyMat : mat).ToArray();
                gotBody |= body;
                kept++;
            }
            if (!gotBody) Debug.LogWarning("[Kiko] FBX 里没找到 Manuka_body（或没有身体材质）——换装后腿会是空的");
            Debug.Log($"[Kiko] 内衣底层放进场景了（{kept} 个网格）");
        }

        // 衣柜里的衣服（都只在本机）。贴图用 Tools 里烤好的不透明版本（Assets/LocalModels/Textures/outfit_*.png）：
        // 原贴图的丝袜、薄纱是半透明的，在透明窗口里会把桌面透出来（主人 11-10「腿不见了」）→ 透肤的地方直接把肤色烤进贴图。
        static Material OutfitMat(string name, string baked, string fallback)
        {
            var texPath = File.Exists(baked) ? baked : fallback;
            if (AssetImporter.GetAtPath(texPath) is TextureImporter ti && (ti.maxTextureSize != 2048 || ti.textureCompression != TextureImporterCompression.CompressedHQ))
            {
                ti.maxTextureSize = 2048; ti.textureCompression = TextureImporterCompression.CompressedHQ; ti.anisoLevel = 8; ti.SaveAndReimport();
            }
            Directory.CreateDirectory("Assets/LocalModels/Materials");
            var path = $"Assets/LocalModels/Materials/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!mat) { mat = new Material(Shader.Find("lilToon")) { name = name }; AssetDatabase.CreateAsset(mat, path); }
            LilSetup(mat, 1, true);
            mat.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture2D>(texPath));
            mat.SetColor("_Color", Color.white);
            mat.SetFloat("_Cutoff", 0.5f);
            mat.SetFloat("_Cull", 0); // 裙子里面也画
            mat.SetFloat("_UseShadow", 1);
            mat.SetColor("_ShadowColor", new Color(0.78f, 0.78f, 0.88f, 1f));
            mat.SetFloat("_ShadowBorder", 0.5f);
            mat.SetFloat("_ShadowBlur", 0.2f);
            mat.SetFloat("_OutlineWidth", 0.04f);
            mat.SetColor("_OutlineColor", new Color(0.22f, 0.22f, 0.30f, 1f));
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static void PlaceOutfit(string prefabPath, string fbxPath, string label, bool keepBase, Func<SkinnedMeshRenderer, Material, Material> pick, string[] off = null)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (!prefab) { Debug.Log("[Kiko] 没有衣服预制体 " + prefabPath + "，跳过「" + label + "」"); return; }
            if (AssetImporter.GetAtPath(fbxPath) is ModelImporter mi && !mi.isReadable) { mi.isReadable = true; mi.SaveAndReimport(); }
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            go.name = "KikoOutfit_" + label;
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject); // VRChat 的 PhysBone 脚本这里没有
            foreach (var a in go.GetComponentsInChildren<Animator>(true)) UnityEngine.Object.DestroyImmediate(a);
            foreach (var r in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                r.sharedMaterials = r.sharedMaterials.Select(m => pick(r, m)).ToArray();
                if (r.name.Contains("(Grab)") || r.name.Contains("Breast_big")) r.enabled = false; // VRChat 机关用的备用网格
                if (off != null && off.Any(o => r.name.Contains(o))) r.enabled = false; // 这套衣服里不要的部件
            }
            var info = go.AddComponent<Kiko.KikoOutfitInfo>();
            info.label = label; info.keepBase = keepBase;
            Debug.Log("[Kiko] 衣服放进场景了：" + label + " ← " + prefabPath);
        }

        // 主人 11-10：泳装（沙滩女仆）的丝袜去掉，只留腿环
        static readonly string[] BeachOff = { "High_Knee_Socks" };

        // 只改场景里已有衣服的部件开关，不重建场景（主人自己在场景里调过的东西不会被 M1 冲掉）
        [MenuItem("Kiko/M1a 更新衣服部件开关（不重建场景）")]
        public static void RefreshOutfitParts()
        {
            int n = 0;
            foreach (var info in UnityEngine.Object.FindObjectsByType<Kiko.KikoOutfitInfo>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!info.label.Contains("沙滩")) continue;
                foreach (var r in info.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (r.enabled && BeachOff.Any(o => r.name.Contains(o))) { r.enabled = false; EditorUtility.SetDirty(r); n++; }
            }
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (n > 0) { EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene); }
            Debug.Log($"[Kiko] 衣服部件开关：关掉 {n} 个（{scene.path}）");
        }

        const string OutfitDir = "Assets/Demicrown/Cyber Maid_Manuka";
        const string BeachDir = "Assets/relee/beach_maid";
        static void AddOutfit()
        {
            const string T = "Assets/LocalModels/Textures/";
            if (File.Exists(OutfitDir + "/cybermaid_Manuka.prefab"))
            {
                Material body = OutfitMat("Outfit_Body", T + "outfit_cyber_body.png", OutfitDir + "/Materials/Texture/c002_Body_c.png"),
                         acc = OutfitMat("Outfit_Acc", T + "outfit_cyber_acc.png", OutfitDir + "/Materials/Texture/c002_Acc_c.png");
                PlaceOutfit(OutfitDir + "/cybermaid_Manuka.prefab", OutfitDir + "/cybermaid_Manuka.fbx", "赛博女仆", true, (r, m) => m && m.name.Contains("Body") ? body : acc);
            }
            if (File.Exists(BeachDir + "/Prefab/Manuka/Manuka_sky.prefab") && File.Exists(T + "outfit_beach_1.png"))
            {
                Material m1 = OutfitMat("Outfit_Beach1", T + "outfit_beach_1.png", null), m2 = OutfitMat("Outfit_Beach2", T + "outfit_beach_2.png", null);
                // 比基尼女仆：自己就是贴身的，下面不留底层内衣；围裙、袜子、鞋是第二张贴图
                PlaceOutfit(BeachDir + "/Prefab/Manuka/Manuka_sky.prefab", BeachDir + "/FBX/Manuka_beach_maid.fbx", "沙滩女仆", false,
                    (r, m) => r.name.Contains("Apron") || r.name.Contains("Socks") || r.name.Contains("Shoes") ? m2 : m1, BeachOff);
            }
            AssetDatabase.SaveAssets();
        }

        [MenuItem("Kiko/M2 换回 VRoid 希狐")]
        public static void UseVroid()
        {
            EditorPrefs.SetBool("kiko.localModel", false);
            EditorSceneManager.OpenScene(ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            Debug.Log("[Kiko] 现在用 VRoid 希狐：" + ScenePath);
        }

        [MenuItem("Kiko/3 生成场景")]
        public static void CreateScene() => CreateSceneFor(VrmPath, ScenePath);

        static void CreateSceneFor(string VrmPath, string ScenePath)
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
                scenes = new[] { ActiveScene },
                locationPathName = BuildPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.CompressWithLz4HC, // 贴图还没压成 BC7 前，先靠 LZ4HC 把包压小
            });
            var mb = r.summary.totalSize / 1048576.0;
            Debug.Log($"[Kiko] ④ 打包（{ActiveScene}）{r.summary.result}：{BuildPath}，{mb:F1} MB，{r.summary.totalTime.TotalSeconds:F0} 秒，错误 {r.summary.totalErrors}");
            if (Application.isBatchMode && r.summary.result != BuildResult.Succeeded) EditorApplication.Exit(1);
        }

        // ---------------- ⑤ 效果图 ----------------
        [MenuItem("Kiko/5 拍效果图（先进 Play 模式）")]
        public static void Shots()
        {
            var pet = UnityEngine.Object.FindAnyObjectByType<KikoPet>();
            if (!Application.isPlaying || !pet) { Debug.LogWarning("[Kiko] 先点 Play，等她站好再点这个"); return; }
            pet.StartShots();
        }

        // ---------------- 命令行入口 ----------------
        public static void Step1() => SetupProject();

        // GitHub Actions（game-ci/unity-builder 的 buildMethod）：材质、场景都已经提交在仓库里，只要设好项目再打包
        public static void Ci()
        {
            SetupProject();
            AssetDatabase.Refresh();
            Build();
        }

        public static void Step2()
        {
            ConvertMaterials();
            CreateScene();
            Build();
        }
    }
}
