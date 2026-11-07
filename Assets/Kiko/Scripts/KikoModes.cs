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
                return Screen.height - cam.WorldToScreenPoint(h.position + Vector3.up * 0.24f).y;
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
            return true;
        }

        float bustMeasureAt;
        void BustView(float ledge, float dur) => SetView(ledge, (restHeadY + 0.3f - ledge) * WinH / baseHeight * 1.1f, dur);

        void MeasureBust()
        {
            bustMeasureAt = 0;
            float Y(HumanBodyBones b) => rig.TryGetValue(b, out var t) ? t.position.y : restChestY;
            float fl = (Y(HumanBodyBones.LeftLowerArm) + Y(HumanBodyBones.LeftHand)) / 2f, fr = (Y(HumanBodyBones.RightLowerArm) + Y(HumanBodyBones.RightHand)) / 2f;
            float arm = Mathf.Min(fl, fr), ledge = Mathf.Clamp(arm - 0.035f, restChestY - 0.25f, restChestY + 0.05f);
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
        struct Plat { public IntPtr h; public int x0, x1, y; public string title; }
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
                if (p.y >= fy - 3 && fx >= p.x0 + Margin / 2 && fx <= p.x1 - Margin / 2 && p.y < best.y) best = p;
            return best;
        }

        void StandOn(Plat p)
        {
            onH = p.h;
            surfY = p.y;
            if (onH != IntPtr.Zero && !Win32.RectOf(onH, out onRect)) onH = IntPtr.Zero;
        }

        // 脚下的窗口被拖走 → 跟着走；关掉 / 最小化 / 被别的窗口盖住 → 掉下去
        void FollowPlatform()
        {
            if (onH == IntPtr.Zero || state != State.Idle || ms == Mini.Jump) return;
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
            if (state != State.Idle) return; // 被拖着 / 正在掉：UpdateInput 管
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
                    float u = Mathf.Clamp01(msT / jumpDur);
                    float x = Mathf.Lerp(jumpFrom.x, jumpTo.x, u);
                    float y = Mathf.Lerp(jumpFrom.y, jumpTo.y, u) - jumpH * 4f * u * (1 - u);
                    PlaceFeet(x, y);
                    if (u >= 1)
                    {
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
                    if (motion == null) { ms = Mini.Idle; msUntil = Time.time + 1f; break; }
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
                var wins = plats.Where(p => p.h != IntPtr.Zero && p.x1 - p.x0 >= Margin * 3).ToList();
                // 已经在窗口上了：一半留在这个窗口上，一半换一个
                var here = wins.Where(p => p.h == onH && FeetX >= p.x0 && FeetX <= p.x1).ToList();
                if (here.Count > 0 && (miniGoal != "wander" && Random.value < 0.5f)) goalPlat = here[0];
                else if (wins.Count > 0) goalPlat = wins[Random.Range(0, wins.Count)];
                else goalPlat = plats.Last(); // 桌面上没有窗口：就在任务栏上
                goalX = Mathf.RoundToInt(Random.Range(goalPlat.x0 + Margin * 1.5f, goalPlat.x1 - Margin * 1.5f));
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
            int landX = Mathf.Clamp(goalX, goalPlat.x0 + Margin, goalPlat.x1 - Margin);
            int reach = Mathf.RoundToInt(480 * scale);
            int takeoff = Mathf.Clamp(landX + Mathf.Clamp(FeetX - landX, -reach, reach), cur.x0 + Margin, cur.x1 - Margin);
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
                    // 窗口上 = 坐在边上腿垂下去；任务栏上 = 坐地上
                    var m = onH != IntPtr.Zero ? PickMotion(MiniEdgeSet) : PickMotion(SitSet);
                    if (m == null) goto default;
                    sec = Random.Range(60f, 180f);
                    Act(m);
                    miniDrop = onH != IntPtr.Zero ? SitDropPx(motions[m]) : 0;
                    ms = Mini.Sit; msUntil = Time.time + sec;
                    if (onH != IntPtr.Zero) Win32.BringToTop();
                    Log($"mini: sit {m} drop {miniDrop:F0}px {sec:F0}s");
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
            Act("Hop");
            onH = IntPtr.Zero;
            ms = Mini.Jump; msT = 0;
            Log($"mini: jump {jumpFrom} → {to} ({jumpDur:F2}s, up {up:F0}px) → {goalPlat.title}");
        }

        void StartGrow()
        {
            ms = Mini.Grow; msT = 0;
            SetView(camBottom, camTotal, 1.2f);
            SetMood("happy", 2.5f);
        }
    }
}
