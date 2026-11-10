// 两个「换个大小」的模式（主人 11-07 20:43 的设计）：
//  · 大头（趴在任务栏沿上）：人还是这么大，取景拉近到胸口以上，窗口底边 = 任务栏上沿 = 窗台，双臂交叠搭在上面。
//    姿势/小动作沿用 Tauri 版 vrm.js 的 PEEK（acts 里的 Peek* 片段）。
//  · 小只：缩到 0.4 倍，在桌面上沿着别的程序窗口的上边蹦跶、跳上去坐着（腿垂在窗口前面）、或者躺在上面睡觉；
//    窗口被拖走她跟着走，窗口关掉/被盖住她就掉下去。点她一下：先朝镜头看一眼，跳回任务栏，再慢慢变回原来的大小。
// 两个模式都只动相机取景（窗口大小不变，透明的地方本来就点不中），所以切换是连续的缩放动画。
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Kiko
{
    public partial class KikoPet
    {
        // ================= 取景 =================
        // viewB：窗口底边对应的世界高度（米）；viewH：整个窗口高度对应多少米。全身 = (camBottom, camTotal)
        float viewB, viewH, viewB0, viewH0, viewB1, viewH1, viewT = 1, viewDur = 1;
        float restChestY, restHeadY;
        bool ViewBusy => viewT < 1;
        float PxPerM => Screen.height / viewH;

        void SetView(float b, float h, float dur)
        {
            viewB0 = viewB; viewH0 = viewH; viewB1 = b; viewH1 = h;
            viewT = 0; viewDur = Mathf.Max(0.01f, dur);
        }

        void UpdateView(float dt)
        {
            if (viewT < 1)
            {
                viewT = Mathf.Min(1, viewT + dt / viewDur);
                float k = viewT * viewT * (3 - 2 * viewT);
                viewB = Mathf.Lerp(viewB0, viewB1, k);
                viewH = Mathf.Lerp(viewH0, viewH1, k);
            }
            if (!shooting) ApplyView();
        }

        void ApplyView()
        {
            float dist = viewH / 2f / Mathf.Tan(fov / 2f * Mathf.Deg2Rad);
            cam.transform.SetPositionAndRotation(new Vector3(0, viewB + viewH / 2f, dist), Quaternion.Euler(0, 180, 0));
        }

        /// 世界坐标 → 屏幕像素（左上角原点，y 向下）
        Vector2 ScreenOf(Vector3 world)
        {
            var sp = cam.WorldToScreenPoint(world);
            var w = Win32.WindowRect;
            return new Vector2(w.x + sp.x, w.y + Screen.height - sp.y);
        }

        /// 头顶在窗口里的 GUI 纵坐标（气泡放在它上面）
        public float HeadTopGui
        {
            get
            {
                if (!ready || !rig.TryGetValue(HumanBodyBones.Head, out var h)) return bubbleSpace * scale;
                return Screen.height - cam.WorldToScreenPoint(h.position + Vector3.up * (headTopOff - 0.02f)).y;
            }
        }

        bool OnFloor()
        {
            var win = Win32.WindowRect;
            var wa = Win32.WorkAreaAt(new Vector2Int(win.x + win.width / 2, win.y + win.height / 2));
            return Mathf.Abs(win.y - (wa.yMax - win.height)) <= 3;
        }

        // ================= 大头：趴在任务栏沿上 =================
        static readonly Dictionary<HumanBodyBones, Vector3> Peek = new()
        {
            [HumanBodyBones.Spine] = new(10, 0, 0), [HumanBodyBones.Chest] = new(8, 0, 0),
            [HumanBodyBones.Neck] = new(-6, 0, 0), [HumanBodyBones.Head] = new(-8, 0, 4),
            [HumanBodyBones.LeftUpperArm] = new(0, -55, -45), [HumanBodyBones.RightUpperArm] = new(0, 55, 45),
            [HumanBodyBones.LeftLowerArm] = new(0, -95, 0), [HumanBodyBones.RightLowerArm] = new(0, 95, 0),
            [HumanBodyBones.LeftHand] = new(0, -10, 0), [HumanBodyBones.RightHand] = new(0, 10, 0),
        };
        Dictionary<HumanBodyBones, Vector3> standFull;
        bool bust => rest == Rest.Bust;
        public bool InBust => bust;

        // 站姿换成 PEEK（关键帧片段也以站姿为底，所以 Peek* 片段里没写到的骨骼会停在搭着的姿势）
        void UsePeekStand(bool on)
        {
            if (acts == null) return;
            standFull ??= new Dictionary<HumanBodyBones, Vector3>(acts.Stand);
            foreach (var kv in Peek) acts.Stand[kv.Key] = on ? kv.Value : standFull[kv.Key];
        }

        public bool EnterBust(float sec)
        {
            if (rest == Rest.Bust) return true;
            if (rest == Rest.Mini || state != State.Idle) return false;
            if (!OnFloor()) { Log("bust skipped (not on floor)"); return false; }
            action = null;
            StopMotion(0.5f);
            yawGoal = 0;
            rest = Rest.Bust;
            restUntil = Time.time + sec;
            if (mood != "idle") SetMood("idle", 0);
            UsePeekStand(true);
            BustView(restChestY + 0.02f, 0.9f);
            bustMeasureAt = Time.time + 0.8f; // 姿势搭好以后量一下前臂实际在哪，窗台（窗口底边）对齐到前臂下沿
            idleNext = Time.time + 3f;
            Log($"rest: bust {sec:F0}s");
            Brain.Tell("趴在桌面任务栏的边上探着头");
            return true;
        }

        float bustMeasureAt;
        void BustView(float ledge, float dur) => SetView(ledge, (restHeadY + headTopOff + 0.04f - ledge) * WinH / baseHeight * 1.1f, dur);

        void MeasureBust()
        {
            bustMeasureAt = 0;
            float Y(HumanBodyBones b) => rig.TryGetValue(b, out var t) ? t.position.y : restChestY;
            float fl = (Y(HumanBodyBones.LeftLowerArm) + Y(HumanBodyBones.LeftHand)) / 2f, fr = (Y(HumanBodyBones.RightLowerArm) + Y(HumanBodyBones.RightHand)) / 2f;
            float arm = Mathf.Min(fl, fr), ledge = Mathf.Clamp(arm - 0.06f, restChestY - 0.25f, restChestY + 0.05f); // 前臂整条露在窗台上面
            Log($"bust measure chest {restChestY:F3} head {restHeadY:F3} elbowL {Y(HumanBodyBones.LeftLowerArm):F3} handL {Y(HumanBodyBones.LeftHand):F3} elbowR {Y(HumanBodyBones.RightLowerArm):F3} handR {Y(HumanBodyBones.RightHand):F3} → ledge {ledge:F3}");
            BustView(ledge, 0.5f);
        }

        public void ExitBust(float dur = 0.9f)
        {
            if (!bust) return;
            rest = Rest.None;
            action = null;
            UsePeekStand(false);
            SetView(camBottom, camTotal, dur);
            Touch();
            Log("rest: bust end");
        }

        // 趴着时被戳：戳头 / 脸 / 耳朵 / 下面（手臂）反应不一样
        void BustPoke()
        {
            Touch();
            restUntil = Mathf.Max(restUntil, Time.time + 60f);
            var c = Win32.Cursor;
            var head = rig[HumanBodyBones.Head].position;
            var h0 = ScreenOf(head);
            float ppm = Mathf.Abs(ScreenOf(head + Vector3.right * 0.1f).x - h0.x) * 10f;
            var eye = ScreenOf(head + new Vector3(0, 0.07f, 0.08f));
            var chin = ScreenOf(head + new Vector3(0, -0.02f, 0.07f));
            var top = ScreenOf(head + new Vector3(0, 0.22f, 0));
            float dx = c.x - eye.x;
            string part = c.y < eye.y - 0.015f * ppm && c.y > top.y - 0.05f * ppm ? (Mathf.Abs(dx) > 0.075f * ppm ? "ear" : "head")
                : c.y <= chin.y + 0.01f * ppm && Mathf.Abs(dx) < 0.09f * ppm ? "face" : "body";
            switch (part)
            {
                case "ear": Act(dx > 0 ? "PeekEarL" : "PeekEarR"); talk.SayKey("startled", null, 3f); break; // 画面右边 = 她的左边
                case "head": Act(Random.value < 0.5f ? "PeekPat" : "PeekFlinch"); talk.SayKey("pokeHead", null, 3f); break;
                case "face": Act("PeekCheek"); talk.SayKey("pokeCheek", null, 3f); break;
                default: Act("PeekFlinch"); talk.SayKey("pokeBottom", null, 3f); break;
            }
            Log("bust poke " + part);
        }

        // ================= 小只：在桌面窗口上跑来跑去 =================
        const float MiniK = 0.4f;
        enum Mini { Shrink, Idle, Walk, Jump, Sit, Lie, Look, Grow }
        Mini ms;
        float msT, msUntil;
        struct Plat { public IntPtr h; public int x0, x1, y; public string title; public bool icon; }
        static bool IsIcon(IntPtr h) => (long)h < -1; // 桌面图标没有窗口句柄，用负数编号
        int M(Plat p) => p.icon ? Mathf.RoundToInt(6 * scale) : Margin; // 图标很窄，站在中间就行
        List<RectInt> icons = new();
        float nextIconScan;
        readonly List<Plat> plats = new();
        float nextScan;
        IntPtr onH;          // 站在哪个窗口上（Zero = 任务栏）
        RectInt onRect;      // 那个窗口上次的位置（窗口被拖走 → 她跟着挪）
        float surfY;         // 脚下那条边的屏幕 y
        string miniGoal;     // "sit" / "lie" / "wander"
        bool hasGoal, returning, lieAsleep;
        Plat goalPlat;
        int goalX;
        Vector2 jumpFrom, jumpTo;
        float jumpDur, jumpH, walkToX, walkX, miniDrop;
        bool mini => rest == Rest.Mini;
        [NonSerialized] public string DebugPrefer; // 编辑器菜单试：指定去 "icon" / "window"
        public bool InMini => mini;
        public bool MiniAsleep => mini && ms == Mini.Lie && lieAsleep;
        bool Walking => rest == Rest.Walk || (mini && ms == Mini.Walk);
        int Margin => Mathf.RoundToInt(24 * scale);

        // 小只时用的动作：坐窗口边 = 翘腿坐（腿并着、不走光）；躺 = 侧躺 / 趴着 / 团睡；坐地上 = 抱膝/跪坐那几个
        static readonly string[] MiniEdgeSet = { "L_EJ_11_wait_sitliedown_F", "L_EJ_13_wait_sitliedown_F" };
        static readonly string[] MiniLieSet = { "L_EJ_16_wait_sitliedown_F", "L_EJ_17_wait_sitliedown_F", "L_AFK_manmaruCat" };

        int FeetX { get { var w = Win32.WindowRect; return w.x + w.width / 2; } }
        void PlaceFeet(float fx, float fy)
        {
            var w = Win32.WindowRect;
            Win32.MoveWindow(new Vector2Int(Mathf.RoundToInt(fx - w.width / 2f), Mathf.RoundToInt(fy - w.height)));
        }

        public bool EnterMini(string goal)
        {
            if (rest == Rest.Mini) { miniGoal = goal; hasGoal = false; if (ms == Mini.Idle) msUntil = 0; else if (ms is Mini.Sit or Mini.Lie) EndActivity(); return true; }
            if (state != State.Idle) return false;
            if (rest == Rest.Bust) ExitBust(0.3f);
            if (rest == Rest.Sleep) WakeUp(true);
            if (!OnFloor()) { Log("mini skipped (not on floor)"); return false; }
            action = null;
            StopMotion(0.4f);
            yawGoal = 0;
            rest = Rest.Mini;
            ms = Mini.Shrink; msT = 0;
            miniGoal = goal; hasGoal = false; returning = false; lieAsleep = false;
            SetView(camBottom, camTotal / MiniK, 1.0f);
            if (mood != "idle") SetMood("idle", 0);
            ScanPlats();
            StandOn(SurfaceBelow(FeetX, Win32.WindowRect.yMax));
            Log($"mini: enter ({goal}) platforms {plats.Count}: " + string.Join(" | ", plats.Select(p => $"{p.title}[{p.x0}..{p.x1}@{p.y}]")));
            return true;
        }

        /// 点一下（或菜单「变回来」）：朝镜头看一眼 → 跳回任务栏 → 慢慢变大
        public void ComeBack()
        {
            if (!mini || returning) return;
            returning = true;
            hasGoal = false;
            if (ms == Mini.Walk) EndMiniWalk();
            if (motion != null) StopMotion(0.35f);
            action = null;
            yawGoal = 0;
            lieAsleep = false;
            SetMood("surprised", 1.2f);
            ms = Mini.Look; msT = 0;
            Log("mini: come back");
            Brain.Tell("在桌面上被 Yank 点了一下，跑回来变回原来的大小");
        }

        void MiniPoke()
        {
            Touch();
            if (MiniAsleep) talk.OnPokeAsleep();
            ComeBack();
        }

        // ---------- 平台：别的窗口的上沿（没被更上面的窗口挡住的那几段）+ 任务栏 ----------
        void ScanPlats()
        {
            plats.Clear();
            var me = Win32.WindowRect;
            var wa = Win32.WorkAreaAt(new Vector2Int(me.x + me.width / 2, me.yMax - 4));
            int headroom = Mathf.RoundToInt(baseHeight * scale * MiniK * 0.9f), minW = Mathf.RoundToInt(110 * scale);
            var above = new List<RectInt>();
            foreach (var w in Win32.TopWindows())
            {
                var r = w.r;
                int y = r.yMin;
                if (r.xMax > wa.xMin && r.xMin < wa.xMax && y > wa.yMin + headroom && y < wa.yMax - 40)
                {
                    var segs = new List<Vector2Int> { new(Mathf.Max(r.xMin, wa.xMin), Mathf.Min(r.xMax, wa.xMax)) };
                    foreach (var a in above)
                        if (a.yMin <= y - 2 && a.yMax > y - 2) segs = Cut(segs, a.xMin, a.xMax);
                    foreach (var sg in segs)
                        if (sg.y - sg.x >= minW) plats.Add(new Plat { h = w.h, x0 = sg.x, x1 = sg.y, y = y, title = w.title });
                }
                above.Add(r);
            }
            // 桌面图标（主人 11-07：也可以坐在桌面图标上）：在所有窗口下面，被任何窗口挡住的那段都不算；图标位置 2 秒读一次
            if (Time.time > nextIconScan) { nextIconScan = Time.time + 2f; icons = Win32.DesktopIcons(); }
            int iconW = Mathf.RoundToInt(28 * scale);
            for (int i = 0; i < icons.Count; i++)
            {
                var r = icons[i];
                int y = r.yMin + Mathf.RoundToInt(4 * scale); // 图标格子上面有几像素空白
                if (r.xMax <= wa.xMin || r.xMin >= wa.xMax || y <= wa.yMin + headroom || y >= wa.yMax - 20) continue;
                int pad = r.width / 5; // 格子比图标图案宽，两边收一点
                var segs = new List<Vector2Int> { new(r.xMin + pad, r.xMax - pad) };
                foreach (var a in above)
                    if (a.yMin <= y - 2 && a.yMax > y - 2) segs = Cut(segs, a.xMin, a.xMax);
                foreach (var sg in segs)
                    if (sg.y - sg.x >= iconW) plats.Add(new Plat { h = (IntPtr)(-(i + 2)), x0 = sg.x, x1 = sg.y, y = y, title = "图标" + i, icon = true });
            }
            plats.Add(new Plat { h = IntPtr.Zero, x0 = wa.xMin, x1 = wa.xMax, y = wa.yMax, title = "任务栏" });
        }

        static List<Vector2Int> Cut(List<Vector2Int> segs, int a, int b)
        {
            var r = new List<Vector2Int>();
            foreach (var s in segs)
            {
                if (b <= s.x || a >= s.y) { r.Add(s); continue; }
                if (a > s.x) r.Add(new Vector2Int(s.x, a));
                if (b < s.y) r.Add(new Vector2Int(b, s.y));
            }
            return r;
        }

        /// 脚下（fy 往下）最近的一条边
        Plat SurfaceBelow(int fx, int fy)
        {
            Plat best = plats.Last();
            foreach (var p in plats)
                if (p.y >= fy - 3 && fx >= p.x0 + M(p) / 2 && fx <= p.x1 - M(p) / 2 && p.y < best.y) best = p;
            return best;
        }

        void StandOn(Plat p)
        {
            onH = p.h;
            surfY = p.y;
            if (onH != IntPtr.Zero && !IsIcon(onH) && !Win32.RectOf(onH, out onRect)) onH = IntPtr.Zero;
        }

        // 脚下的窗口被拖走 → 跟着走；关掉 / 最小化 / 被别的窗口盖住 → 掉下去
        void FollowPlatform()
        {
            if (onH == IntPtr.Zero || IsIcon(onH) || state != State.Idle || ms == Mini.Jump) return;
            if (!Win32.RectOf(onH, out var r)) { MiniFall("窗口没了"); return; }
            if (r.position != onRect.position)
            {
                var d = r.position - onRect.position;
                var w = Win32.WindowRect;
                Win32.MoveWindow(w.position + d);
                surfY += d.y;
                if (ms == Mini.Walk) { walkToX += d.x; walkX += d.x; }
                onRect = r;
            }
        }

        void MiniFall(string why)
        {
            Log("mini: fall (" + why + ")");
            if (ms == Mini.Walk) EndMiniWalk();
            if (motion != null) StopMotion(0.3f);
            onH = IntPtr.Zero;
            ms = Mini.Idle; msUntil = Time.time + 0.5f;
            hasGoal = false; lieAsleep = false;
            fallY = Win32.WindowRect.y; fallV = 0;
            Go(State.Fall);
        }

        /// 掉落时的「地面」：全身 = 任务栏上沿；小只 = 脚下最近的窗口上沿
        float FallFloor(RectInt win, RectInt wa)
        {
            if (!mini) return wa.yMax - win.height;
            return SurfaceBelow(win.x + win.width / 2, Mathf.RoundToInt(fallY) + win.height).y - win.height;
        }

        void MiniLanded()
        {
            var w = Win32.WindowRect;
            ScanPlats();
            StandOn(SurfaceBelow(w.x + w.width / 2, w.yMax));
            ms = Mini.Idle; msUntil = Time.time + 1.2f;
        }

        void UpdateMini(float dt)
        {
            if (!mini || shooting) return;
            if (Time.time > nextScan)
            {
                nextScan = Time.time + 0.4f;
                ScanPlats();
                // 站着的那段边被别的窗口盖住了（脚下不再是能站的边）→ 掉下去
                if (onH != IntPtr.Zero && state == State.Idle && ms is Mini.Idle or Mini.Walk or Mini.Sit or Mini.Lie
                    && !plats.Any(p => p.h == onH && FeetX >= p.x0 && FeetX <= p.x1)) MiniFall("被盖住了");
            }
            FollowPlatform();
            if (landUntil > 0 && Time.time > landUntil) { landUntil = 0; SetJumpPose(null, 0); }
            if (state != State.Idle) { if (ms == Mini.Jump) { ms = Mini.Idle; SetJumpPose(null, 0); } return; } // 被拖着 / 正在掉：UpdateInput 管
            msT += dt;
            switch (ms)
            {
                case Mini.Shrink:
                    if (!ViewBusy) { ms = Mini.Idle; msUntil = Time.time + 0.5f; }
                    break;
                case Mini.Idle:
                    if (Time.time > msUntil && !returning) Think();
                    break;
                case Mini.Walk:
                {
                    var w = Win32.WindowRect;
                    float step = walkSpeed * dt, dx = walkToX - walkX;
                    bool done = Mathf.Abs(dx) <= step;
                    walkX = done ? walkToX : walkX + Mathf.Sign(dx) * step; // 小数累加，慢慢走也不会被取整吃掉
                    Win32.MoveWindow(new Vector2Int(Mathf.RoundToInt(walkX - w.width / 2f), w.y));
                    if (done) { EndMiniWalk(); ms = Mini.Idle; msUntil = Time.time + 0.25f; }
                    break;
                }
                case Mini.Jump:
                {
                    // 先蹲一下蓄力（窗口不动）→ 蹬出去 → 空中收腿 → 快落地时腿伸直 → 落地蹲一下缓冲
                    if (msT < JumpPre) { SetJumpPose(Crouch, -0.07f); break; }
                    float u = Mathf.Clamp01((msT - JumpPre) / jumpDur);
                    SetJumpPose(u < 0.12f || u > 0.82f ? null : Tuck, 0);
                    float x = Mathf.Lerp(jumpFrom.x, jumpTo.x, u);
                    float y = Mathf.Lerp(jumpFrom.y, jumpTo.y, u) - jumpH * 4f * u * (1 - u);
                    PlaceFeet(x, y);
                    if (u >= 1)
                    {
                        SetJumpPose(Crouch, -0.06f);
                        landUntil = Time.time + 0.22f;
                        StandOn(goalPlat);
                        surfY = jumpTo.y;
                        yawGoal = 0;
                        ms = Mini.Idle; msUntil = Time.time + 0.5f;
                        Log($"mini: landed on {goalPlat.title}");
                        if (returning) StartGrow();
                    }
                    break;
                }
                case Mini.Sit:
                {
                    if (motion == null) { ms = Mini.Idle; msUntil = Time.time + 1f; yawGoal = 0; break; }
                    // 窗口跟着「坐下 / 站起来」那段往下 / 往上挪，坐稳时屁股正好在窗口上沿，腿垂在那个窗口前面
                    float k = SegProgress();
                    var w = Win32.WindowRect;
                    Win32.MoveWindow(new Vector2Int(w.x, Mathf.RoundToInt(surfY - w.height + miniDrop * k)));
                    if (Time.time > msUntil && !exiting) EndActivity();
                    break;
                }
                case Mini.Lie:
                    if (motion == null) { lieAsleep = false; ms = Mini.Idle; msUntil = Time.time + 1f; if (mood == "sleep") SetMood("idle", 0); break; }
                    if (Time.time > msUntil && !exiting) EndActivity();
                    break;
                case Mini.Look:
                    if (msT > 1.0f)
                    {
                        if (onH != IntPtr.Zero)
                        {
                            var floor = plats.Last();
                            goalPlat = floor;
                            StartJump(new Vector2(Mathf.Clamp(FeetX, floor.x0 + Margin, floor.x1 - Margin), floor.y));
                        }
                        else StartGrow();
                    }
                    break;
                case Mini.Grow:
                    if (!ViewBusy)
                    {
                        rest = Rest.None;
                        returning = false;
                        Touch();
                        talk.SayKey("summoned", "happy", 3.5f);
                        Log("mini: back to full size");
                    }
                    break;
            }
        }

        float SegProgress()
        {
            if (motion == null) return 0;
            if (!motion.HasSegments) return Mathf.Clamp01(motionT / 0.8f);
            float tm = MotionTime(), a = motion.loopStart / motion.fps, b = motion.loopEnd / motion.fps;
            float k = exiting ? 1 - Mathf.Clamp01((tm - b) / Mathf.Max(0.1f, motion.Duration - b)) : Mathf.Clamp01(tm / Mathf.Max(0.1f, a));
            return k * k * (3 - 2 * k);
        }

        /// 坐的动作坐稳时，屁股比脚高多少像素（窗口要往下挪这么多，屁股才落在边上）
        float SitDropPx(KikoMotion mo)
        {
            float frac = mo.hips[mo.HasSegments ? mo.loopStart : 0];
            return Mathf.Max(0, (hipsBase * (1 + frac) - 0.08f - camBottom) * PxPerM);
        }

        // 想下一步去哪、干什么
        void Think()
        {
            if (!hasGoal)
            {
                if (string.IsNullOrEmpty(miniGoal)) miniGoal = Night ? "lie" : Random.value < 0.5f ? "sit" : Random.value < 0.6f ? "wander" : "lie";
                var wins = plats.Where(p => p.h != IntPtr.Zero && p.x1 - p.x0 >= (p.icon ? 0 : Margin * 3)).ToList();
                // 图标和窗口都有时，四成去图标上
                var ic = wins.Where(p => p.icon).ToList();
                var wn = wins.Where(p => !p.icon).ToList();
                if (miniGoal == "lie") wins = wn; // 图标太窄，躺不下
                else if (DebugPrefer != null && (DebugPrefer == "icon" ? ic : wn).Count > 0) wins = DebugPrefer == "icon" ? ic : wn;
                else if (ic.Count > 0 && wn.Count > 0) wins = Random.value < 0.4f ? ic : wn;
                // 已经在窗口上了：一半留在这个窗口上，一半换一个
                var here = wins.Where(p => p.h == onH && FeetX >= p.x0 && FeetX <= p.x1).ToList();
                if (here.Count > 0 && (miniGoal != "wander" && Random.value < 0.5f)) goalPlat = here[0];
                else if (wins.Count > 0) goalPlat = wins[Random.Range(0, wins.Count)];
                else goalPlat = plats.Last(); // 桌面上没有窗口：就在任务栏上
                goalX = goalPlat.icon ? (goalPlat.x0 + goalPlat.x1) / 2 : Mathf.RoundToInt(Random.Range(goalPlat.x0 + Margin * 1.5f, goalPlat.x1 - Margin * 1.5f));
                hasGoal = true;
                Log($"mini: goal {miniGoal} → {goalPlat.title} x {goalX}");
            }
            var cur = SurfaceBelow(FeetX, Mathf.RoundToInt(surfY));
            bool sameSurface = goalPlat.h == onH && Mathf.Abs(goalPlat.y - surfY) < 4;
            if (sameSurface)
            {
                if (Mathf.Abs(FeetX - goalX) > 6) { StartMiniWalk(goalX); return; }
                hasGoal = false;
                DoActivity();
                return;
            }
            // 换到别的边：离得够近就直接跳；太远先沿着现在这条边走近一点（一跳最远横着 reach 像素）
            int landX = goalPlat.icon ? goalX : Mathf.Clamp(goalX, goalPlat.x0 + Margin, goalPlat.x1 - Margin);
            int reach = Mathf.RoundToInt(480 * scale);
            int takeoff = Mathf.Clamp(landX + Mathf.Clamp(FeetX - landX, -reach, reach), cur.x0 + M(cur), cur.x1 - M(cur));
            if (Mathf.Abs(FeetX - takeoff) > 6) { StartMiniWalk(takeoff); return; }
            StartJump(new Vector2(landX, goalPlat.y));
        }

        void DoActivity()
        {
            string g = miniGoal;
            miniGoal = null;
            float sec;
            switch (g)
            {
                case "sit":
                {
                    // 窗口 / 图标上 = 坐在边上腿垂下去（多半是壁纸那样并膝、手放膝盖上，偶尔翘腿）；任务栏上 = 坐地上
                    string m;
                    ledgeIK = false;
                    if (onH == IntPtr.Zero) m = PickMotion(SitSet);
                    else
                    {
                        m = Random.value < 0.75f ? PickMotion(MiniLedgeSet) : null;
                        if (m != null) { ledgeIK = true; ledgeTilt = Random.Range(6f, 12f) * (Random.value < 0.5f ? 1 : -1); yawGoal = -Mathf.Sign(ledgeTilt) * 18f; } // 像壁纸那样稍微侧着身子
                        else m = PickMotion(MiniEdgeSet);
                    }
                    if (m == null) goto default;
                    sec = Random.Range(60f, 180f);
                    Act(m);
                    miniDrop = onH != IntPtr.Zero ? SitDropPx(motions[m]) : 0;
                    ms = Mini.Sit; msUntil = Time.time + sec;
                    if (onH != IntPtr.Zero) Win32.BringToTop();
                    Log($"mini: sit {m} drop {miniDrop:F0}px {sec:F0}s");
                    Brain.Tell(goalPlat.icon ? "变小坐在桌面图标上" : onH != IntPtr.Zero ? "变小爬到桌面上一个窗口的边上坐着" : "变小坐在任务栏上");
                    return;
                }
                case "lie":
                {
                    // 任务栏上优先趴着（EJ_17）
                    var m = onH == IntPtr.Zero && motions.ContainsKey(MiniLieSet[1]) && Random.value < 0.6f ? MiniLieSet[1] : PickMotion(MiniLieSet);
                    if (m == null) goto default;
                    sec = Night ? 3600f : Random.Range(120f, 300f);
                    Act(m);
                    lieAsleep = true;
                    SetMood("sleep", 0);
                    ms = Mini.Lie; msUntil = Time.time + sec;
                    talk.OnSleep();
                    Log($"mini: lie {m} {sec:F0}s");
                    Brain.Tell(onH != IntPtr.Zero ? "变小躺在桌面上一个窗口顶上睡着了" : "变小趴在任务栏上睡着了");
                    return;
                }
                default: // wander：站一会儿，然后去别处
                    ms = Mini.Idle; msUntil = Time.time + Random.Range(3f, 8f);
                    miniGoal = Random.value < 0.5f ? "sit" : Random.value < 0.5f ? "wander" : "lie";
                    return;
            }
        }

        void EndActivity()
        {
            lieAsleep = false;
            if (mood == "sleep") SetMood("idle", 0);
            if (!BeginExit()) StopMotion(0.6f);
            Log("mini: activity end");
            miniGoal = Random.value < 0.4f ? "sit" : Random.value < 0.6f ? "wander" : "lie";
        }

        void StartMiniWalk(float x)
        {
            var m = PickMotion(WalkSet);
            walkToX = x;
            walkX = FeetX;
            walkSpeed = WalkPace * PxPerM;
            if (m != null) { motions[m].speed = WalkCadence; Act(m); }
            else Act("Walk");
            yawGoal = x > FeetX ? -90f : 90f; // 屏幕右边 = 世界 -x（相机转了 180°）
            ms = Mini.Walk;
        }

        void EndMiniWalk()
        {
            if (motion != null) StopMotion(0.3f);
            action = null;
            yawGoal = 0;
        }

        void StartJump(Vector2 to)
        {
            var w = Win32.WindowRect;
            jumpFrom = new Vector2(w.x + w.width / 2f, w.yMax);
            jumpTo = to;
            float dx = to.x - jumpFrom.x, up = jumpFrom.y - to.y; // up > 0 = 往上跳
            jumpH = (up > 0 ? up * 0.75f : 0) + 45f * scale;
            jumpDur = Mathf.Clamp(0.45f + 0.0006f * (Mathf.Abs(dx) + Mathf.Abs(up)) / scale, 0.45f, 1.4f);
            yawGoal = Mathf.Abs(dx) > 40 ? (dx > 0 ? -60f : 60f) : 0;
            if (motion != null) StopMotion(0.2f);
            action = null;
            onH = IntPtr.Zero;
            ms = Mini.Jump; msT = 0;
            Log($"mini: jump {jumpFrom} → {to} ({jumpDur:F2}s, up {up:F0}px) → {goalPlat.title}");
        }

        // ---------- 跳的姿势：叠在弹簧站姿上（以前放 2 秒多的 Hop 动捕，和窗口的抛物线对不上，看着僵）----------
        const float JumpPre = 0.2f;
        float landUntil, jumpSink;
        readonly Dictionary<HumanBodyBones, Vector3> jumpAdd = new();
        static readonly Dictionary<HumanBodyBones, Vector3> Crouch = new()
        {
            [HumanBodyBones.LeftUpperLeg] = new(-28, 0, 0), [HumanBodyBones.RightUpperLeg] = new(-28, 0, 0),
            [HumanBodyBones.LeftLowerLeg] = new(48, 0, 0), [HumanBodyBones.RightLowerLeg] = new(48, 0, 0),
            [HumanBodyBones.Spine] = new(12, 0, 0), [HumanBodyBones.Head] = new(-6, 0, 0),
        };
        static readonly Dictionary<HumanBodyBones, Vector3> Tuck = new()
        {
            [HumanBodyBones.LeftUpperLeg] = new(-40, 0, 3), [HumanBodyBones.RightUpperLeg] = new(-40, 0, -3),
            [HumanBodyBones.LeftLowerLeg] = new(72, 0, 0), [HumanBodyBones.RightLowerLeg] = new(72, 0, 0),
            [HumanBodyBones.Spine] = new(-3, 0, 0), [HumanBodyBones.Head] = new(-5, 0, 0),
        };
        void SetJumpPose(Dictionary<HumanBodyBones, Vector3> p, float sink)
        {
            jumpAdd.Clear();
            if (p != null) foreach (var kv in p) jumpAdd[kv.Key] = kv.Value;
            jumpSink = sink;
        }

        // ---------- 坐在边上（主人的壁纸那样）：膝盖并拢、双手放在膝盖上、身子微微前倾、歪着头笑 ----------
        // 底子是坐椅子的动捕（腿垂下去），上面用 IK 改：大腿往里收到膝盖并拢，小腿垂在膝盖下面，手臂两节 IK 把手放到膝盖上
        static readonly string[] MiniLedgeSet = { "L_EJ_09_wait_sitliedown_F", "L_EJ_10_wait_sitliedown_F", "L_EJ_12_wait_sitliedown_F" };
        bool ledgeIK;
        float ledgeW, ledgeTilt;

        void ApplyLedgeSit(float dt)
        {
            if (debugLedge && rest != Rest.Sit) debugLedge = false;
            float want = (mini && ms == Mini.Sit || debugLedge) && ledgeIK && motion != null ? SegProgress() : 0;
            ledgeW = Mathf.MoveTowards(ledgeW, want, dt * 2.5f);
            if (ledgeW < 0.002f || shooting) return;
            float w = ledgeW;
            var root = vrm.transform;
            Vector3 R = root.right, F = root.forward;
            Transform T(HumanBodyBones b) => rig.TryGetValue(b, out var t) ? t : null;
            void Turn(HumanBodyBones b, Quaternion q) { var t = T(b); if (t) t.rotation = Quaternion.Slerp(Quaternion.identity, q, w) * t.rotation; }
            // 1) 上身微微前倾，头歪着、稍微低一点，慢慢晃
            float sway = Mathf.Sin(Time.time * 0.45f) * 2.5f;
            Turn(HumanBodyBones.Spine, Quaternion.AngleAxis(10f, R));
            Turn(HumanBodyBones.Chest, Quaternion.AngleAxis(6f, R)); // 壁纸那样往前探，手才够得着膝盖
            Turn(HumanBodyBones.Head, Quaternion.AngleAxis(ledgeTilt + sway, F) * Quaternion.AngleAxis(5f, R));
            // 2) 膝盖并拢，小腿垂在膝盖下面（模型面朝 +z，左腿在 +x）
            for (int s = 0; s < 2; s++)
            {
                float side = s == 0 ? 1 : -1;
                var ul = T(s == 0 ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg);
                var ll = T(s == 0 ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg);
                var ft = T(s == 0 ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot);
                if (!ul || !ll || !ft) continue;
                Vector3 H = ul.position, K = ll.position;
                var kl = root.InverseTransformPoint(K);
                var k2 = root.TransformPoint(new Vector3(side * 0.055f, kl.y, kl.z));
                ul.rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(K - H, k2 - H), w) * ul.rotation;
                K = ll.position;
                var fp = ft.position;
                var fl = root.InverseTransformPoint(fp);
                kl = root.InverseTransformPoint(K);
                var f2 = root.TransformPoint(new Vector3(side * 0.06f, fl.y, Mathf.Lerp(fl.z, kl.z, 0.5f)));
                ll.rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(fp - K, f2 - K), w) * ll.rotation;
            }
            // 3) 手放在膝盖上（手肘往外、往后）
            for (int s = 0; s < 2; s++)
            {
                float side = s == 0 ? 1 : -1;
                var up = T(s == 0 ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
                var lo = T(s == 0 ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
                var hd = T(s == 0 ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
                var knee = T(s == 0 ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg);
                if (!up || !lo || !hd || !knee) continue;
                var target = knee.position + Vector3.up * 0.075f - F * 0.05f - R * side * 0.01f; // 膝盖上面稍靠后（盖在裙子上）
                var pole = root.TransformDirection(new Vector3(side, -0.4f, -0.6f));
                TwoBoneIK(up, lo, hd, target, pole, w);
            }
        }

        bool debugLedge;
        /// 编辑器试：原大小坐一下壁纸坐姿（看得清楚）
        public void DebugLedgeSit(float yaw = 18)
        {
            yawGoal = yaw;
            var m = PickMotion(MiniLedgeSet);
            if (m == null) return;
            action = null;
            Act(m);
            ledgeIK = true; debugLedge = true;
            ledgeTilt = -Mathf.Sign(yaw) * Random.Range(6f, 12f);
            rest = Rest.Sit; restUntil = Time.time + 120f;
            Log("debug ledge sit " + m);
        }

        static void TwoBoneIK(Transform up, Transform lo, Transform end, Vector3 target, Vector3 pole, float w)
        {
            Quaternion u0 = up.localRotation, l0 = lo.localRotation;
            Vector3 S = up.position, E = lo.position, W = end.position;
            float a = (E - S).magnitude, b = (W - E).magnitude;
            var st = target - S;
            float d = Mathf.Clamp(st.magnitude, Mathf.Abs(a - b) + 1e-3f, a + b - 1e-3f);
            var dir = st.normalized;
            float cosA = Mathf.Clamp((a * a + d * d - b * b) / (2 * a * d), -1, 1);
            var bend = Vector3.ProjectOnPlane(pole, dir);
            if (bend.sqrMagnitude < 1e-8f) bend = Vector3.ProjectOnPlane(E - S, dir);
            bend.Normalize();
            var e2 = S + dir * (a * cosA) + bend * (a * Mathf.Sqrt(1 - cosA * cosA));
            up.rotation = Quaternion.FromToRotation(E - S, e2 - S) * up.rotation;
            E = lo.position; W = end.position;
            lo.rotation = Quaternion.FromToRotation(W - E, S + dir * d - E) * lo.rotation;
            Quaternion u1 = up.localRotation, l1 = lo.localRotation;
            up.localRotation = Quaternion.Slerp(u0, u1, w);
            lo.localRotation = Quaternion.Slerp(l0, l1, w);
        }

        // ---------- 裙子盖在腿上（在弹簧骨算完之后改，LateUpdate 顺序在 VRM 后面）----------
        // 光靠改重力方向不够：大腿的碰撞体会把前片往大腿下面推，坐下时前片垂在腿下面、大腿露出来。
        // 所以坐着（大腿抬起来）时直接把前片 / 侧片一节一节转向「膝盖上面」，过了膝盖再往下垂；权重 = 前片程度 × 大腿抬起程度。
        public void DrapeSkirt()
        {
            if (!ready || skirtChains.Count == 0 || shooting) return;
            var root = vrm.transform;
            bool dbg = Time.time > nextDrapeLog && (debugLedge || mini && ms == Mini.Sit);
            if (dbg) nextDrapeLog = Time.time + 4f;
            int nOn = 0; float wMax = 0; string ex = "";
            foreach (var c in skirtChains)
            {
                var ul = c.left ? legUL : legUR;
                var ll = c.left ? legLL : legLR;
                var thigh = ll.position - ul.position;
                var td = thigh.normalized;
                float raise = Mathf.Clamp01((Vector3.Angle(td, Vector3.down) - 15f) / 45f);
                float w = c.front * raise;
                if (w < 0.01f || c.bones.Count < 2) continue;
                nOn++; if (w > wMax) { wMax = w; ex = $"{c.bones[0].name} front {c.front:F2} raise {raise:F2} side {c.side:F2} bones {c.bones.Count}"; }
                var target = ll.position + Vector3.up * 0.07f + root.right * (c.side * 0.08f);
                for (int k = 0; k < c.bones.Count - 1; k++)
                {
                    var t = c.bones[k];
                    var p = t.position;
                    var toKnee = target - p;
                    var d = Vector3.Dot(toKnee, td) > 0.04f ? toKnee.normalized : (td * 0.3f + Vector3.down).normalized; // 过了膝盖就往下垂
                    var q = Quaternion.FromToRotation(c.bones[k + 1].position - p, d);
                    t.rotation = Quaternion.Slerp(Quaternion.identity, q, w) * t.rotation;
                }
            }
            if (dbg) Log($"drape chains {nOn}/{skirtChains.Count} max w {wMax:F2} {ex}");
        }
        float nextDrapeLog;

        /// 动捕里（坐着、躺着）也带一点呼吸和晃动，不那么僵
        bool BreatheOnMotion => motion != null && (rest is Rest.Sit or Rest.Edge or Rest.Sleep || (mini && ms is Mini.Sit or Mini.Lie));

        /// 坐在边上时淡淡地笑
        void SitFace()
        {
            if (ledgeW < 0.05f) return;
            expGoal.TryGetValue("happy", out var h);
            expGoal.TryGetValue("relaxed", out var r);
            expGoal["happy"] = Mathf.Max(h, 0.3f * ledgeW);
            expGoal["relaxed"] = Mathf.Max(r, 0.35f * ledgeW);
        }

        void StartGrow()
        {
            ms = Mini.Grow; msT = 0;
            SetView(camBottom, camTotal, 1.2f);
            SetMood("happy", 2.5f);
        }
    }
}
