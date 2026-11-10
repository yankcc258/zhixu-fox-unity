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
        class Outfit { public string label; public SkinnedMeshRenderer[] meshes = new SkinnedMeshRenderer[0]; public bool[] on = new bool[0]; public bool keepBase = true, hideUniform; }
        readonly List<Outfit> outfits = new();
        SkinnedMeshRenderer[] baseMeshes = new SkinnedMeshRenderer[0];
        readonly List<SkinnedMeshRenderer> uniform = new();
        int outfitIdx;
        public int OutfitCount => outfits.Count;
        public string OutfitLabel => outfits.Count > 0 ? outfits[outfitIdx].label : "";

        void SetupWardrobe()
        {
            KikoDress.Springs.Clear();
            uniform.AddRange(vrm.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.name.Contains("Manuka_costume") || r.name.Contains("Manuka_underwear_stocking")));
            outfits.Add(new Outfit { label = "原来的制服" });
            var baseLayer = GameObject.Find("KikoBase");
            if (baseLayer && KikoDress.Apply(vrm, baseLayer, Log)) baseMeshes = KikoDress.LastMeshes;
            foreach (var info in FindObjectsByType<KikoOutfitInfo>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).OrderBy(i => i.transform.GetSiblingIndex()))
            {
                var on = info.GetComponentsInChildren<SkinnedMeshRenderer>(true).Select(r => r.enabled && r.gameObject.activeSelf).ToArray();
                if (!KikoDress.Apply(vrm, info.gameObject, Log)) continue;
                outfits.Add(new Outfit { label = info.label, meshes = KikoDress.LastMeshes, on = on, keepBase = info.keepBase, hideUniform = info.hideUniform });
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
            dressed = outfitIdx > 0;
            if (save) { PlayerPrefs.SetInt("kiko.outfit", outfitIdx); PlayerPrefs.Save(); }
            Log($"wardrobe: 穿 {outfitIdx}/{outfits.Count} {cur.label}（底层内衣 {(cur.keepBase ? "留" : "收")}）");
        }

        public string NextOutfit() { Wear(outfitIdx + 1); return OutfitLabel; }
    }
}
