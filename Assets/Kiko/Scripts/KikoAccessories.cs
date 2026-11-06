// A2「朱铃稻荷」的小部件（特蕾莎设计稿 kiko_maid2_A2_turnaround.png / kiko_maid2_A2_vroid.md）：
//   领口：朱红细绳打的小蝴蝶结 + 两条带流苏的绳尾 + 一枚小金铃（会跟着身体晃）
//   左后腰：白色扁蝴蝶结（围裙系带；A2 衣服做出来以前先关着，免得和旧女仆装的大蝴蝶结打架）
// 网格全部在运行时用代码生成（车削/扫掠），材质复制模型里现成的 lilToon 材质改颜色，不依赖外部模型文件。
// 位置在 Awake 的静止姿势下量：烘焙蒙皮网格，找领口正中最靠前的衣服表面，挂到上胸骨骼上跟着动。
using System;
using System.Collections.Generic;
using UniVRM10;
using UnityEngine;

namespace Kiko
{
    public static class KikoAccessories
    {
        public static bool Bell = true;
        public static bool BackBow = false;

        static readonly Color Gold = new(0.96f, 0.76f, 0.30f), GoldShade = new(0.72f, 0.48f, 0.20f);
        static readonly Color Red = new(0.82f, 0.13f, 0.12f), RedShade = new(0.55f, 0.07f, 0.10f);
        static readonly Color White = new(0.97f, 0.97f, 0.99f), WhiteShade = new(0.80f, 0.82f, 0.92f);
        static readonly Color Dark = new(0.22f, 0.13f, 0.07f);

        public static Transform BellPivot; // 铃铛的摆动支点（KikoPet 每帧给它一点惯性摆动）

        public static void Build(Vrm10Instance vrm, Action<string> log)
        {
            Transform Bone(HumanBodyBones b) => vrm.TryGetBoneTransform(b, out var t) ? t : null;
            var neck = Bone(HumanBodyBones.Neck);
            var chest = Bone(HumanBodyBones.UpperChest) ?? Bone(HumanBodyBones.Chest);
            var spine = Bone(HumanBodyBones.Spine);
            var la = Bone(HumanBodyBones.LeftUpperArm); var ra = Bone(HumanBodyBones.RightUpperArm);
            if (!neck || !chest || !spine || !la || !ra) { log("accessories: 缺骨骼"); return; }
            float S = Vector3.Distance(la.position, ra.position); // 肩宽
            var baked = Bake(vrm);
            var src = FindLil(vrm);
            if (!src) { log("accessories: 没找到 lilToon 材质"); return; }
            // 角色自己的左边：Unity 里朝 +Z 站，左手在 -X（UniVRM 导入时把 X 镜像了）
            float leftSign = Mathf.Sign(Bone(HumanBodyBones.LeftHand).position.x - Bone(HumanBodyBones.RightHand).position.x);

            if (Bell)
            {
                float y = neck.position.y - 0.022f;
                var front = Surface(baked, 0, y, 0.006f, +1);
                var knot = new Vector3(0, y, front.z + 0.004f);
                var root = new GameObject("Kiko_A2_Bell").transform;
                root.SetPositionAndRotation(knot, Quaternion.identity);
                root.SetParent(chest, true);
                // 蝴蝶结（朱红细绳）：两个绳圈 + 中间结 + 两条绳尾带流苏
                var red = Mat(src, Red, RedShade, "A2_Red");
                float bw = 0.26f * S, r = 0.0021f;
                var mb = new MeshBuilder();
                for (int s = -1; s <= 1; s += 2)
                {
                    var loop = new List<Vector3>();
                    for (int i = 0; i <= 24; i++)
                    {
                        float a = i / 24f * Mathf.PI * 2;
                        // 一边一个泪滴形的圈，从中心出发再回来
                        float px = s * (0.5f - 0.5f * Mathf.Cos(a)) * bw * 0.5f;
                        float py = Mathf.Sin(a) * bw * 0.17f * (0.6f + 0.4f * (1 - Mathf.Cos(a)) / 2);
                        loop.Add(new Vector3(px, py + Mathf.Abs(px) * 0.25f, -Mathf.Abs(px) * 0.15f));
                    }
                    mb.Sweep(loop, _ => r, 6);
                    // 绳尾：往下往外斜着垂，末端金色包头 + 朱红流苏
                    var tail = new List<Vector3>();
                    for (int i = 0; i <= 8; i++)
                    {
                        float t = i / 8f;
                        tail.Add(new Vector3(s * (0.004f + t * 0.012f), -t * 0.032f, 0.001f - t * t * 0.004f));
                    }
                    mb.Sweep(tail, _ => r * 0.9f, 6);
                    var tipPos = tail[^1];
                    mb.Lathe(tipPos + new Vector3(0, -0.001f, 0), new[] { new Vector2(0.0001f, 0.0f), new Vector2(0.0042f, -0.003f), new Vector2(0.0050f, -0.013f), new Vector2(0.0001f, -0.0135f) }, 10);
                }
                mb.Sphere(Vector3.zero, new Vector3(0.0042f, 0.0040f, 0.0032f), 10, 8); // 中间的结
                Add(root, "cord", mb, red);
                // 绳尾的金色包头
                var gold = Mat(src, Gold, GoldShade, "A2_Gold");
                var caps = new MeshBuilder();
                for (int s = -1; s <= 1; s += 2)
                    caps.Lathe(new Vector3(s * 0.016f, -0.032f + 0.001f, 0.001f - 0.004f), new[] { new Vector2(0.0001f, 0.0015f), new Vector2(0.0026f, 0.001f), new Vector2(0.0030f, -0.0015f), new Vector2(0.0001f, -0.0018f) }, 10);
                Add(root, "caps", caps, gold);
                // 金铃：挂在结下面，支点在结上，单独一个节点好让它晃
                BellPivot = new GameObject("bellPivot").transform;
                BellPivot.SetParent(root, false);
                BellPivot.localPosition = new Vector3(0, -0.003f, 0.002f);
                float d = 0.115f * S; // 铃铛直径
                var bell = new MeshBuilder();
                var prof = new List<Vector2>();
                for (int i = 0; i <= 12; i++)
                {
                    float a = Mathf.Lerp(0.05f, Mathf.PI - 0.35f, i / 12f);
                    prof.Add(new Vector2(Mathf.Sin(a) * d * 0.5f, Mathf.Cos(a) * d * 0.5f));
                }
                prof.Add(new Vector2(d * 0.40f, -d * 0.50f)); // 下沿微微收口
                prof.Add(new Vector2(0.0001f, -d * 0.48f));
                bell.Lathe(new Vector3(0, -0.006f - d * 0.5f, 0), prof.ToArray(), 16);
                // 顶上的小环
                var ring = new List<Vector3>();
                for (int i = 0; i <= 16; i++) { float a = i / 16f * Mathf.PI * 2; ring.Add(new Vector3(0, -0.0035f + Mathf.Cos(a) * 0.0025f, Mathf.Sin(a) * 0.0025f)); }
                bell.Sweep(ring, _ => 0.0007f, 5);
                Add(BellPivot, "bell", bell, gold);
                // 铃铛的开口缝和小圆孔（深色）
                var dark = Mat(src, Dark, Dark * 0.8f, "A2_Dark");
                if (dark.HasProperty("_Cull")) dark.SetFloat("_Cull", 0);
                var slit = new MeshBuilder();
                float cy = -0.006f - d * 0.5f;
                slit.Box(new Vector3(0, cy - d * 0.30f, d * 0.43f), new Vector3(d * 0.06f, d * 0.30f, d * 0.03f));
                slit.Box(new Vector3(0, cy - d * 0.40f, d * 0.38f), new Vector3(d * 0.70f, d * 0.05f, d * 0.03f));
                slit.Sphere(new Vector3(0, cy - d * 0.12f, d * 0.47f), Vector3.one * d * 0.09f, 8, 6);
                Add(BellPivot, "slit", slit, dark);
                log($"accessories: bell at {knot} (S {S:F3}, bell {d * 100:F1}cm)");
            }

            if (BackBow)
            {
                float y = spine.position.y + 0.035f;
                float x = leftSign * 0.055f;
                var back = Surface(baked, x, y, 0.01f, -1);
                var root = new GameObject("Kiko_A2_BackBow").transform;
                root.SetPositionAndRotation(new Vector3(x, y, back.z - 0.006f), Quaternion.Euler(0, 180, 0));
                root.SetParent(spine, true);
                var white = Mat(src, White, WhiteShade, "A2_White");
                var mb = new MeshBuilder();
                float w = 0.075f;
                for (int s = -1; s <= 1; s += 2)
                {
                    // 扁的布圈：用压扁的球近似（可读就行，近看再说）
                    mb.Sphere(new Vector3(s * w * 0.26f, 0.002f, 0), new Vector3(w * 0.25f, w * 0.17f, 0.006f), 14, 8);
                    mb.Sweep(new List<Vector3> { new(s * 0.004f, -0.004f, 0), new(s * 0.012f, -0.03f, -0.002f), new(s * 0.016f, -0.055f, -0.004f) }, t => 0.0045f, 6);
                }
                mb.Sphere(Vector3.zero, new Vector3(0.008f, 0.009f, 0.007f), 10, 8);
                Add(root, "bow", mb, white);
                log($"accessories: back bow at {root.position}");
            }
        }

        // ---------- 量位置：静止姿势下烘焙所有不是头发的蒙皮网格 ----------
        static List<Vector3> Bake(Vrm10Instance vrm)
        {
            var pts = new List<Vector3>();
            var m = new Mesh();
            foreach (var smr in vrm.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (smr.name.IndexOf("hair", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                smr.BakeMesh(m, true);
                var tr = smr.transform;
                foreach (var v in m.vertices) pts.Add(tr.position + tr.rotation * v);
            }
            UnityEngine.Object.Destroy(m);
            return pts;
        }

        /// 在 (x, y) 附近找最靠前（dir=+1）或最靠后（dir=-1）的表面点
        static Vector3 Surface(List<Vector3> pts, float x, float y, float band, int dir)
        {
            var best = new Vector3(x, y, dir > 0 ? -9 : 9);
            foreach (var p in pts)
            {
                if (Mathf.Abs(p.x - x) > band || Mathf.Abs(p.y - y) > band * 2) continue;
                if (dir > 0 ? p.z > best.z : p.z < best.z) best = p;
            }
            return best;
        }

        static Material FindLil(Vrm10Instance vrm)
        {
            Material fallback = null;
            foreach (var r in vrm.GetComponentsInChildren<Renderer>())
                foreach (var m in r.sharedMaterials)
                {
                    if (!m || !m.shader || !m.shader.name.Contains("lilToon")) continue;
                    if (m.name.Contains("Shoes")) return m;
                    fallback ??= m;
                }
            return fallback;
        }

        static Material Mat(Material src, Color c, Color shade, string name)
        {
            var m = new Material(src) { name = name };
            m.SetTexture("_MainTex", Texture2D.whiteTexture);
            if (m.HasProperty("_ShadowColorTex")) m.SetTexture("_ShadowColorTex", null);
            if (m.HasProperty("_BumpMap")) m.SetTexture("_BumpMap", null);
            if (m.HasProperty("_UseBumpMap")) m.SetFloat("_UseBumpMap", 0);
            if (m.HasProperty("_UseEmission")) m.SetFloat("_UseEmission", 0);
            m.SetColor("_Color", c);
            if (m.HasProperty("_ShadowColor")) m.SetColor("_ShadowColor", shade);
            if (m.HasProperty("_OutlineWidth")) m.SetFloat("_OutlineWidth", 0.02f);
            if (m.HasProperty("_OutlineColor")) m.SetColor("_OutlineColor", shade * 0.6f);
            return m;
        }

        static void Add(Transform parent, string name, MeshBuilder mb, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mb.ToMesh(name);
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        // ---------- 简单的网格生成 ----------
        class MeshBuilder
        {
            readonly List<Vector3> v = new();
            readonly List<int> t = new();

            public Mesh ToMesh(string name)
            {
                var m = new Mesh { name = name };
                m.SetVertices(v);
                m.SetTriangles(t, 0);
                m.RecalculateNormals();
                m.RecalculateBounds();
                return m;
            }

            void Grid(int baseIdx, int rows, int cols, bool closeCols)
            {
                for (int i = 0; i < rows - 1; i++)
                    for (int j = 0; j < (closeCols ? cols : cols - 1); j++)
                    {
                        int j1 = (j + 1) % cols;
                        int a = baseIdx + i * cols + j, b = baseIdx + i * cols + j1, c = baseIdx + (i + 1) * cols + j, d = baseIdx + (i + 1) * cols + j1;
                        t.Add(a); t.Add(b); t.Add(c); // Unity 正面是顺时针
                        t.Add(b); t.Add(d); t.Add(c);
                    }
            }

            /// 绕 Y 轴车削：profile 是 (半径, 高度)，从上到下
            public void Lathe(Vector3 c, Vector2[] profile, int seg)
            {
                int b = v.Count;
                foreach (var p in profile)
                    for (int j = 0; j < seg; j++)
                    {
                        float a = j / (float)seg * Mathf.PI * 2;
                        v.Add(c + new Vector3(Mathf.Cos(a) * p.x, p.y, Mathf.Sin(a) * p.x));
                    }
                Grid(b, profile.Length, seg, true);
            }

            public void Sphere(Vector3 c, Vector3 rad, int seg, int rings)
            {
                int b = v.Count;
                for (int i = 0; i <= rings; i++)
                {
                    float phi = Mathf.PI * i / rings;
                    for (int j = 0; j < seg; j++)
                    {
                        float a = j / (float)seg * Mathf.PI * 2;
                        v.Add(c + Vector3.Scale(rad, new Vector3(Mathf.Sin(phi) * Mathf.Cos(a), Mathf.Cos(phi), Mathf.Sin(phi) * Mathf.Sin(a))));
                    }
                }
                Grid(b, rings + 1, seg, true);
            }

            /// 沿折线扫一个圆截面（管子）
            public void Sweep(List<Vector3> path, Func<float, float> radius, int seg)
            {
                int b = v.Count, n = path.Count;
                Vector3 prevN = Vector3.up;
                for (int i = 0; i < n; i++)
                {
                    var tan = (path[Mathf.Min(i + 1, n - 1)] - path[Mathf.Max(i - 1, 0)]).normalized;
                    var nrm = Vector3.Cross(tan, Vector3.Cross(prevN, tan)).normalized;
                    if (nrm.sqrMagnitude < 1e-6f) nrm = Vector3.Cross(tan, Vector3.right).normalized;
                    prevN = nrm;
                    var bin = Vector3.Cross(tan, nrm);
                    float r = radius(i / (float)Mathf.Max(1, n - 1));
                    for (int j = 0; j < seg; j++)
                    {
                        float a = j / (float)seg * Mathf.PI * 2;
                        v.Add(path[i] + (nrm * Mathf.Cos(a) + bin * Mathf.Sin(a)) * r);
                    }
                }
                Grid(b, n, seg, true);
            }

            public void Box(Vector3 c, Vector3 size)
            {
                int b = v.Count;
                var h = size * 0.5f;
                for (int i = 0; i < 8; i++)
                    v.Add(c + new Vector3((i & 1) == 0 ? -h.x : h.x, (i & 2) == 0 ? -h.y : h.y, (i & 4) == 0 ? -h.z : h.z));
                int[] f = { 0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5 };
                foreach (var i in f) t.Add(b + i);
            }
        }
    }
}
