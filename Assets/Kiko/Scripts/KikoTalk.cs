// 希狐说话：气泡 + 台词 + 右键菜单（移植 Tauri 版 pet.js 的 say / poke / chat / 菜单，台词是 feature/kiko-fox 的 lines.js）。
// 台词表：Resources/kiko_lines.json（Tools/export_lines.js 从 lines.js 导出）。
// 气泡用 IMGUI 画在窗口顶上留出来的空白里（KikoPet.bubbleSpace），透明窗口里只有气泡本身不透明，所以只有气泡挡鼠标。
// 设置存在 PlayerPrefs：kiko.name（称呼，默认 Yank）、kiko.chatty（0 安静 / 1 少 / 2 正常 / 3 多）。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Kiko
{
    [Serializable] class KikoLineGroup { public string key; public string[] lines; }
    [Serializable] class KikoLineData { public KikoLineGroup[] groups; }

    public class KikoTalk : MonoBehaviour
    {
        public KikoPet pet;
        const string Zhixu = "https://yank-study-lab.yank-study-lab.workers.dev";

        readonly Dictionary<string, string[]> lines = new();
        string text;
        float until;
        float startedAt, nextChat;
        readonly List<float> pokes = new();
        float warnedAt = -999, lastPokeUp = -999;
        bool hidden;

        public string Name
        {
            get { var n = PlayerPrefs.GetString("kiko.name", "").Trim(); return n.Length > 0 ? n : "Yank"; }
        }
        public int Chatty
        {
            get => PlayerPrefs.GetInt("kiko.chatty", 2);
            set { PlayerPrefs.SetInt("kiko.chatty", value); PlayerPrefs.Save(); ScheduleChat(); }
        }
        public bool Showing => text != null && Time.time < until;

        void Awake()
        {
            var ta = Resources.Load<TextAsset>("kiko_lines");
            if (ta)
                foreach (var g in JsonUtility.FromJson<KikoLineData>(ta.text).groups)
                    lines[g.key] = g.lines;
            startedAt = Time.time;
            ScheduleChat();
            KikoPet.Log($"talk: {lines.Count} line groups, chatty {Chatty}, name {Name}");
        }

        // ---------- 台词 ----------
        string Pick(string key) => lines.TryGetValue(key, out var a) && a.Length > 0 ? a[Random.Range(0, a.Length)] : null;

        string Fill(string s)
        {
            var now = DateTime.Now;
            return s.Replace("{n}", Name).Replace("{s}", "知序").Replace("{h}", now.Hour.ToString())
                .Replace("{m}", Mathf.RoundToInt((Time.time - startedAt) / 60f).ToString());
        }

        // 一句台词配什么表情（lines.js 的 MOOD_HINTS；没有饭团表情 → 开心）
        static readonly (Regex re, string mood)[] Hints =
        {
            (new Regex("💢|生气|不理你|罢工|警告"), "angry"),
            (new Regex("呜哇|欺负|委屈|会肿"), "pout"),
            (new Regex("白饭|油豆腐|饭团|吃|饿"), "happy"),
            (new Regex("睡|五分钟|揉眼睛|zzz|服务器繁忙|躺着"), "sleepy"),
            (new Regex("！！|诶|吓"), "surprised"),
            (new Regex("喷|少爷|收费|token|一碗|一块"), "smirk"),
            (new Regex("哼哼|首席|伟大|魅力|夸"), "smug"),
            (new Regex("こんこん|小调|嘿嘿|好看|开心"), "happy"),
            (new Regex("戳什么|闲|不帮你|不许|摸鱼"), "meh"),
        };
        static string MoodFor(string t, string fallback = "smug")
        {
            foreach (var (re, m) in Hints) if (re.IsMatch(t)) return m;
            return fallback;
        }

        /// delay > 0：先让正在做的反应（戳一下的缩、落地）做完再换表情动作
        public void Say(string line, string mood, float sec, float delay = 0)
        {
            if (line == null) return;
            text = Fill(line);
            until = Time.time + sec;
            if (pet.Resting) mood = null; // 坐着/睡着时说话不换表情动作，不然会把坐姿/睡姿打断
            if (mood != null)
            {
                if (delay > 0) StartCoroutine(MoodLater(mood, sec, delay));
                else pet.SetMood(mood, sec);
            }
            KikoPet.Log("say: " + text);
        }

        IEnumerator MoodLater(string mood, float sec, float delay)
        {
            yield return new WaitForSeconds(delay);
            pet.SetMood(mood, sec - delay);
        }

        public void SayKey(string key, string mood = null, float sec = 4.5f, float delay = 0)
        {
            var l = Pick(key);
            if (l != null) Say(l, mood ?? MoodFor(l), sec, delay);
        }

        public void Hide() => text = null;

        // ---------- 事件 ----------
        public void Hello() => SayKey("hello", "wave", 5f);
        public void OnDropped() => SayKey("dropped", "pout", 3.5f, 0.7f);

        /// 松手 = 戳一下。返回 true 表示这次是双击（打开知序）
        public bool OnPoke()
        {
            float now = Time.time;
            // 双击 = 打开知序；但乱戳也是一串快速点击 → 双击后 0.35 秒内又点了就当成乱戳，不打开
            if (openPending != null) { StopCoroutine(openPending); openPending = null; noOpenUntil = now + 0.8f; }
            else if (now < noOpenUntil) noOpenUntil = now + 0.8f;
            else if (now - lastPokeUp < 0.3f) openPending = StartCoroutine(OpenSoon());
            lastPokeUp = now;
            // 乱戳：3 秒内 6 下生气并警告；警告后 30 秒内再戳 4 下就躲起来（Tauri 版躲进托盘，这里躲 45 秒）
            pokes.RemoveAll(t => now - t > 3f);
            pokes.Add(now);
            if (now - warnedAt > 30f) warnedAt = -999;
            if (warnedAt > 0)
            {
                int after = 0;
                foreach (var t in pokes) if (t > warnedAt) after++;
                if (after >= 4) { StartCoroutine(RunAway()); return false; }
                SayKey("bugged", "angry", 4f, 0.5f);
                return false;
            }
            if (pokes.Count >= 6)
            {
                warnedAt = now;
                pokes.Clear();
                SayKey("angry", "angry", 6f, 0.5f);
                return false;
            }
            int h = DateTime.Now.Hour;
            if (h < 5) { SayKey("pokeNight", "meh", 3.5f, 0.55f); return false; }
            if (h < 8 && Random.value < 0.6f) { SayKey("pokeMorning", "sleepy", 3.5f, 0.55f); return false; }
            var l = Pick("poke");
            Say(l, MoodFor(l), 3.5f, 0.55f);
            return false;
        }

        Coroutine openPending;
        float noOpenUntil;
        IEnumerator OpenSoon()
        {
            yield return new WaitForSeconds(0.35f);
            openPending = null;
            pokes.Clear();
            OpenSite();
        }

        IEnumerator RunAway()
        {
            warnedAt = -999;
            pokes.Clear();
            SayKey("runAway", "angry", 1.8f);
            yield return new WaitForSeconds(1.6f);
            hidden = true;
            Hide();
            Win32.ShowWindow(false);
            KikoPet.Log("run away (45s)");
            yield return new WaitForSeconds(45f);
            Win32.ShowWindow(true);
            hidden = false;
            SayKey("summoned", "meh", 4f);
        }

        public void OpenSite()
        {
            SayKey("openZhixu", "happy", 3.5f);
            Application.OpenURL(Zhixu + "/#desk-whale");
            KikoPet.Log("open site");
        }

        // ---------- 闲聊：按话量隔一阵说一句（分钟，和 Tauri 版 CHAT_GAP 一样）----------
        static readonly Vector2[] Gap = { Vector2.zero, new(45, 90), new(20, 45), new(8, 15) };
        void ScheduleChat()
        {
            int c = Mathf.Clamp(Chatty, 0, 3);
            nextChat = c == 0 ? float.MaxValue : Time.time + Random.Range(Gap[c].x, Gap[c].y) * 60f;
        }

        public void Chat()
        {
            ScheduleChat();
            int h = DateTime.Now.Hour;
            var pool = new List<string>();
            void Add(string k, int times = 1) { if (lines.TryGetValue(k, out var a)) for (int i = 0; i < times; i++) pool.AddRange(a); }
            Add("chatAny");
            if (h < 5) Add("chatNight", 2);
            else if (h < 7) Add("chatEarly");
            else if (h < 11) Add("chatMorning");
            else if (h < 14) Add("chatNoon");
            else if (h < 18) Add("chatAfternoon");
            else if (h < 22) Add("chatEvening");
            else Add("chatLate");
            if (Time.time - startedAt > 90 * 60) Add("chatLong");
            if (pool.Count == 0) return;
            var l = pool[Random.Range(0, pool.Count)];
            Say(l, Regex.IsMatch(l, "还不睡|恨现在|关灯") ? "meh" : MoodFor(l, "happy"), 8f);
        }

        void Update()
        {
            if (hidden) return;
            if (pet.Sleeping)
            {
                // 睡着了偶尔说梦话
                if (Time.time > nextSleepTalk && !Showing) { SayKey("sleepTalk", null, 5f); nextSleepTalk = Time.time + Random.Range(150f, 360f); }
                return;
            }
            if (Time.time > nextChat && pet.CanChat && !Showing) Chat();
        }

        // ---------- 睡觉 ----------
        float nextSleepTalk, groggyAt = -999;
        public void OnSleep()
        {
            Hide();
            nextSleepTalk = Time.time + Random.Range(40f, 90f);
        }

        /// 睡着时被戳醒：迷迷糊糊
        public void OnPokeAsleep()
        {
            groggyAt = Time.time;
            SayKey("groggy", "sleepy", 6f);
        }

        /// 刚被戳醒 12 秒内又戳 → 起床气。返回 true 表示已经处理了
        public bool OnPokeGroggy()
        {
            if (Time.time - groggyAt > 12f) return false;
            groggyAt = -999;
            SayKey("mad", "angry", 6f, 0.3f);
            return true;
        }

        // ---------- 右键菜单（Windows 原生弹出菜单）----------
        public void Menu()
        {
            int c = Chatty;
            int id = Win32.PopupMenu(
                (1, "说句话", false), (2, "摸摸头", false), (3, "打开知序（双击也行）", false), (0, "-", false),
                (4, pet.InMini ? "找个窗口坐会儿" : "坐下歇会儿（变小去窗口上坐）", false), (7, pet.InBust ? "从任务栏上起来" : "趴在任务栏上（大头）", false),
                (6, "走走", false), (5, pet.Sleeping ? "叫醒她" : "去睡觉", false), (8, "变回来", false), (0, "-", false),
                (13, "话多一点", c == 3), (12, "正常", c == 2), (11, "话少一点", c == 1), (10, "安静", c == 0), (0, "-", false),
                (20, "退出", false));
            KikoPet.Log("menu → " + id);
            switch (id)
            {
                case 1: if (pet.Sleeping) SayKey("sleepTalk", null, 5f); else Chat(); break;
                case 2: if (pet.Sleeping) SayKey("patSleep", null, 4f); else SayKey("pat", "pat", 4f); break;
                case 4:
                    // 主人 11-07：明确让她坐下 → 缩小，去桌面上找个程序窗口爬上去坐着
                    if (pet.Sleeping && !pet.InMini) pet.WakeUp(true);
                    if (pet.EnterMini("sit")) Say("那我找个地方坐一会儿～", null, 3f);
                    else Say("……先把我放回任务栏上啦。", "meh", 3f);
                    break;
                case 5:
                    if (pet.Sleeping) { if (pet.InMini) pet.ComeBack(); else pet.WakeUp(true); }
                    else if (pet.EnterMini("lie")) Say("那我找个窗口睡一会儿……有事戳我。zzz", null, 3f);
                    else if (pet.GoSleep()) Say("那我睡了……有事戳我。zzz", null, 3f);
                    else Say("……睡不着。（动作包没装）", "meh", 3f);
                    break;
                case 8:
                    if (pet.InMini) pet.ComeBack();
                    else if (pet.InBust) pet.ExitBust();
                    else Say("我本来就是这么大呀？", "smug", 3f);
                    break;
                case 3: OpenSite(); break;
                case 6:
                    if (pet.InMini) { pet.EnterMini("wander"); break; }
                    if (pet.Sleeping) pet.WakeUp(true);
                    if (pet.InBust) pet.ExitBust(0.4f);
                    if (!pet.StartWalk()) Say("……走不了。（得先站在任务栏上）", "meh", 3f);
                    break;
                case 7:
                    if (pet.InBust) { pet.ExitBust(); break; }
                    if (pet.InMini) pet.ComeBack();
                    if (pet.Sleeping) pet.WakeUp(true);
                    if (!pet.EnterBust(Random.Range(120f, 300f))) Say("……先把我放回任务栏上啦。", "meh", 3f);
                    break;
                case 10: case 11: case 12: case 13:
                    Chatty = id - 10;
                    Say(Chatty == 0 ? "好吧，我安静待着……有事戳我。" : Chatty == 3 ? "那我可要多嘴了哦，{n}～" : "知道啦。", Chatty == 0 ? "meh" : "smug", 3f);
                    break;
                case 20: StartCoroutine(Quit()); break;
            }
        }

        IEnumerator Quit()
        {
            SayKey("bye", "wave", 2f);
            yield return new WaitForSeconds(1.4f);
            KikoPet.Log("quit (menu)");
            Application.Quit();
        }

        // ---------- 气泡 ----------
        GUIStyle style, tailStyle;
        Texture2D bubbleTex, tailTex;
        Rect bubbleRect;
        static readonly Color Fill_ = new(1f, 1f, 1f, 1f), Line = new(0.72f, 0.70f, 0.88f, 1f), Ink = new(0.18f, 0.20f, 0.34f, 1f);

        /// 光标（屏幕像素）是不是点在气泡上（点气泡 = 关掉气泡，不算戳）
        public bool HitBubble(Vector2Int screen)
        {
            if (!Showing) return false;
            var w = Win32.WindowRect;
            return bubbleRect.Contains(new Vector2(screen.x - w.x, screen.y - w.y));
        }

        void MakeStyle()
        {
            // 9 宫格圆角底图：白底 + 淡紫描边
            const int N = 32, R = 11;
            bubbleTex = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = Mathf.Max(0, Mathf.Max(R - x - 0.5f, x + 0.5f - (N - R))), dy = Mathf.Max(0, Mathf.Max(R - y - 0.5f, y + 0.5f - (N - R)));
                    float d = Mathf.Sqrt(dx * dx + dy * dy); // 离圆角圆心的距离（直边处为 0）
                    float edge = R - d;                      // 到外边缘的距离
                    float a = Mathf.Clamp01(edge + 0.5f);
                    var c = edge < 2.2f ? Line : Fill_;
                    bubbleTex.SetPixel(x, y, new Color(c.r, c.g, c.b, a));
                }
            // 直边上的描边（上面只算了圆角）
            for (int i = 0; i < N; i++)
                for (int k = 0; k < 2; k++)
                {
                    if (i >= R && i < N - R)
                    {
                        bubbleTex.SetPixel(i, k, Line); bubbleTex.SetPixel(i, N - 1 - k, Line);
                        bubbleTex.SetPixel(k, i, Line); bubbleTex.SetPixel(N - 1 - k, i, Line);
                    }
                }
            bubbleTex.Apply();
            // 小尾巴（朝下的三角）
            const int T = 24;
            tailTex = new Texture2D(T, T, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < T; y++)
                for (int x = 0; x < T; x++)
                {
                    float v = 1f - (float)y / T;          // 0 = 顶（接气泡）→ 1 = 尖（GUI 里 y 向下，贴图 y 向上）
                    float half = (1f - v) * T * 0.5f;     // 这一行三角的半宽
                    float dx = Mathf.Abs(x + 0.5f - T * 0.35f - v * T * 0.15f);
                    float edge = half - dx;
                    var c = edge < 2f ? Line : Fill_;
                    tailTex.SetPixel(x, y, new Color(c.r, c.g, c.b, Mathf.Clamp01(edge + 0.5f)));
                }
            for (int x = 0; x < T; x++) for (int k = 0; k < 3; k++) { var p = tailTex.GetPixel(x, T - 1 - k); if (p.a > 0) tailTex.SetPixel(x, T - 1 - k, new Color(1, 1, 1, p.a)); }
            tailTex.Apply();

            var font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Yu Gothic UI", "Segoe UI", "Segoe UI Emoji", "Segoe UI Symbol" }, 16);
            style = new GUIStyle
            {
                font = font,
                wordWrap = true,
                alignment = TextAnchor.MiddleLeft,
                border = new RectOffset(R + 1, R + 1, R + 1, R + 1),
            };
            style.normal.background = bubbleTex;
            style.normal.textColor = Ink;
            tailStyle = new GUIStyle();
            tailStyle.normal.background = tailTex;
        }

        void OnGUI()
        {
            if (!Showing || hidden || !pet || !pet.Ready) return;
            if (style == null) MakeStyle();
            float s = pet.UiScale;
            style.fontSize = Mathf.RoundToInt(15 * s);
            int pad = Mathf.RoundToInt(11 * s);
            style.padding = new RectOffset(pad + 2, pad + 2, pad, pad);
            float W = Screen.width, w = Mathf.Min(W * 0.72f, 300 * s);
            var content = new GUIContent(text);
            float h = Mathf.Max(style.CalcHeight(content, w), 40 * s);
            // 气泡放在头顶上方（变小 / 趴着时头的位置不一样），最高顶到窗口上沿
            float x = Mathf.Round(W * 0.5f - w * 0.62f), y = Mathf.Round(Mathf.Max(6 * s, pet.HeadTopGui - h - 6 * s));
            // 气泡底边不要压到脸：顶多压到窗口里人物区域头顶往下一点
            bubbleRect = new Rect(x, y, w, h);
            GUI.Box(bubbleRect, content, style);
            float tw = 16 * s, th = 14 * s;
            GUI.Box(new Rect(W * 0.5f - tw * 0.2f, y + h - 3 * s, tw, th), GUIContent.none, tailStyle);
        }
    }
}
