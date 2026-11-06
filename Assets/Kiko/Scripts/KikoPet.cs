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
        public int baseWidth = 340;
        public int baseHeight = 440;
        [Header("相机")]
        public float fov = 20f;
        [Tooltip("掉落加速度（像素/秒²，和 Tauri 版一样）")]
        public float gravity = 2600f;
        [Header("尾巴静止姿势（弹簧骨以此为准）")]
        [Tooltip("每节往侧面（画面左边）多弯的角度")] public float tailSide = 14f;
        [Tooltip("每节往上翘的角度")] public float tailLift = 9f;

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
        readonly Dictionary<HumanBodyBones, Vector3> clipPose = new();

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
            else Log("没找到 Resources/kiko_acts.json");
            PoseTail();
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
            foreach (var b in KikoPose.Bones)
            {
                if (vrm.TryGetBoneTransform(b, out var t) && t)
                {
                    rig[b] = t;
                    restWorld[b] = t.rotation;
                }
                cur[b] = Base(b);
                vel[b] = Vector3.zero;
            }
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
                nd = Vector3.RotateTowards(nd, Vector3.up * d.magnitude, tailLift * Mathf.Deg2Rad, 0);
                b.rotation = Quaternion.FromToRotation(d, nd) * b.rotation;
            }
            Log($"tail posed: {chain.Count} bones side {tailSide} lift {tailLift}");
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

        Vector3 Base(HumanBodyBones b) => acts != null && acts.Stand.TryGetValue(b, out var v) ? v : KikoPose.Stand[b];

        /// 换心情：表情跟着换；表里有对应动作就做一次（和 Tauri 版 setMood 一样）。sec > 0 时过几秒回 idle
        public void SetMood(string m, float sec)
        {
            if (m == mood && sec <= 0) return;
            bool changed = m != mood;
            mood = m;
            moodUntil = sec > 0 ? Time.time + sec : 0;
            if (changed && acts != null && acts.MoodAct.TryGetValue(m, out var a)) Act(a);
        }

        /// 播一段动作（vrm.js act()）
        public void Act(string name)
        {
            if (acts == null || name == null || !acts.Clips.TryGetValue(name, out var c)) return;
            action = c;
            actStart = Time.time;
            idleNext = Time.time + 4f + Random.Range(0f, 6f);
        }

        // 待机小动作：心情是 idle、没在做动作、鼠标没停在她身上时，每 6–15 秒随机一个
        void IdleFidget()
        {
            if (acts == null || action != null || state != State.Idle || mood != "idle" || over || Time.time < idleNext) return;
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
            // 动作片段求值；放完了回站姿
            float sink = 0;
            if (action != null && !acts.Eval(action, (now - actStart) * 1000f, clipPose, out sink)) { action = null; sink = 0; }
            bool idle = action == null && state == State.Idle;
            hipsV += ((sink - hipsNow) * ST - hipsV * DM) * dt;
            hipsNow += hipsV * dt;
            if (rig.TryGetValue(HumanBodyBones.Hips, out var hipsT))
            {
                var lp = hipsT.localPosition;
                lp.y = hipsBase + hipsNow;
                hipsT.localPosition = lp;
            }
            foreach (var b in KikoPose.Bones)
            {
                if (!rig.TryGetValue(b, out var t)) continue;
                var goal = action != null && clipPose.TryGetValue(b, out var cp) ? cp : Base(b);
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
        int previewIdx = -1;
        void DebugKeys()
        {
#if UNITY_EDITOR
            string[] moods = { "idle", "happy", "angry", "surprised", "smug", "sleepy", "pat", "wave", "pout", "excited" };
            for (int i = 0; i < moods.Length; i++)
                if (Input.GetKeyDown(KeyCode.Alpha0 + i)) { SetMood(moods[i], 4f); Log("debug mood " + moods[i]); }
            if (Input.GetKeyDown(KeyCode.F1) && acts != null)
            {
                var names = acts.Clips.Keys.Where(n => !n.StartsWith("Peek")).OrderBy(n => n).ToList();
                previewIdx = (previewIdx + 1) % names.Count;
                Act(names[previewIdx]);
                Log("debug act " + names[previewIdx]);
            }
#endif
        }

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
