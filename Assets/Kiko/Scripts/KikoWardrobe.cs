// 衣柜：场景里每件带 KikoOutfitInfo 的物体是一套衣服（BOOTH 买的对应衣服的预制体实例），开场全部「穿上」（接到身体骨架上），
// 只显示选中的那一套；右键菜单「换衣服」轮着换，选的哪套记在 PlayerPrefs 里。第 0 套永远是模型原来的制服。
// 底层内衣（KikoBase）：主人 11-10「内衣当成衣服的底层，换衣服别直接套上去」——除了自己就是泳装的那种，其它衣服下面都留着。
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Kiko
{
    public partial class KikoPet
    {
        class Outfit { public string label; public SkinnedMeshRenderer[] meshes = new SkinnedMeshRenderer[0]; public bool[] on = new bool[0]; public bool keepBase = true, hideUniform, skirt; public string[] shapes = new string[0]; }
        readonly List<Outfit> outfits = new();
        SkinnedMeshRenderer[] baseMeshes = new SkinnedMeshRenderer[0];
        // VRM 的身体把腿缩掉了（靠原版白裤袜当腿）→ 穿别的衣服时换成 FBX 里那副完整的身体（KikoBase 里的 Manuka_body）
        SkinnedMeshRenderer vrmBody, fullBody;
        readonly List<SkinnedMeshRenderer> uniform = new();
        int outfitIdx;
        public int OutfitCount => outfits.Count;
        /// 现在穿的这套有没有裙子（买来的短蓬蓬裙不会顺着腿垂下来 → 抱膝坐、团着睡会走光，这些姿势换掉）
        public bool SkirtOn => outfits.Count > 0 && outfits[outfitIdx].skirt;
        public string OutfitLabel => outfits.Count > 0 ? outfits[outfitIdx].label : "";

        void SetupWardrobe()
        {
            KikoDress.Springs.Clear();
            uniform.AddRange(vrm.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.name.Contains("Manuka_costume") || r.name.Contains("Manuka_underwear_stocking")));
            vrmBody = vrm.GetComponentsInChildren<SkinnedMeshRenderer>(true).FirstOrDefault(r => r.name.StartsWith("Manuka_body"));
            outfits.Add(new Outfit { label = "原来的制服" });
            var baseLayer = GameObject.Find("KikoBase");
            if (baseLayer && KikoDress.Apply(vrm, baseLayer, Log))
            {
                fullBody = KikoDress.LastMeshes.FirstOrDefault(r => r.name == "Manuka_body");
                baseMeshes = KikoDress.LastMeshes.Where(r => r != fullBody).ToArray();
            }
            foreach (var info in FindObjectsByType<KikoOutfitInfo>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).OrderBy(i => i.transform.GetSiblingIndex()))
            {
                var on = info.GetComponentsInChildren<SkinnedMeshRenderer>(true).Select(r => r.enabled && r.gameObject.activeSelf).ToArray();
                int before = KikoDress.Springs.Count;
                if (!KikoDress.Apply(vrm, info.gameObject, Log)) continue;
                bool skirt = KikoDress.Springs.Skip(before).Any(sp => sp.name.Contains("Skirt"));
                outfits.Add(new Outfit { label = info.label, meshes = KikoDress.LastMeshes, on = on, keepBase = info.keepBase, hideUniform = info.hideUniform, shapes = info.bodyShapes ?? new string[0], skirt = skirt });
            }
            Wear(PlayerPrefs.GetInt("kiko.outfit", 1), false);
        }

        public void Wear(int i, bool save = true)
        {
            if (outfits.Count == 0) return;
            outfitIdx = ((i % outfits.Count) + outfits.Count) % outfits.Count;
            var cur = outfits[outfitIdx];
            for (int k = 0; k < outfits.Count; k++)
                for (int m = 0; m < outfits[k].meshes.Length; m++)
                    if (outfits[k].meshes[m]) outfits[k].meshes[m].enabled = k == outfitIdx && outfits[k].on[m];
            foreach (var r in uniform) if (r) r.enabled = !cur.hideUniform;
            foreach (var r in baseMeshes) if (r) r.enabled = cur.keepBase;
            bool bare = cur.hideUniform && fullBody && vrmBody;
            if (fullBody) fullBody.enabled = bare;
            if (vrmBody) vrmBody.enabled = !bare;
            // 身体形态键（完整版身体才有）：先把上一套开的关掉，再开这一套的
            foreach (var n in shapesOn) SetBodyShape(n, 0);
            shapesOn.Clear();
            foreach (var e in cur.shapes)
            {
                var kv = e.Split('=');
                if (kv.Length == 2 && float.TryParse(kv[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w) && SetBodyShape(kv[0].Trim(), w)) shapesOn.Add(kv[0].Trim());
            }
            dressed = outfitIdx > 0;
            if (save) { PlayerPrefs.SetInt("kiko.outfit", outfitIdx); PlayerPrefs.Save(); }
            Log($"wardrobe: 穿 {outfitIdx}/{outfits.Count} {cur.label}（底层内衣 {(cur.keepBase ? "留" : "收")}，身体 {(bare ? "完整版" : "原版")}）");
        }

        readonly List<string> shapesOn = new();
        /// 完整版身体（FBX）的形态键；找不到返回 false
        public bool SetBodyShape(string name, float w)
        {
            if (!fullBody || !fullBody.sharedMesh) return false;
            var mesh = fullBody.sharedMesh;
            int idx = mesh.GetBlendShapeIndex(name);
            for (int i = 0; idx < 0 && i < mesh.blendShapeCount; i++) if (mesh.GetBlendShapeName(i).EndsWith("." + name)) idx = i;
            if (idx < 0) { Log("wardrobe: 身体没有形态键 " + name); return false; }
            fullBody.SetBlendShapeWeight(idx, w);
            return true;
        }

        public string NextOutfit() { Wear(outfitIdx + 1); return OutfitLabel; }
    }
}
