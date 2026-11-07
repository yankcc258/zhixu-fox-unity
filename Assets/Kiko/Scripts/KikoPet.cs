// 希狐桌宠（Unity 版）阶段 1：透明窗口里站着、看鼠标、眨眼、呼吸；能拖、松手掉到任务栏上、戳一下会缩。
// 行为照搬 Tauri 版 pet.js / vrm.js 的数值，方便两边对比。
// 退出：按住 Shift 右键点她。
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.IO;
using Kirurobo;
using UniVRM10;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Kiko
{
    public class KikoPet : MonoBehaviour
    {
        [Header("窗口（1080p 时的大小，2K 屏自动放大）")]
        public int baseWidth = 400; // 尾巴放大后加宽（原 340）
        public int baseHeight = 440;
        [Header("相机")]
        public float fov = 20f;
        [Tooltip("掉落加速度（像素/秒²，和 Tauri 版一样）")]
        public float gravity = 2600f;
        [Header("尾巴静止姿势（弹簧骨以此为准）")]
        [Tooltip("每节往侧面弯的角度（每节累加；负 = 角色左边，正面看在画面右边）")] public float tailSide = -2f; // 11-07 照 A2 三视图：基本垂直，正面只从腿边露一点
        [Tooltip("尾巴根往下压的角度（静止时先往下垂）")] public float tailRootDroop = 10f; // 根部先往后伸再垂：尾巴很粗，离腿太近会把大腿裹进去
        [Tooltip("后面每节往上翘的角度（每节累加；尾尖微微勾一下）")] public float tailLift = 2.5f;
        [Tooltip("尾巴整体放大；粗细在 Tools/shape_tail.py 里按这个倍数换算，改了要一起改")] public float tailScale = 1.25f;
        [Header("尾巴物理（主人：要朝下、随重力晃来晃去）")]
        [Tooltip("弹簧硬度：越小越软、越容易被重力拉下去")] public float tailStiff = 0.8f;
        [Tooltip("重力")] public float tailGravity = 0.4f;
        [Tooltip("阻尼：越小晃得越久")] public float tailDrag = 0.25f;
        [Tooltip("待机时左右慢慢摆的幅度")] public float tailWag = 0.35f;
        [Tooltip("重力往后偏多少（0 = 直直往下，会贴到腿上）")] public float tailBack = 0.3f;
        [Tooltip("窗口被拖动时尾巴甩的强度")] public float tailInertia = 1f;
        [Tooltip("后裙让开尾巴：尾巴前几节挂胶囊碰撞体，半径 = 尾巴实际粗细 × 这个（0 = 关）")] public float tailSkirtPush = 1f;
        [Tooltip("尾巴碰撞体再加上毛的长度（米），免得毛从裙子里扎出来")] public float tailFurMargin = 0.025f;

        Vrm10Instance vrm;
        Camera cam;
        UniWindowController uwc;
        Transform lookTarget;
        readonly Dictionary<HumanBodyBones, Transform> rig = new();
        readonly Dictionary<HumanBodyBones, Quaternion> restWorld = new(), normWorld = new();
        // 规范化骨骼：模型里所有人形骨骼，按层级从上到下排好；每根记下它上面最近的人形骨骼（中间没控制的骨骼，
        // 比如 upperChest、肩膀，规范化旋转是单位，跟着父骨骼走）
        readonly List<HumanBodyBones> rigOrder = new();
        readonly Dictionary<HumanBodyBones, HumanBodyBones> humanParent = new();
        // 要写旋转的骨骼：关键帧/弹簧的 15 根 + 动捕里出现的（手指、肩膀、脚、upperChest…）
        readonly List<HumanBodyBones> driven = new();
        readonly HashSet<HumanBodyBones> springSet = new(KikoPose.Bones);
        readonly Dictionary<HumanBodyBones, Vector3> cur = new(), vel = new(), outPose = new();
        readonly Dictionary<HumanBodyBones, Vector3> clipPose = new(), motionPose = new();

        // ---------- 真人动捕（Resources/kiko_motions.json，Tools/bake_vrma.py 从 .vrma 烘出来）----------
        Dictionary<string, KikoMotion> motions = new();
        KikoMotion motion;
        float motionT, motionW, motionHips;

        // ---------- 心情 / 动作（数据：Resources/kiko_acts.json，从 Tauri 版 vrm.js 导出）----------
        KikoActs acts;
        string mood = "idle";
        float moodUntil;
        KikoClip action;
        float actStart, idleNext = 6f;
        float hipsBase, hipsNow, hipsV;
        readonly Dictionary<string, float> expNow = new(), expGoal = new();
        readonly Dictionary<string, ExpressionKey> expKey = new();

        enum State { Idle, Pressed, Drag, Fall, Land }
        State state = State.Idle;
        Vector2Int pressCursor, pressWin;
        float fallY, fallV, stateT, flinchUntil, blinkNext = 2f, blinkT = -1f;
        float scale = 1f;
        string logPath;
        float nextStat;
        double lastCpu, lastCpuT;

        void Awake()
        {
            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 0;
            Application.runInBackground = true;
            logPath = Path.Combine(Application.persistentDataPath, "pet.log");
            Log($"start {Application.version} unity {Application.unityVersion} gfx {SystemInfo.graphicsDeviceType}");

            uwc = FindAnyObjectByType<UniWindowController>() ?? gameObject.AddComponent<UniWindowController>();
            cam = Camera.main;
            vrm = FindAnyObjectByType<Vrm10Instance>();
            if (!vrm) { Log("没找到 Vrm10Instance"); return; }
            var json = Resources.Load<TextAsset>("kiko_acts");
            if (json) { acts = KikoActs.Load(json.text); Log($"acts {acts.Clips.Count} exps {acts.Exp.Count}"); }
            // 屑狐狸表情（主人：表情太乖了）：覆盖/新增几种心情的表情，数据在 Resources/kiko_exp_kuzu.json
            var kuzu = Resources.Load<TextAsset>("kiko_exp_kuzu");
            if (acts != null && kuzu)
            {
                var k = JsonUtility.FromJson<KikoActData>(kuzu.text);
                foreach (var e in k.exps)
                {
                    var m = new Dictionary<string, float>();
                    for (int i = 0; i < e.names.Length; i++) m[e.names[i]] = e.values[i];
                    acts.Exp[e.mood] = m;
                }
                Log($"kuzu exps {k.exps.Length}");
            }
            else if (acts == null) Log("没找到 Resources/kiko_acts.json");
            var mj = Resources.Load<TextAsset>("kiko_motions");
            if (mj) { motions = KikoMotionData.Load(mj.text); Log($"motions {string.Join(",", motions.Keys)}"); }
            PoseTail();
            SetupTailPhysics();
            KikoAccessories.Build(vrm, Log);
            vrm.LookAtTargetType = VRM10ObjectLookAt.LookAtTargetTypes.SpecifiedTransform;
            lookTarget = new GameObject("LookTarget").transform;
            vrm.LookAtTarget = lookTarget;
        }

        System.Collections.IEnumerator Start()
        {
            // 窗口大小：按工作区高度放大（1080p ≈ 1，2K ≈ 1.35），和 Tauri 版 figScale 一样
            var wa = Win32.WorkArea;
            scale = Mathf.Clamp(Mathf.Round(wa.height / 1040f * 20f) / 20f, 1f, 1.6f);
            int w = Mathf.RoundToInt(baseWidth * scale), h = Mathf.RoundToInt(baseHeight * scale);
#if !UNITY_EDITOR
            // 先改大小、等两帧再开透明：SetResolution 会让 Unity 重新套窗口样式（标题栏、白底），在透明之后调就把透明冲掉了
            if (Screen.width != w || Screen.height != h) Screen.SetResolution(w, h, FullScreenMode.Windowed);
            yield return null;
            yield return null;
#endif
            uwc.isTransparent = false;
            uwc.isTransparent = true;
            uwc.isTopmost = true;
            uwc.isHitTestEnabled = true;
            uwc.hitTestType = UniWindowController.HitTestType.Opacity;
#if !UNITY_EDITOR
            yield return null;
            Win32.MoveWindow(new Vector2Int(wa.xMax - w - 16, wa.yMax - h));
#endif
            Log($"workArea {wa} scale {scale} window {w}x{h} screen {Screen.width}x{Screen.height} transparent {uwc.isTransparent}");

            if (!vrm) yield break;
            // 场景里摆好的预制体没有 ControlRig，自己做「规范化骨骼」：记下 T-pose 时每根骨头的世界旋转，
            // 之后 实际世界旋转 = 规范化世界旋转 × 静止世界旋转
            for (var b = HumanBodyBones.Hips; b < HumanBodyBones.LastBone; b++)
                if (vrm.TryGetBoneTransform(b, out var t) && t)
                {
                    rig[b] = t;
                    restWorld[b] = t.rotation;
                }
            var byT = rig.ToDictionary(kv => kv.Value, kv => kv.Key);
            int Depth(Transform t) { int d = 0; for (; t; t = t.parent) d++; return d; }
            foreach (var kv in rig)
                for (var p = kv.Value.parent; p; p = p.parent)
                    if (byT.TryGetValue(p, out var pb)) { humanParent[kv.Key] = pb; break; }
            rigOrder.AddRange(rig.Keys.OrderBy(b => Depth(rig[b])));
            var want = new HashSet<HumanBodyBones>(KikoPose.Bones);
            foreach (var m in motions.Values) foreach (var id in m.ids) if ((int)id >= 0) want.Add(id);
            want.ExceptWith(new[] { HumanBodyBones.LeftEye, HumanBodyBones.RightEye, HumanBodyBones.Jaw });
            foreach (var b in rigOrder)
                if (want.Contains(b))
                {
                    driven.Add(b);
                    cur[b] = Base(b);
                    vel[b] = Vector3.zero;
                    outPose[b] = cur[b];
                }
            Log($"rig {rig.Count} bones, driven {driven.Count}");
            if (rig.TryGetValue(HumanBodyBones.Hips, out var hipsT)) hipsBase = hipsT.localPosition.y;
            FrameCamera();
            ready = true;
            Invoke(nameof(LogTail), 3f);
        }

        bool ready;

        // 尾巴：模型里的静止姿势基本是朝正后方垂着，从正面只露一点尾尖（主人：看不见尾巴）。
        // 在弹簧骨初始化之前（Awake）把每节往侧面、往上再弯一点，弹簧骨就把这个当静止姿势，还是会跟着晃
        void PoseTail()
        {
            var chain = vrm.GetComponentsInChildren<Transform>(true)
                .Where(t => t.name.StartsWith("J_Opt_C_FoxTail") && !t.name.Contains("_end"))
                .OrderBy(t => t.name).ToList();
            for (int i = 0; i < chain.Count; i++)
            {
                var b = chain[i];
                if (b.childCount == 0) continue;
                var d = b.GetChild(0).position - b.position;
                if (d.sqrMagnitude < 1e-8f) continue;
                float k = i == 0 ? 1.3f : 1f;
                var nd = Vector3.RotateTowards(d, new Vector3(1, 0, 0) * d.magnitude, tailSide * k * Mathf.Deg2Rad, 0);
                // 根部往下压，后面每节往上勾 → 垂下去再翘起来的 J 字形
                nd = i == 0
                    ? Vector3.RotateTowards(nd, Vector3.down * d.magnitude, tailRootDroop * Mathf.Deg2Rad, 0)
                    : Vector3.RotateTowards(nd, Vector3.up * d.magnitude, tailLift * Mathf.Deg2Rad, 0);
                b.rotation = Quaternion.FromToRotation(d, nd) * b.rotation;
            }
            // 整条尾巴放大：只缩放第一节，后面的骨骼和蒙皮跟着变大（弹簧骨在这之后才初始化，长度按放大后的算）
            if (chain.Count > 0 && Mathf.Abs(tailScale - 1f) > 0.01f) chain[0].localScale *= tailScale;
            Log($"tail posed: {chain.Count} bones side {tailSide} droop {tailRootDroop} lift {tailLift} scale {tailScale}");
        }

        // ---------- 领口金铃：始终往下垂，跟身体晃的时候慢半拍 ----------
        Quaternion bellRot = Quaternion.identity;
        bool bellInit;
        void UpdateBell(float dt)
        {
            var p = KikoAccessories.BellPivot;
            if (!p || !p.parent) return;
            var pr = p.parent.rotation;
            var target = Quaternion.FromToRotation(pr * Vector3.down, Vector3.down) * pr;
            if (!bellInit) { bellRot = target; bellInit = true; }
            bellRot = Quaternion.Slerp(bellRot, target, 1 - Mathf.Exp(-dt * 9f));
            p.rotation = bellRot;
        }

        // ---------- 尾巴物理：软一点、重一点，靠重力垂下来；待机慢慢摆，窗口被拖动时往反方向甩 ----------
        readonly List<VRM10SpringBoneJoint> tailJoints = new();
        Vector2Int lastWin;
        Vector2 winVel;
        void SetupTailPhysics()
        {
            foreach (var j in vrm.GetComponentsInChildren<VRM10SpringBoneJoint>(true))
            {
                if (!j.name.StartsWith("J_Opt_C_FoxTail")) continue;
                j.m_stiffnessForce = tailStiff;
                j.m_gravityPower = tailGravity;
                j.m_dragForce = tailDrag;
                j.m_gravityDir = Vector3.down;
                tailJoints.Add(j);
            }
            Log($"tail joints {tailJoints.Count} stiff {tailStiff} gravity {tailGravity} drag {tailDrag}");
            SetupTailSkirt();
        }

        // 后裙让开尾巴：尾巴根部几节挂胶囊碰撞体，加进裙子弹簧骨的碰撞组 → 裙子被尾巴顶起来搭在尾巴上，而不是尾巴从布里面穿出来。
        // 在 Awake 里做（Vrm10Instance.Start 才建弹簧骨运行时），所以不用 Reconstruct。
        void SetupTailSkirt()
        {
            if (tailSkirtPush <= 0) return;
            var chain = vrm.GetComponentsInChildren<Transform>(true)
                .Where(t => t.name.StartsWith("J_Opt_C_FoxTail") && !t.name.Contains("_end"))
                .OrderBy(t => t.name).ToList();
            var smr = vrm.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .FirstOrDefault(r => r.sharedMaterials.Any(m => m && m.name.Contains("FoxTail")));
            if (!smr || chain.Count < 2) { Log("tail skirt: 没找到尾巴网格/骨骼"); return; }
            var mesh = new Mesh();
            smr.BakeMesh(mesh, true);
            var tr = smr.transform;
            // 尾巴是 Body 网格里的一个子网格：只取尾巴材质那个子网格用到的顶点（之前把整个身体都算进去了，半径量成 20~40cm，把裙子整圈撑开）
            int sub = System.Array.FindIndex(smr.sharedMaterials, m => m && m.name.Contains("FoxTail"));
            var verts = mesh.vertices;
            var pts = smr.sharedMesh.GetTriangles(sub).Distinct().Select(i => tr.position + tr.rotation * verts[i]).ToArray();
            Destroy(mesh);
            var group = chain[0].gameObject.AddComponent<VRM10SpringBoneColliderGroup>();
            group.Name = "KikoTail";
            var radii = new List<string>();
            for (int i = 0; i < Mathf.Min(4, chain.Count - 1); i++)
            {
                Vector3 a = chain[i].position, b = chain[i + 1].position, ab = b - a;
                float l2 = ab.sqrMagnitude;
                if (l2 < 1e-8f) continue;
                var ds = new List<float>();
                foreach (var p in pts)
                {
                    float t = Vector3.Dot(p - a, ab) / l2;
                    if (t < 0 || t > 1) continue;
                    ds.Add(Vector3.Distance(p, a + ab * t));
                }
                if (ds.Count < 8) continue;
                ds.Sort();
                float r = ds[(int)(ds.Count * 0.6f)] * tailSkirtPush + tailFurMargin;
                var go = new GameObject("KikoTailCollider" + i);
                go.transform.SetParent(chain[i], false);
                var c = go.AddComponent<VRM10SpringBoneCollider>();
                c.ColliderType = VRM10SpringBoneColliderTypes.Capsule;
                c.Offset = Vector3.zero;
                c.Tail = go.transform.InverseTransformPoint(b);
                var ls = go.transform.lossyScale;
                c.Radius = r / Mathf.Max(ls.x, Mathf.Max(ls.y, ls.z));
                group.Colliders.Add(c);
                radii.Add($"{r * 100:F1}cm");
            }
            vrm.SpringBone.ColliderGroups.Add(group);
            int ns = 0;
            foreach (var sp in vrm.SpringBone.Springs)
                if (sp.Joints.Count > 0 && sp.Joints[0] && sp.Joints[0].name.Contains("Skirt")) { sp.ColliderGroups.Add(group); ns++; }
            // 尾巴本身也躲开大腿（用模型自带的大腿碰撞组），关节半径按尾巴粗细的一半
            foreach (var sp in vrm.SpringBone.Springs)
                if (sp.Joints.Count > 0 && sp.Joints[0] && sp.Joints[0].name.StartsWith("J_Opt_C_FoxTail"))
                {
                    foreach (var g in vrm.SpringBone.ColliderGroups)
                        if (g && ((g.Name ?? "").Contains("UpperLeg") || g.gameObject.name.Contains("UpperLeg")) && !sp.ColliderGroups.Contains(g)) sp.ColliderGroups.Add(g);
                    foreach (var jt in sp.Joints) if (jt) jt.m_jointRadius = Mathf.Max(jt.m_jointRadius, 0.035f);
                }
            Log($"tail skirt: {group.Colliders.Count} capsules r [{string.Join(", ", radii)}] → {ns} skirt springs");
        }

        void UpdateTail(float dt)
        {
            if (tailJoints.Count == 0 || dt <= 0) return;
            var w = Win32.WindowRect.position;
            if (lastWin == default) lastWin = w;
            var v = (Vector2)(w - lastWin) / dt;
            lastWin = w;
            winVel = Vector2.Lerp(winVel, v, 1 - Mathf.Exp(-dt * 8));
            float t = Time.time;
            // 屏幕向右 = 世界 -x（相机转了 180°）；窗口往右移 → 尾巴往左甩 = 世界 +x。窗口往下掉（y 变大）→ 尾巴往上飘
            float sway = tailWag * (Mathf.Sin(t * 1.15f) * 0.7f + Mathf.Sin(t * 0.41f + 1.3f) * 0.3f);
            float ix = Mathf.Clamp(winVel.x / 1500f, -1.5f, 1.5f) * tailInertia;
            float iy = Mathf.Clamp(winVel.y / 1800f, -0.9f, 0.9f) * tailInertia;
            var dir = new Vector3(sway + ix, -1f + iy, -tailBack).normalized; // z 往后：垂在腿后面，不挡吊带袜
            var sb = vrm.Runtime.SpringBone;
            foreach (var j in tailJoints)
            {
                j.m_gravityDir = dir;
                sb.SetJointLevel(j.transform, j.Blittable);
            }
        }

        // 排查「看不见尾巴」：尾巴网格在不在、用的什么着色器、尾尖在哪
        void LogTail()
        {
            foreach (var r in vrm.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                    if (m && m.name.Contains("FoxTail"))
                        Log($"tail renderer {r.name} enabled={r.enabled} active={r.gameObject.activeInHierarchy} visible={r.isVisible} bounds={r.bounds.center:F3}/{r.bounds.size:F3} shader={m.shader.name} queue={m.renderQueue} cutoff={(m.HasProperty("_Cutoff") ? m.GetFloat("_Cutoff") : -1):F2}");
            foreach (var t in vrm.GetComponentsInChildren<Transform>(true))
                if (t.name.StartsWith("J_Opt_C_FoxTail")) Log($"tail bone {t.name} pos={t.position:F3}");
            rig.TryGetValue(HumanBodyBones.Hips, out var hips);
            if (hips) Log($"hips pos={hips.position:F3} cam={cam.transform.position:F3}");
        }

        void FrameCamera()
        {
            // 头顶到脚底整个人放进画面（上下留 3%），和 three.js 版 frameCamera 一样
            vrm.TryGetBoneTransform(HumanBodyBones.Head, out var head);
            float top = head.position.y + 0.26f, bottom = 0f;
            float hh = (top - bottom) * 1.06f;
            float dist = hh / 2f / Mathf.Tan(fov / 2f * Mathf.Deg2Rad);
            cam.fieldOfView = fov;
            cam.transform.SetPositionAndRotation(new Vector3(0, (top + bottom) / 2f, dist), Quaternion.Euler(0, 180, 0));
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
            cam.allowHDR = false;
        }

        void Update()
        {
            if (!vrm || !ready) return;
            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            stateT += dt;
            if (moodUntil > 0 && Time.time > moodUntil) { moodUntil = 0; SetMood("idle", 0); }
            DebugKeys();
            UpdateInput(dt);
            IdleFidget();
            UpdateLook();
            UpdatePose(dt);
            UpdateFace(dt);
            UpdateTail(dt);
            UpdateBell(dt);
            // 省电：没人碰她、也没在掉落时 30 帧，鼠标在身上/拖着/掉落时 60 帧
            Application.targetFrameRate = state == State.Idle && !over && Time.time > flinchUntil ? 30 : 60;
            if (Time.time > nextStat) { nextStat = Time.time + 30f; LogStats(); }
        }

        // ---------- 拖动 / 掉落 / 戳 ----------
        bool over => !uwc.isClickThrough; // 鼠标在她身上（不透明像素）
        void UpdateInput(float dt)
        {
            var c = Win32.Cursor;
            if (Win32.RightDown && Win32.ShiftDown && over) { Log("quit (shift+right click)"); Application.Quit(); }
            switch (state)
            {
                case State.Idle:
                case State.Land:
                    if (state == State.Land && stateT > 0.6f) Go(State.Idle);
                    if (Win32.LeftDown && over)
                    {
                        pressCursor = c;
                        pressWin = Win32.WindowRect.position;
                        Go(State.Pressed);
                    }
                    break;
                case State.Pressed:
                    if (!Win32.LeftDown) { Poke(); Go(State.Idle); break; }
                    if ((c - pressCursor).sqrMagnitude > 25) Go(State.Drag);
                    break;
                case State.Drag:
                    Win32.MoveWindow(pressWin + (c - pressCursor));
                    if (!Win32.LeftDown) { fallY = Win32.WindowRect.y; fallV = 0; Go(State.Fall); }
                    break;
                case State.Fall:
                {
                    var win = Win32.WindowRect;
                    var wa = Win32.WorkAreaAt(new Vector2Int(win.x + win.width / 2, win.y + win.height / 2));
                    float floor = wa.yMax - win.height;
                    if (fallY >= floor - 1) { Win32.MoveWindow(new Vector2Int(win.x, (int)floor)); Go(State.Land); break; }
                    fallV = Mathf.Min(2200f, fallV + gravity * dt);
                    fallY = Mathf.Min(floor, fallY + fallV * dt);
                    Win32.MoveWindow(new Vector2Int(win.x, Mathf.RoundToInt(fallY)));
                    if (Win32.LeftDown && over) { pressCursor = c; pressWin = win.position; Go(State.Pressed); }
                    break;
                }
            }
        }

        void Go(State s)
        {
            state = s;
            stateT = 0;
            if (s == State.Drag) SetMood("dangle", 0);          // → Hang（循环）
            else if (s == State.Fall) { SetMood("surprised", 0); Act("Fall"); }
            else if (s == State.Land) { Act("Land"); SetMood("idle", 0); }
            else if (s == State.Idle && mood == "dangle") SetMood("idle", 0);
        }

        void Poke()
        {
            flinchUntil = Time.time + 0.6f;
            SetMood("surprised", 1.6f); // → Flinch
        }

        Vector3 Base(HumanBodyBones b) =>
            acts != null && acts.Stand.TryGetValue(b, out var v) ? v : KikoPose.Stand.TryGetValue(b, out var s) ? s : Vector3.zero;

        /// 换心情：表情跟着换；表里有对应动作就做一次（和 Tauri 版 setMood 一样）。sec > 0 时过几秒回 idle
        public void SetMood(string m, float sec)
        {
            if (m == mood && sec <= 0) return;
            bool changed = m != mood;
            mood = m;
            moodUntil = sec > 0 ? Time.time + sec : 0;
            if (changed)
            {
                // 换心情：停掉持续/循环中的动作（拎着、走路、耷拉），再做新心情的动作（vrm.js show()）
                if (action != null && (action.hold || action.loop >= 0)) action = null;
                if (motion != null && motion.loop) StopMotion();
            }
            if (changed && acts != null && acts.MoodAct.TryGetValue(m, out var a)) Act(a);
        }

        /// 播一段动作（vrm.js act()）
        public void Act(string name)
        {
            if (name == null) return;
            // 有动捕的动作优先播动捕（和 Tauri 版一样），没有再用关键帧
            if (motions.TryGetValue(name, out var mo))
            {
                PlayMotion(mo);
                action = null;
                idleNext = Time.time + 4f + Random.Range(0f, 6f);
                return;
            }
            if (acts == null || !acts.Clips.TryGetValue(name, out var c)) return;
            if (motion != null) StopMotion();
            action = c;
            actStart = Time.time;
            idleNext = Time.time + 4f + Random.Range(0f, 6f);
        }

        // 待机小动作：心情是 idle、没在做动作、鼠标没停在她身上时，每 6–15 秒随机一个
        void IdleFidget()
        {
            if (shooting || acts == null || action != null || motion != null || state != State.Idle || mood != "idle" || over || Time.time < idleNext) return;
            if (acts.IdleClips.Length == 0) return;
            Act(acts.IdleClips[Random.Range(0, acts.IdleClips.Length)]);
            idleNext = Time.time + 6f + Random.Range(0f, 9f);
        }

        // ---------- 看鼠标 ----------
        Vector2 look; // -1..1，相对窗口中心
        void UpdateLook()
        {
            var win = Win32.WindowRect;
            var c = Win32.Cursor;
            // 头在画面上方 ~15% 处；鼠标相对头的位置，离开窗口越远越饱和
            var headPx = new Vector2(win.x + win.width * 0.5f, win.y + win.height * 0.15f);
            var d = new Vector2(c.x - headPx.x, c.y - headPx.y) / (win.height * 0.8f);
            var want = shooting ? Vector2.zero : new Vector2(Mathf.Clamp(d.x, -1, 1), Mathf.Clamp(d.y, -1, 1));
            look = Vector2.Lerp(look, want, 1 - Mathf.Exp(-Time.deltaTime * 6));
            // 眼睛：看相机前方的一个点（屏幕左右 = 世界 -x/+x，因为相机转了 180°）
            var head = rig[HumanBodyBones.Head].position;
            lookTarget.position = head + new Vector3(-look.x * 0.6f, -look.y * 0.45f, 1.2f);
        }

        // ---------- 动捕：开始 / 停止（0.25 秒交叉淡入，停的时候从当前姿势接回弹簧，不跳）----------
        void PlayMotion(KikoMotion m)
        {
            TakeOver();
            motion = m;
            motionT = 0;
            motionW = 0;
        }

        void StopMotion()
        {
            if (motion == null) return;
            TakeOver();
            motion = null;
            motionW = 0;
        }

        // 把正在显示的姿势读回弹簧（vrm.js takeOverPose）
        void TakeOver()
        {
            foreach (var b in driven) { cur[b] = outPose[b]; vel[b] = Vector3.zero; }
            hipsNow = hipsOut;
            hipsV = 0;
        }

        // ---------- 姿势（弹簧跟随，和 vrm.js 一样 ST 200 / 阻尼 0.92）----------
        float hipsOut;
        void UpdatePose(float dt)
        {
            float now = Time.time;
            float br = Mathf.Sin(now * 1.6f), ws = Mathf.Sin(now * 0.45f);
            const float ST = 200f;
            float DM = 2f * Mathf.Sqrt(ST) * 0.92f;
            // 动作片段求值；放完了回站姿
            float sink = 0;
            if (action != null && !acts.Eval(action, (now - actStart) * 1000f, clipPose, out sink)) { action = null; sink = 0; }
            // 动捕：放完了（不循环的）就交回弹簧
            if (motion != null)
            {
                motionT += dt * motion.speed;
                if (!motion.loop && motionT >= motion.Duration) StopMotion();
                else
                {
                    float t = motion.loop ? motionT % motion.Duration : motionT;
                    motionHips = motion.Sample(t, motionPose) * hipsBase;
                    motionW = Mathf.Min(1, motionW + dt / 0.25f);
                }
            }
            bool mo = motion != null;
            bool idle = action == null && !mo && state == State.Idle;
            float hipsGoal = mo ? motionHips : sink;
            hipsV += ((hipsGoal - hipsNow) * ST - hipsV * DM) * dt;
            hipsNow += hipsV * dt;
            hipsOut = mo ? Mathf.Lerp(hipsNow, motionHips, motionW) : hipsNow;
            if (rig.TryGetValue(HumanBodyBones.Hips, out var hipsT))
            {
                var lp = hipsT.localPosition;
                lp.y = hipsBase + hipsOut;
                hipsT.localPosition = lp;
            }
            foreach (var b in driven)
            {
                bool spring = springSet.Contains(b);
                Vector3 goal;
                if (mo && motionPose.TryGetValue(b, out var mp)) goal = mp;
                else if (!spring) goal = Vector3.zero;
                else goal = action != null && clipPose.TryGetValue(b, out var cp) ? cp : Base(b);
                if (spring && idle) goal += Idle(b, br, ws, now);
                var v = vel[b];
                v += ((goal - cur[b]) * ST - v * DM) * dt;
                vel[b] = v;
                cur[b] += v * dt;
                // 动捕在播：直接用动捕（淡入期间和弹簧混合），细节不被弹簧抹掉
                var o = mo && motionPose.TryGetValue(b, out var mv) ? Vector3.Lerp(cur[b], mv, motionW) : cur[b];
                outPose[b] = o;
            }
            // 规范化世界旋转：从上往下乘；没驱动的骨骼规范化局部旋转是单位
            foreach (var b in rigOrder)
            {
                var pw = humanParent.TryGetValue(b, out var p) && normWorld.TryGetValue(p, out var q) ? q : Quaternion.identity;
                if (!outPose.TryGetValue(b, out var e)) { normWorld[b] = pw; continue; }
                if (springSet.Contains(b)) e += Look(b) * (mo ? 0.4f : 1f);
                var wn = pw * KikoPose.ToUnity(e);
                normWorld[b] = wn;
                rig[b].rotation = wn * restWorld[b];
            }
        }

        static Vector3 Idle(HumanBodyBones b, float br, float ws, float now) => b switch
        {
            HumanBodyBones.Hips => new Vector3(0, 0, 1.6f * ws),
            HumanBodyBones.Spine => new Vector3(1.2f * br, 0, -1.2f * ws),
            HumanBodyBones.Chest => new Vector3(1.0f * br, 0, -0.6f * ws),
            HumanBodyBones.Head => new Vector3(-0.6f * br, 0, 0.8f * Mathf.Sin(now * 0.7f) + 0.4f * ws),
            HumanBodyBones.LeftUpperArm => new Vector3(0, 0, -1.0f * br),
            HumanBodyBones.RightUpperArm => new Vector3(0, 0, 1.0f * br),
            _ => Vector3.zero,
        };

        // 看鼠标：头 60%、脖子 25%、胸 15%（数值是 three.js 坐标：x 正 = 低头，y 正 = 转向屏幕右边）
        Vector3 Look(HumanBodyBones b) => b switch
        {
            HumanBodyBones.Head => new Vector3(look.y * 16, look.x * 22, -look.x * 4),
            HumanBodyBones.Neck => new Vector3(look.y * 6, look.x * 9, 0),
            HumanBodyBones.Chest => new Vector3(0, look.x * 5, 0),
            _ => Vector3.zero,
        };

        // ---------- 表情：心情表（EXP）+ 动作自带表情，0.25 秒渐变；自动眨眼（闭眼类心情不眨）----------
        static readonly Dictionary<string, ExpressionPreset> Presets = new()
        {
            { "happy", ExpressionPreset.happy }, { "angry", ExpressionPreset.angry }, { "sad", ExpressionPreset.sad },
            { "relaxed", ExpressionPreset.relaxed }, { "surprised", ExpressionPreset.surprised },
            { "aa", ExpressionPreset.aa }, { "ih", ExpressionPreset.ih }, { "ou", ExpressionPreset.ou }, { "ee", ExpressionPreset.ee }, { "oh", ExpressionPreset.oh },
            { "blink", ExpressionPreset.blink }, { "blinkLeft", ExpressionPreset.blinkLeft }, { "blinkRight", ExpressionPreset.blinkRight },
            { "neutral", ExpressionPreset.neutral },
        };
        ExpressionKey Key(string n)
        {
            if (!expKey.TryGetValue(n, out var k))
                expKey[n] = k = Presets.TryGetValue(n, out var p) ? ExpressionKey.CreateFromPreset(p) : ExpressionKey.CreateCustom(n);
            return k;
        }

        void UpdateFace(float dt)
        {
            var ex = vrm.Runtime.Expression;
            float now = Time.time;
            // 目标：心情表 + 动作表情（动作的值覆盖心情的同名项）
            foreach (var k in expNow.Keys.ToList()) expGoal[k] = 0;
            if (acts != null && acts.Exp.TryGetValue(mood, out var e)) foreach (var kv in e) expGoal[kv.Key] = kv.Value;
            if (action?.expNames != null) for (int i = 0; i < action.expNames.Length; i++) expGoal[action.expNames[i]] = action.expValues[i];
            float k25 = 1 - Mathf.Exp(-dt / 0.08f); // ≈ 0.25 秒到位
            foreach (var kv in expGoal)
            {
                expNow.TryGetValue(kv.Key, out var w);
                expNow[kv.Key] = w + (kv.Value - w) * k25;
            }
            // 自动眨眼
            bool eyesShut = acts != null && acts.EyesShut.Contains(mood);
            if (shooting && blinkT < 0) blinkNext = now + 1f; // 拍照时不眨眼
            if (!eyesShut && now > blinkNext && blinkT < 0) blinkT = 0;
            float blink = 0;
            if (blinkT >= 0)
            {
                blinkT += dt;
                blink = Mathf.Sin(Mathf.Clamp01(blinkT / 0.16f) * Mathf.PI);
                if (blinkT > 0.16f) { blinkT = -1; blinkNext = now + Random.Range(2f, 6f); }
            }
            foreach (var kv in expNow)
            {
                float w = kv.Value;
                if (kv.Key == "blink") w = Mathf.Max(w, blink);
                ex.SetWeight(Key(kv.Key), Mathf.Clamp01(w));
            }
            if (!expNow.ContainsKey("blink")) ex.SetWeight(ExpressionKey.Blink, blink);
        }

        // ---------- 编辑器试玩用：数字键换心情，F1 依次播所有动作 ----------
#if UNITY_EDITOR
        int previewIdx = -1;
#endif
        void DebugKeys()
        {
#if UNITY_EDITOR
            string[] moods = { "idle", "happy", "angry", "surprised", "smug", "scheme", "pat", "tease", "pout", "excited" };
            for (int i = 0; i < moods.Length; i++)
                if (Input.GetKeyDown(KeyCode.Alpha0 + i)) { SetMood(moods[i], 4f); Log("debug mood " + moods[i]); }
            if (Input.GetKeyDown(KeyCode.F9) || Input.GetKeyDown(KeyCode.P))
            {
                Log($"debug shots key (shooting={shooting})");
                if (!shooting) StartCoroutine(ShotSet());
            }
            if (Input.GetKeyDown(KeyCode.F1) && acts != null)
            {
                var names = acts.Clips.Keys.Where(n => !n.StartsWith("Peek")).OrderBy(n => n).ToList();
                previewIdx = (previewIdx + 1) % names.Count;
                Act(names[previewIdx]);
                Log("debug act " + names[previewIdx]);
            }
#endif
        }

        // ---------- 效果图（编辑器里按 P 或 F9；F11 会被编辑器吃掉）：转一圈 + 表情特写 + 几个动作，存到 persistentDataPath/shots ----------
        bool shooting;
        /// 菜单 Kiko/5 调用（键盘在远程操作时不一定进得了 Game 视图）
        public void StartShots()
        {
            Log($"shots requested (ready={ready}, shooting={shooting})");
            if (ready && !shooting) StartCoroutine(ShotSet());
        }
#if UNITY_EDITOR
        System.Collections.IEnumerator ShotSet()
        {
            shooting = true;
            var dir = Path.Combine(Application.persistentDataPath, "shots");
            Directory.CreateDirectory(dir);
            Log("shots start → " + dir);
            var camPos = cam.transform.position;
            var camRot = cam.transform.rotation;
            void Calm() { action = null; StopMotion(); }
            SetMood("idle", 0); Calm();
            yield return new WaitForSeconds(1.5f);
            // 转一圈（相机绕她转，光不动）
            foreach (var yaw in new[] { 0f, 35f, 90f, 145f, 180f })
            {
                cam.transform.SetPositionAndRotation(Quaternion.Euler(0, yaw, 0) * camPos, Quaternion.Euler(0, 180 + yaw, 0));
                yield return new WaitForSeconds(0.4f);
                yield return Shot(dir, $"turn_{yaw:000}");
            }
            // 尾巴摆动（正面，3 秒 24 帧，拼成 GIF 用）
            cam.transform.SetPositionAndRotation(camPos, camRot);
            for (int f = 0; f < 24; f++)
            {
                yield return new WaitForSeconds(0.125f);
                yield return Shot(dir, $"sway_{f:00}", 0.5f);
            }
            // 表情特写
            var head = rig[HumanBodyBones.Head];
            foreach (var m in new[] { "idle", "happy", "smug", "surprised", "angry", "pout", "sleepy", "pat", "excited", "smirk", "bleh", "scheme", "tease" })
            {
                cam.transform.SetPositionAndRotation(camPos, camRot);
                SetMood(m, 0); Calm();
                yield return new WaitForSeconds(1.0f);
                var hp = head.position;
                cam.transform.SetPositionAndRotation(new Vector3(hp.x, hp.y + 0.05f, hp.z + 1.5f), Quaternion.Euler(0, 180, 0));
                yield return Shot(dir, "face_" + m);
            }
            // 领口特写（A2 铃铛/细绳）
            cam.transform.SetPositionAndRotation(camPos, camRot);
            SetMood("idle", 0); Calm();
            yield return new WaitForSeconds(0.8f);
            var nk = rig[HumanBodyBones.Neck].position;
            cam.transform.SetPositionAndRotation(new Vector3(nk.x, nk.y - 0.06f, nk.z + 1.0f), Quaternion.Euler(0, 180, 0));
            yield return Shot(dir, "detail_collar");
            cam.transform.SetPositionAndRotation(Quaternion.Euler(0, 35, 0) * new Vector3(nk.x, nk.y - 0.06f, nk.z + 1.0f), Quaternion.Euler(0, 215, 0));
            yield return Shot(dir, "detail_collar35");
            // 背面腰部特写：看后裙和尾巴根、背后蝴蝶结（第二张把头发藏掉）
            var wp = rig[HumanBodyBones.Hips].position + Vector3.up * 0.04f;
            var hairs = vrm.GetComponentsInChildren<SkinnedMeshRenderer>().Where(r => r.name.IndexOf("hair", System.StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            for (int k = 0; k < 2; k++)
            {
                foreach (var r in hairs) r.enabled = k == 0;
                cam.transform.SetPositionAndRotation(new Vector3(wp.x, wp.y, wp.z - 1.1f), Quaternion.identity);
                yield return Shot(dir, k == 0 ? "detail_back" : "detail_back_nohair");
                cam.transform.SetPositionAndRotation(Quaternion.Euler(0, 130, 0) * (new Vector3(0, 0, 1.1f)) + wp, Quaternion.Euler(0, 310, 0));
                yield return Shot(dir, k == 0 ? "detail_back130" : "detail_back130_nohair");
            }
            foreach (var r in hairs) r.enabled = true;
            // 动作（取中间好看的一帧）
            cam.transform.SetPositionAndRotation(camPos, camRot);
            SetMood("idle", 0); Calm();
            yield return new WaitForSeconds(1f);
            foreach (var (n, t) in new[] { ("Cheer", 1.4f), ("Wave", 2.0f), ("IdleC", 3f), ("IdleD", 4.5f), ("IdleE", 3f), ("Shy", 0.9f), ("Stretch", 1.2f), ("Hang", 1.2f) })
            {
                Calm();
                yield return new WaitForSeconds(0.6f);
                Act(n);
                yield return new WaitForSeconds(t);
                yield return Shot(dir, "act_" + n);
            }
            Calm();
            SetMood("idle", 0);
            cam.transform.SetPositionAndRotation(camPos, camRot);
            shooting = false;
            Log("shots → " + dir);
        }

        System.Collections.IEnumerator Shot(string dir, string name, float res = 3f)
        {
            yield return new WaitForEndOfFrame();
            var rt = RenderTexture.GetTemporary(Mathf.RoundToInt(baseWidth * res), Mathf.RoundToInt(baseHeight * res), 24, RenderTextureFormat.ARGB32);
            var old = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = old;
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), tex.EncodeToPNG());
            if (!name.StartsWith("sway")) Log("shot " + name);
            Destroy(tex);
        }
#else
        System.Collections.IEnumerator ShotSet() { yield break; }
#endif

        // ---------- 日志：判 Go/No-go 用的内存、帧率 ----------
        void LogStats()
        {
            var p = Process.GetCurrentProcess();
            var (ws, commit) = Win32.Memory;
            double cpu = p.TotalProcessorTime.TotalSeconds, now = Time.realtimeSinceStartup;
            // CPU 占用 = 这 30 秒里用掉的 CPU 秒数 / 30 秒 / 逻辑核心数
            double pct = lastCpuT > 0 ? (cpu - lastCpu) / (now - lastCpuT) / System.Environment.ProcessorCount * 100 : 0;
            lastCpu = cpu; lastCpuT = now;
            Log($"stats ws={ws}MB commit={commit}MB mono={System.GC.GetTotalMemory(false) / 1048576}MB fps={1f / Mathf.Max(Time.smoothDeltaTime, 1e-4f):F0} cpu={pct:F1}% (全机) gpu={SystemInfo.graphicsDeviceName}");
        }

        void Log(string s)
        {
            Debug.Log("[Kiko] " + s);
            try { File.AppendAllText(logPath, System.DateTime.Now.ToString("MM-dd HH:mm:ss ") + s + "\n"); } catch { }
        }
    }
}
