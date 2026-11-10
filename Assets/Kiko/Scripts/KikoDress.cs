// 换装：把 BOOTH 买的「对应某个素体」的衣服（自带一副同名骨架的预制体）穿到 VRM 模型上。
//  · 衣服网格的骨骼按名字换成身体的骨骼；身体里没有的骨骼（裙摆、带子…）整串挂到身体对应的父骨骼下面
//  · 衣服骨架是 FBX 原样（骨骼带旋转），VRM 的骨骼是规范化过的（T-pose、旋转清零）→ 先把衣服骨架摆到和身体重合，再重算每根骨骼的 bindpose
//  · 挂过来的裙摆/带子骨骼链做成 VRM 弹簧骨（在 Awake 里做，Vrm10Instance.Start 建运行时的时候就带上了）
//  · 原来的衣服网格藏起来
// 衣服文件（VN3 规约，禁止再配布）只在本机 Assets/ 下，不进仓库；场景里叫 KikoOutfit 的物体就是衣服预制体实例。
using System;
using System.Collections.Generic;
using System.Linq;
using UniVRM10;
using UnityEngine;

namespace Kiko
{
    public static class KikoDress
    {
        /// 穿上以后新做的弹簧链（名字, 关节）——KikoPet 调裙子物理、盖腿用
        public static readonly List<(string name, List<VRM10SpringBoneJoint> joints)> Springs = new();

        public static bool Apply(Vrm10Instance vrm, GameObject outfit, Action<string> log, string[] hide)
        {
            var body = new Dictionary<string, Transform>();
            foreach (var t in vrm.GetComponentsInChildren<Transform>(true)) if (!body.ContainsKey(t.name)) body[t.name] = t;
            var smrs = outfit.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (smrs.Length == 0) { log("dress: 衣服里没有网格"); return false; }
            outfit.transform.SetPositionAndRotation(vrm.transform.position, vrm.transform.rotation);

            // 1) 衣服骨架摆到和身体重合：从上往下，位置对齐，再把「指向同名子骨骼」的方向转成一样
            var obones = outfit.GetComponentsInChildren<Transform>(true).Where(t => !t.GetComponent<Renderer>()).ToList();
            int aligned = 0;
            foreach (var ob in obones) // GetComponentsInChildren 是先父后子
            {
                if (!body.TryGetValue(ob.name, out var bb) || ob == outfit.transform) continue;
                ob.position = bb.position;
                Transform oc = null, bc = null;
                for (int i = 0; i < ob.childCount && !oc; i++)
                {
                    var c = ob.GetChild(i);
                    if (body.TryGetValue(c.name, out var b2) && b2.parent == bb && (c.position - ob.position).sqrMagnitude > 1e-6f) { oc = c; bc = b2; }
                }
                if (oc)
                {
                    var q = Quaternion.FromToRotation(oc.position - ob.position, bc.position - bb.position);
                    q.ToAngleAxis(out float ang, out _);
                    if (ang > 0.5f) { ob.rotation = q * ob.rotation; aligned++; }
                }
            }

            // 2) 每个网格：重算 bindpose，骨骼换成身体的；身体没有的骨骼先记下来
            var extraRoots = new List<Transform>();
            foreach (var smr in smrs)
            {
                var src = smr.sharedMesh;
                if (!src) continue;
                var bones = smr.bones;
                var binds = src.bindposes;
                var nb = new Transform[bones.Length];
                for (int i = 0; i < bones.Length; i++)
                {
                    var ob = bones[i];
                    if (!ob) continue;
                    if (body.TryGetValue(ob.name, out var bb))
                    {
                        nb[i] = bb;
                        binds[i] = bb.worldToLocalMatrix * ob.localToWorldMatrix * binds[i];
                    }
                    else
                    {
                        nb[i] = ob;
                        var top = ob; // 这串「身体没有的骨骼」最上面那根
                        while (top.parent && !body.ContainsKey(top.parent.name) && top.parent != outfit.transform) top = top.parent;
                        if (!extraRoots.Contains(top)) extraRoots.Add(top);
                    }
                }
                var mesh = UnityEngine.Object.Instantiate(src);
                mesh.name = src.name + "_dressed";
                mesh.bindposes = binds;
                smr.sharedMesh = mesh;
                smr.bones = nb;
                if (smr.rootBone && body.TryGetValue(smr.rootBone.name, out var rb)) smr.rootBone = rb;
                smr.updateWhenOffscreen = true;
            }
            // 3) 多出来的骨骼链挂到身体上（保持现在的世界位置）
            foreach (var top in extraRoots)
            {
                if (top.parent && body.TryGetValue(top.parent.name, out var bp)) top.SetParent(bp, true);
                else log($"dress: {top.name} 的父骨骼 {(top.parent ? top.parent.name : "?")} 身体里没有");
            }
            foreach (var smr in smrs) smr.transform.SetParent(vrm.transform, true);

            // 4) 弹簧骨：每串一条链（一路取第一个子骨骼）；腿和髋的碰撞体都加上
            var groups = vrm.SpringBone.ColliderGroups.Where(g => g && (g.name.Contains("Leg") || g.name.Contains("Hips") || (g.Name ?? "").Contains("Leg"))).ToList();
            foreach (var top in extraRoots)
            {
                if (top.childCount == 0 || top.GetComponent<Renderer>()) continue;
                var joints = new List<VRM10SpringBoneJoint>();
                for (var t = top; t; t = t.childCount > 0 ? t.GetChild(0) : null)
                {
                    var j = t.gameObject.AddComponent<VRM10SpringBoneJoint>();
                    // 裙摆：衬裙撑着的蓬蓬裙 → 硬一点、重力小，站着保持原来的形状，动起来才晃；带子、挂件软一点
                    bool skirt = top.name.Contains("Skirt");
                    j.m_stiffnessForce = skirt ? 1.6f : 0.6f; j.m_gravityPower = skirt ? 0.08f : 0.2f; j.m_gravityDir = Vector3.down;
                    j.m_dragForce = skirt ? 0.6f : 0.5f; j.m_jointRadius = 0.02f;
                    joints.Add(j);
                }
                if (joints.Count < 2) continue;
                var sp = new Vrm10InstanceSpringBone.Spring("Kiko_" + top.name) { Joints = joints };
                if (top.name.Contains("Skirt")) sp.ColliderGroups.AddRange(groups);
                vrm.SpringBone.Springs.Add(sp);
                Springs.Add((top.name, joints));
            }
            // 5) 原来的衣服藏起来
            int hidden = 0;
            foreach (var r in vrm.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (!smrs.Contains(r) && hide.Any(h => r.name.Contains(h))) { r.enabled = false; hidden++; }
            outfit.SetActive(false); // 剩下的是衣服自带的那副骨架，用不着了
            log($"dress: {smrs.Length} 个网格穿上了，骨架对齐转了 {aligned} 根，挂过来 {extraRoots.Count} 串骨骼、{Springs.Count} 条弹簧链（碰撞组 {groups.Count}），藏起原衣服 {hidden} 件");
            return true;
        }
    }
}
