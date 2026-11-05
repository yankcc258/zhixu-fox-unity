// 希狐桌宠（Unity 版）阶段 1：透明窗口里站着、看鼠标、眨眼、呼吸；能拖、松手掉到任务栏上、戳一下会缩。
// 行为照搬 Tauri 版 pet.js / vrm.js 的数值，方便两边对比。
// 退出：按住 Shift 右键点她。
using System.Collections.Generic;
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
        public int baseWidth = 340;
        public int baseHeight = 440;
        [Header("相机")]
        public float fov = 20f;
        [Tooltip("掉落加速度（像素/秒²，和 Tauri 版一样）")]
        public float gravity = 2600f;

        Vrm10Instance vrm;
        Camera cam;
        UniWindowController uwc;
        Transform lookTarget;
        readonly Dictionary<HumanBodyBones, Transform> rig = new();
        readonly Dictionary<HumanBodyBones, Quaternion> restWorld = new(), normWorld = new();
        // 规范化骨骼的父子关系（中间没控制的骨骼，比如 upperChest、肩膀，规范化旋转都是单位，跟着父骨骼走）
        static readonly Dictionary<HumanBodyBones, HumanBodyBones> Parent = new()
        {
            { HumanBodyBones.Spine, HumanBodyBones.Hips }, { HumanBodyBones.Chest, HumanBodyBones.Spine },
            { HumanBodyBones.Neck, HumanBodyBones.Chest }, { HumanBodyBones.Head, HumanBodyBones.Neck },
            { HumanBodyBones.LeftUpperArm, HumanBodyBones.Chest }, { HumanBodyBones.RightUpperArm, HumanBodyBones.Chest },
            { HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftUpperArm }, { HumanBodyBones.RightLowerArm, HumanBodyBones.RightUpperArm },
            { HumanBodyBones.LeftHand, HumanBodyBones.LeftLowerArm }, { HumanBodyBones.RightHand, HumanBodyBones.RightLowerArm },
            { HumanBodyBones.LeftUpperLeg, HumanBodyBones.Hips }, { HumanBodyBones.RightUpperLeg, HumanBodyBones.Hips },
            { HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftUpperLeg }, { HumanBodyBones.RightLowerLeg, HumanBodyBones.RightUpperLeg },
        };
        readonly Dictionary<HumanBodyBones, Vector3> cur = new(), vel = new();
        Dictionary<HumanBodyBones, Vector3> target = KikoPose.Stand;

        enum State { Idle, Pressed, Drag, Fall, Land }
        State state = State.Idle;
        Vector2Int pressCursor, pressWin;
        float fallY, fallV, stateT, flinchUntil, blinkNext = 2f, blinkT = -1f;
        float scale = 1f;
        string logPath;
        float nextStat;

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
            vrm.LookAtTargetType = VRM10ObjectLookAt.LookAtTargetTypes.SpecifiedTransform;
            lookTarget = new GameObject("LookTarget").transform;
            vrm.LookAtTarget = lookTarget;
        }

        void Start()
        {
            uwc.isTransparent = true;
            uwc.isTopmost = true;
            uwc.isHitTestEnabled = true;
            uwc.hitTestType = UniWindowController.HitTestType.Opacity;

            // 窗口大小：按工作区高度放大（1080p ≈ 1，2K ≈ 1.35），和 Tauri 版 figScale 一样
            var wa = Win32.WorkArea;
            scale = Mathf.Clamp(Mathf.Round(wa.height / 1040f * 20f) / 20f, 1f, 1.6f);
            int w = Mathf.RoundToInt(baseWidth * scale), h = Mathf.RoundToInt(baseHeight * scale);
#if !UNITY_EDITOR
            Screen.SetResolution(w, h, FullScreenMode.Windowed);
            Win32.MoveWindow(new Vector2Int(wa.xMax - w - 16, wa.yMax - h));
#endif
            Log($"workArea {wa} scale {scale} window {w}x{h}");

            if (!vrm) return;
            // 场景里摆好的预制体没有 ControlRig，自己做「规范化骨骼」：记下 T-pose 时每根骨头的世界旋转，
            // 之后 实际世界旋转 = 规范化世界旋转 × 静止世界旋转
            foreach (var b in KikoPose.Bones)
            {
                if (vrm.TryGetBoneTransform(b, out var t) && t)
                {
                    rig[b] = t;
                    restWorld[b] = t.rotation;
                }
                cur[b] = KikoPose.Stand[b];
                vel[b] = Vector3.zero;
            }
            FrameCamera();
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
            if (!vrm) return;
            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            stateT += dt;
            UpdateInput(dt);
            UpdateLook();
            UpdatePose(dt);
            UpdateFace(dt);
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
            target = s == State.Drag || s == State.Fall ? KikoPose.Dangle : s == State.Land ? KikoPose.Land : KikoPose.Stand;
        }

        void Poke()
        {
            flinchUntil = Time.time + 0.6f;
            target = KikoPose.Flinch;
            Invoke(nameof(BackToStand), 0.35f);
        }
        void BackToStand() { if (state == State.Idle) target = KikoPose.Stand; }

        // ---------- 看鼠标 ----------
        Vector2 look; // -1..1，相对窗口中心
        void UpdateLook()
        {
            var win = Win32.WindowRect;
            var c = Win32.Cursor;
            // 头在画面上方 ~15% 处；鼠标相对头的位置，离开窗口越远越饱和
            var headPx = new Vector2(win.x + win.width * 0.5f, win.y + win.height * 0.15f);
            var d = new Vector2(c.x - headPx.x, c.y - headPx.y) / (win.height * 0.8f);
            var want = new Vector2(Mathf.Clamp(d.x, -1, 1), Mathf.Clamp(d.y, -1, 1));
            look = Vector2.Lerp(look, want, 1 - Mathf.Exp(-Time.deltaTime * 6));
            // 眼睛：看相机前方的一个点（屏幕左右 = 世界 -x/+x，因为相机转了 180°）
            var head = rig[HumanBodyBones.Head].position;
            lookTarget.position = head + new Vector3(-look.x * 0.6f, -look.y * 0.45f, 1.2f);
        }

        // ---------- 姿势（弹簧跟随，和 vrm.js 一样 ST 200 / 阻尼 0.92）----------
        void UpdatePose(float dt)
        {
            float now = Time.time;
            float br = Mathf.Sin(now * 1.6f), ws = Mathf.Sin(now * 0.45f);
            const float ST = 200f;
            float DM = 2f * Mathf.Sqrt(ST) * 0.92f;
            bool idle = state == State.Idle && now > flinchUntil;
            foreach (var b in KikoPose.Bones)
            {
                if (!rig.TryGetValue(b, out var t)) continue;
                var goal = target[b];
                if (idle) goal += Idle(b, br, ws, now);
                goal += Look(b);
                var v = vel[b];
                v += ((goal - cur[b]) * ST - v * DM) * dt;
                vel[b] = v;
                cur[b] += v * dt;
                var wn = (Parent.TryGetValue(b, out var p) && normWorld.TryGetValue(p, out var pw) ? pw : Quaternion.identity) * KikoPose.ToUnity(cur[b]);
                normWorld[b] = wn;
                t.rotation = wn * restWorld[b];
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

        // ---------- 表情：眨眼、被戳吓一跳、被拎着委屈 ----------
        void UpdateFace(float dt)
        {
            var ex = vrm.Runtime.Expression;
            float now = Time.time;
            if (now > blinkNext && blinkT < 0) blinkT = 0;
            float blink = 0;
            if (blinkT >= 0)
            {
                blinkT += dt;
                blink = Mathf.Sin(Mathf.Clamp01(blinkT / 0.16f) * Mathf.PI);
                if (blinkT > 0.16f) { blinkT = -1; blinkNext = now + Random.Range(2f, 6f); }
            }
            bool dangling = state == State.Drag || state == State.Fall;
            ex.SetWeight(ExpressionKey.Blink, blink);
            ex.SetWeight(ExpressionKey.Surprised, Mathf.MoveTowards(ex.GetWeight(ExpressionKey.Surprised), now < flinchUntil ? 0.8f : 0, dt * 6));
            ex.SetWeight(ExpressionKey.Sad, Mathf.MoveTowards(ex.GetWeight(ExpressionKey.Sad), dangling ? 0.6f : 0, dt * 4));
            ex.SetWeight(ExpressionKey.Relaxed, Mathf.MoveTowards(ex.GetWeight(ExpressionKey.Relaxed), dangling || now < flinchUntil ? 0 : 0.25f, dt * 3));
        }

        // ---------- 日志：判 Go/No-go 用的内存、帧率 ----------
        void LogStats()
        {
            var p = Process.GetCurrentProcess();
            Log($"stats ws={p.WorkingSet64 / 1048576}MB private={p.PrivateMemorySize64 / 1048576}MB mono={System.GC.GetTotalMemory(false) / 1048576}MB fps={1f / Mathf.Max(Time.smoothDeltaTime, 1e-4f):F0} cpu={p.TotalProcessorTime.TotalSeconds:F1}s");
        }

        void Log(string s)
        {
            Debug.Log("[Kiko] " + s);
            try { File.AppendAllText(logPath, System.DateTime.Now.ToString("MM-dd HH:mm:ss ") + s + "\n"); } catch { }
        }
    }
}
