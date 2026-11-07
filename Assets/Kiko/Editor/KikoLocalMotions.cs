// 菜单 Kiko/6：把 Assets/LocalMotions 里的人形动作（BOOTH / Mixamo 的 FBX、VRChat 的 .anim）烘成希狐能播的格式。
// 这些素材大多「禁止二次配布」，所以 Assets/LocalMotions 整个不进 git（.gitignore），烘出来的
// Assets/LocalMotions/Resources/kiko_motions_local.json 也只留在本机（打包时照样会打进 exe）。
//
// 做法：把 kiko.vrm 的预制体实例化（T-pose = 静止姿势），用 AnimationClip.SampleAnimation 一帧帧套上动作，
// 读每根人形骨的世界旋转，换成运行时用的「规范化局部旋转」（three.js XYZ 欧拉角，和 bake_vrma.py 同一套）：
//   规范化世界 = 世界 × 静止世界⁻¹；规范化局部 = 父规范化世界⁻¹ × 规范化世界；Unity→three：(x,y,z,w) → (x,-y,-z,w)
// 髋高存「相对静止髋高的比例」，水平位移不要（运行时也锁着）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Kiko.EditorTools
{
    public static class KikoLocalMotions
    {
        const string Dir = "Assets/LocalMotions";
        const string Out = Dir + "/Resources/kiko_motions_local.json";
        const string VrmPath = "Assets/Kiko/Models/kiko.vrm";
        const float Fps = 15f, MaxSec = 30f; // DM 的「坐下/躺下」是从站着开始的 20 多秒长片段，截短了就只剩站着

        [MenuItem("Kiko/6 烘焙本地动作（Assets∕LocalMotions）")]
        public static void Bake()
        {
            if (!AssetDatabase.IsValidFolder(Dir)) { Debug.LogWarning("[Kiko] 没有 " + Dir); return; }
            // 1. FBX 一律按人形导入（默认是 Generic，套不到别的模型上）
            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { Dir }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetImporter.GetAtPath(path) is ModelImporter mi && mi.animationType != ModelImporterAnimationType.Human)
                {
                    mi.animationType = ModelImporterAnimationType.Human;
                    mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                    mi.importAnimation = true;
                    mi.SaveAndReimport();
                    Debug.Log("[Kiko] 人形导入 " + path);
                }
            }

            // 2. 希狐的静止姿势
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(VrmPath);
            var go = UnityEngine.Object.Instantiate(prefab);
            go.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                var anim = go.GetComponent<Animator>() ?? go.AddComponent<Animator>();
                if (!anim.avatar) anim.avatar = AssetDatabase.LoadAllAssetsAtPath(VrmPath).OfType<Avatar>().FirstOrDefault();
                if (!anim.avatar || !anim.avatar.isHuman) { Debug.LogError("[Kiko] kiko.vrm 没有人形 Avatar"); return; }
                go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                var rig = new Dictionary<HumanBodyBones, Transform>();
                for (var b = HumanBodyBones.Hips; b < HumanBodyBones.LastBone; b++)
                {
                    var t = anim.GetBoneTransform(b);
                    if (t) rig[b] = t;
                }
                var byT = rig.ToDictionary(kv => kv.Value, kv => kv.Key);
                var parent = new Dictionary<HumanBodyBones, HumanBodyBones>();
                foreach (var kv in rig)
                    for (var p = kv.Value.parent; p; p = p.parent)
                        if (byT.TryGetValue(p, out var pb)) { parent[kv.Key] = pb; break; }
                int Depth(Transform t) { int d = 0; for (; t; t = t.parent) d++; return d; }
                var order = rig.Keys.OrderBy(b => Depth(rig[b])).ToList();
                // 眼睛交给 LookAt、下巴交给表情
                var bones = order.Where(b => b != HumanBodyBones.LeftEye && b != HumanBodyBones.RightEye && b != HumanBodyBones.Jaw).ToList();
                var rest = rig.ToDictionary(kv => kv.Key, kv => kv.Value.rotation);
                var restLocal = rig.ToDictionary(kv => kv.Key, kv => (kv.Value.localPosition, kv.Value.localRotation));
                float restHips = rig[HumanBodyBones.Hips].position.y;

                // 3. 每个动作
                var list = new List<KikoMotion>();
                var clips = new List<(string name, AnimationClip clip)>();
                foreach (var guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { Dir }))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var inFile = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview__")).ToList();
                    foreach (var c in inFile)
                    {
                        if (!c.isHumanMotion) { Debug.Log($"[Kiko] 跳过（不是人形动作）{path} / {c.name}"); continue; }
                        var file = Clean(Path.GetFileNameWithoutExtension(path));
                        clips.Add((inFile.Count > 1 ? file + "_" + Clean(c.name) : file, c));
                    }
                }
                var used = new HashSet<string>();
                foreach (var (raw, clip) in clips)
                {
                    var name = "L_" + raw;
                    for (int k = 2; used.Contains(name); k++) name = $"L_{raw}_{k}";
                    used.Add(name);
                    float len = Mathf.Min(clip.length, MaxSec);
                    int n = Mathf.Max(2, Mathf.CeilToInt(len * Fps) + 1);
                    var frames = new float[n * bones.Count * 3];
                    var hips = new float[n];
                    var nw = new Dictionary<HumanBodyBones, Quaternion>();
                    for (int f = 0; f < n; f++)
                    {
                        // 每帧先回到静止姿势，免得没被动作控制的骨骼带着上一帧
                        foreach (var kv in restLocal) { rig[kv.Key].localPosition = kv.Value.Item1; rig[kv.Key].localRotation = kv.Value.Item2; }
                        go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                        clip.SampleAnimation(go, n == 1 ? 0 : len * f / (n - 1));
                        // 根运动也算进去（躺下的动作身体朝向可能在根上）；go 采样前在原点、不转
                        nw.Clear();
                        for (int i = 0; i < bones.Count; i++)
                        {
                            var b = bones[i];
                            var w = rig[b].rotation * Quaternion.Inverse(rest[b]);
                            nw[b] = w;
                            var pw = parent.TryGetValue(b, out var p) && nw.TryGetValue(p, out var q) ? q : Quaternion.identity;
                            var e = ToThree(Quaternion.Inverse(pw) * w);
                            frames[(f * bones.Count + i) * 3] = R(e.x);
                            frames[(f * bones.Count + i) * 3 + 1] = R(e.y);
                            frames[(f * bones.Count + i) * 3 + 2] = R(e.z);
                        }
                        hips[f] = R((rig[HumanBodyBones.Hips].position.y - restHips) / restHips * 1000f) / 1000f;
                    }
                    list.Add(new KikoMotion
                    {
                        name = name, fps = Fps, speed = 1, loop = clip.isLooping || !raw.Contains("To"),
                        bones = bones.Select(b => b.ToString()).ToArray(), frames = frames, hips = hips, count = n,
                    });
                    Debug.Log($"[Kiko] 烘焙 {name}：{clip.length:F1} 秒 → {n} 帧，髋高 {hips.Min():F2}~{hips.Max():F2}");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(Out));
                File.WriteAllText(Out, JsonUtility.ToJson(new KikoMotionData { motions = list.ToArray() }));
                AssetDatabase.ImportAsset(Out);
                Debug.Log($"[Kiko] ⑥ 本地动作 {list.Count} 个 → {Out}（{new FileInfo(Out).Length / 1024} KB）。进 Play 后菜单 Kiko/7 拍预览");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        static float R(float v) => Mathf.Round(v * 100f) / 100f;

        static string Clean(string s)
        {
            var chars = s.Select(ch => char.IsLetterOrDigit(ch) && ch < 128 ? ch : '_').ToArray();
            return new string(chars).Trim('_');
        }

        /// Unity 局部旋转 → three.js Euler(x,y,z,'XYZ') 度（KikoPose.ToUnity 的逆）
        public static Vector3 ToThree(Quaternion u)
        {
            var q = new Quaternion(u.x, -u.y, -u.z, u.w);
            var m = Matrix4x4.Rotate(q);
            float m13 = Mathf.Clamp(m.m02, -1, 1);
            float y = Mathf.Asin(m13), x, z;
            if (Mathf.Abs(m13) < 0.9999999f) { x = Mathf.Atan2(-m.m12, m.m22); z = Mathf.Atan2(-m.m01, m.m00); }
            else { x = Mathf.Atan2(m.m21, m.m11); z = 0; }
            return new Vector3(x, y, z) * Mathf.Rad2Deg;
        }

        [MenuItem("Kiko/7 拍本地动作预览（先进 Play 模式）")]
        static void Shots()
        {
            var pet = UnityEngine.Object.FindAnyObjectByType<KikoPet>();
            if (!EditorApplication.isPlaying || !pet) { Debug.LogWarning("[Kiko] 先进 Play 模式"); return; }
            pet.StartLocalShots();
        }
    }
}
