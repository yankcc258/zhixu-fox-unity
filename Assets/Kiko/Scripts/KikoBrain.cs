// 希狐的「脑子」：和知序网页上的她共用一份记忆（知序服务器 /api/pet/*）。
//  · 说句话：右键「说句话」→ 头顶弹出输入框，回车发给知序，她用知序那套人设、今天的课和饮食、记仇本、两边合在一起的聊天记录回话，表情跟着变
//  · 发生的事：被戳跑、睡着、变小去窗口上坐……记进共用记忆，网页上的她也知道
//  · 记仇本：戳跑 / 连戳生气 / 戳醒记账，摸头还账（和网页同一本）
// 钥匙：Cloudflare Access 服务令牌（只能进 /api/pet/*），放在 %APPDATA%\ZhixuFox\brain.json，不进仓库：
//   { "base": "https://yank-study-lab.yank-study-lab.workers.dev", "clientId": "xxx.access", "clientSecret": "..." }
// 没有这个文件 / 没填好：她只说本地台词，「说句话」会提示怎么配。
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Kiko
{
    [Serializable] class BrainConfig { public string @base = ""; public string clientId = ""; public string clientSecret = ""; }
    [Serializable] class ChatReq { public string message; }
    [Serializable] class ChatRes { public string reply; public string ja; public string mood; public string outfit; public string error; public string offered; public string answered; }
    [Serializable] class AttentionItem { public string id; public string say; }
    [Serializable] class AttentionRes { public bool quiet; public AttentionItem[] items; }
    [Serializable] class AttentionSaid { public string id; }
    [Serializable] class EventReq { public string text; }
    [Serializable] class GrudgeReq { public string type; }
    [Serializable] class PayReq { public string how; }

    public class KikoBrain : MonoBehaviour
    {
        public KikoPet pet;
        public KikoTalk talk;
        BrainConfig cfg;
        public bool Online => cfg != null && cfg.@base.StartsWith("https://") && cfg.clientId.Length > 8 && cfg.clientSecret.Length > 8;
        public bool Busy { get; private set; }
        public static string ConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZhixuFox", "brain.json");

        // 知序给的表情 → 桌面这边的心情
        static readonly Dictionary<string, string> MoodMap = new()
        {
            ["happy"] = "happy", ["smug"] = "smug", ["smirk"] = "smirk", ["bleh"] = "bleh", ["meh"] = "meh", ["pout"] = "pout",
            ["surprised"] = "surprised", ["excited"] = "excited", ["onigiri"] = "happy", ["sleepy"] = "sleepy", ["strike"] = "angry",
        };

        void Awake() { LoadConfig(); LoadAttention(); }

        void LoadConfig()
        {
            try
            {
                var path = ConfigPath;
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, JsonUtility.ToJson(new BrainConfig { @base = "https://yank-study-lab.yank-study-lab.workers.dev" }, true), Encoding.UTF8);
                    KikoPet.Log("brain: 写了空的 " + path + "（填 clientId / clientSecret 后重启）");
                }
                cfg = JsonUtility.FromJson<BrainConfig>(File.ReadAllText(path, Encoding.UTF8)) ?? new BrainConfig();
                cfg.@base = (cfg.@base ?? "").Trim().TrimEnd('/');
                cfg.clientId = (cfg.clientId ?? "").Trim();
                cfg.clientSecret = (cfg.clientSecret ?? "").Trim();
                KikoPet.Log(Online ? "brain: online → " + cfg.@base : "brain: offline（brain.json 没填好）");
            }
            catch (Exception e) { cfg = new BrainConfig(); KikoPet.Log("brain: 读配置出错 " + e.Message); }
        }

        UnityWebRequest Req(string path, string method, string json)
        {
            var r = new UnityWebRequest(cfg.@base + "/api/pet/" + path, method) { downloadHandler = new DownloadHandlerBuffer(), timeout = 40 };
            if (json != null)
            {
                r.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                r.SetRequestHeader("Content-Type", "application/json");
            }
            r.SetRequestHeader("CF-Access-Client-Id", cfg.clientId);
            r.SetRequestHeader("CF-Access-Client-Secret", cfg.clientSecret);
            r.SetRequestHeader("X-Kiko-Desk", "1");
            return r;
        }

        // ---------- 说句话 ----------
        public void Chat(string msg)
        {
            msg = (msg ?? "").Trim();
            if (msg.Length == 0 || Busy) return;
            if (msg.Length > 300) msg = msg.Substring(0, 300);
            if (!Online) { talk.Say("……（指指耳朵）听不见。brain.json 还没填钥匙呢。", "pout", 5f); return; }
            StartCoroutine(ChatCo(msg));
        }

        IEnumerator ChatCo(string msg)
        {
            Busy = true;
            talk.Say("……（竖起耳朵）", null, 40f);
            if (pet.Voice) pet.Voice.Warm();
            var r = Req("chat", "POST", JsonUtility.ToJson(new ChatReq { message = msg }));
            yield return r.SendWebRequest();
            Busy = false;
            ChatRes res = null;
            try { res = JsonUtility.FromJson<ChatRes>(r.downloadHandler.text); } catch { }
            if (r.result == UnityWebRequest.Result.Success && !string.IsNullOrEmpty(res?.reply))
            {
                MoodMap.TryGetValue(res.mood ?? "", out var mood);
                if (KikoAttentionState.Valid(res.offered)) RememberAttention(res.offered, true);
                if (KikoAttentionState.Valid(res.answered)) res.reply += "\n（回答已记下，稍后批改）";
                float sec = Mathf.Clamp(3f + res.reply.Length * 0.12f, 4f, 14f);
                talk.Say(res.reply, mood, sec, 0, res.ja); // ja：这句话的日语（给声音用），知序没给就念中文
                KikoPet.Log($"brain: chat ok ({res.mood}) {res.reply.Length} 字");
            }
            else
            {
                string why = !string.IsNullOrEmpty(res?.error) ? res.error : r.responseCode == 403 ? "钥匙不对" : r.error;
                talk.Say(r.responseCode == 403 || r.responseCode == 401 ? "……门口的保安不让我进知序。（钥匙不对？）" : "……（发呆）信号不好，刚才没听清。", "pout", 5f);
                KikoPet.Log($"brain: chat 失败 {r.responseCode} {why}");
            }
            r.Dispose();
        }

        // ---------- 自己想起前几天的事搭话（知序 /api/pet/muse，每天最多 6 次，服务器管）----------
        // 没配钥匙 / 今天说够了 / 失败 → 返回 false，调用的地方改说本地台词
        float museBlockedUntil;
        public bool Muse()
        {
            if (!Online || Busy || attentionBusy || attention.Cooling(NowMs) || Time.time < museBlockedUntil) return false;
            StartCoroutine(MuseCo());
            return true;
        }

        IEnumerator MuseCo()
        {
            Busy = true;
            var r = Req("muse", "POST", "{}");
            yield return r.SendWebRequest();
            Busy = false;
            ChatRes res = null;
            try { res = JsonUtility.FromJson<ChatRes>(r.downloadHandler.text); } catch { }
            if (r.result == UnityWebRequest.Result.Success && !string.IsNullOrEmpty(res?.reply))
            {
                MoodMap.TryGetValue(res.mood ?? "", out var mood);
                talk.Say(res.reply, mood, Mathf.Clamp(3f + res.reply.Length * 0.12f, 5f, 12f), 0, res.ja);
                KikoPet.Log("brain: muse ok");
            }
            else
            {
                // 429 今天说够了 / 503 记忆还没开 / 404 最近没什么可想的：过一阵再试，这次改说本地台词
                museBlockedUntil = Time.time + (r.responseCode == 429 ? 6 * 3600 : 1800);
                KikoPet.Log($"brain: muse 跳过 {r.responseCode}");
                talk.Chat(true);
            }
            r.Dispose();
        }

        // ---------- 发生的事 / 记仇本（不等回应，失败就算了）----------
        /// 编辑器里批量试动作/拍图时关掉：不往共用记忆里写「睡着了、趴任务栏」这些假事件
        public static bool Mute;
        public void Tell(string text)
        {
            if (Online && !Mute && !string.IsNullOrEmpty(text)) StartCoroutine(Fire("event", JsonUtility.ToJson(new EventReq { text = text })));
        }
        public void Grudge(string type)
        {
            if (Online && !Mute) StartCoroutine(Fire("grudge", JsonUtility.ToJson(new GrudgeReq { type = type })));
        }
        public void PayPat()
        {
            if (Online && !Mute) StartCoroutine(Fire("grudge/pay", JsonUtility.ToJson(new PayReq { how = "pat" })));
        }

        IEnumerator Fire(string path, string json)
        {
            var r = Req(path, "POST", json);
            yield return r.SendWebRequest();
            if (r.result != UnityWebRequest.Result.Success) KikoPet.Log($"brain: {path} 失败 {r.responseCode} {r.downloadHandler?.text}");
            r.Dispose();
        }

        // ---------- 总管的「待你决定」：一分钟看一眼，显示后才回执 ----------
        KikoAttentionState attention = new KikoAttentionState();
        string attentionKey;
        float nextAttention = 15f;
        bool attentionBusy;
        static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool CanAttention => Online && !Mute && !Busy && !typing && pet && pet.CanChat && !pet.Sleeping
            && talk && !talk.Hidden && talk.Chatty > 0 && !talk.Showing
            && (!pet.Voice || !pet.Voice.Speaking) && !Win32.ForegroundFullscreen;

        void LoadAttention()
        {
            // 按站点和服务身份隔离，配置里换账号后不会沿用另一份回执。
            using (var sha = System.Security.Cryptography.SHA256.Create())
                attentionKey = "kiko.attention." + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(cfg.@base + "|" + cfg.clientId))).Replace("-", "");
            try { attention = JsonUtility.FromJson<KikoAttentionState>(PlayerPrefs.GetString(attentionKey, "{}")) ?? new KikoAttentionState(); }
            catch { attention = new KikoAttentionState(); }
            attention.Clean();
        }

        void RememberAttention(string id, bool acked)
        {
            attention.Remember(id, NowMs, acked);
            PlayerPrefs.SetString(attentionKey, JsonUtility.ToJson(attention));
            PlayerPrefs.Save();
        }

        IEnumerator AttentionAck(string id)
        {
            using (var r = Req("attention/said", "POST", JsonUtility.ToJson(new AttentionSaid { id = id })))
            {
                yield return r.SendWebRequest();
                if (r.result == UnityWebRequest.Result.Success || r.responseCode == 404) RememberAttention(id, true);
                else KikoPet.Log($"brain: attention 回执稍后再交 ({r.responseCode})");
            }
        }

        IEnumerator AttentionPoll()
        {
            attentionBusy = true;
            try
            {
                foreach (var item in attention.Pending()) yield return AttentionAck(item.id);
                if (attention.Pending().Length > 0 || !CanAttention || attention.Cooling(NowMs)) yield break;
                AttentionRes data = null;
                using (var r = Req("attention", "GET", null))
                {
                    yield return r.SendWebRequest();
                    if (r.result != UnityWebRequest.Result.Success) { KikoPet.Log($"brain: attention 稍后再看 ({r.responseCode})"); yield break; }
                    try { data = JsonUtility.FromJson<AttentionRes>(r.downloadHandler.text); } catch { }
                }
                if (data == null || data.quiet || data.items == null || !CanAttention) yield break;
                foreach (var item in data.items)
                {
                    if (item == null || !KikoAttentionState.Valid(item.id) || attention.Seen(item.id) || string.IsNullOrWhiteSpace(item.say)) continue;
                    talk.Say(item.say, "smug", Mathf.Clamp(5f + item.say.Length * 0.15f, 8f, 20f));
                    yield return new WaitForEndOfFrame();
                    if (!talk.IsShowing(item.say)) yield break;
                    RememberAttention(item.id, false);
                    yield return AttentionAck(item.id);
                    break; // 留十五分钟给这道题，不连着问。
                }
            }
            finally { attentionBusy = false; }
        }

        // ---------- 输入框 ----------
        bool typing;
        string draft = "";
        float typingSince;
        GUIStyle boxStyle, fieldStyle, hintStyle;

        public void OpenInput()
        {
            if (Busy) return;
            typing = true;
            draft = "";
            typingSince = Time.time;
            talk.Hide();
            if (pet.Voice) pet.Voice.Warm(); // 趁他打字先把语音引擎叫起来，回话时就不用等
            Win32.Focus(); // 让键盘输入进到这个窗口
            Input.imeCompositionMode = IMECompositionMode.On;
        }

        void CloseInput()
        {
            typing = false;
            Input.imeCompositionMode = IMECompositionMode.Auto;
        }

        void Update()
        {
            // 点到别的程序上了 → 收起输入框
            if (typing && Time.time - typingSince > 0.8f && !Application.isFocused) CloseInput();
            if (Online && !Mute && !attentionBusy && Time.unscaledTime >= nextAttention)
            {
                nextAttention = Time.unscaledTime + 60f;
                StartCoroutine(AttentionPoll());
            }
        }

        void OnGUI()
        {
            if (!typing || !pet || !pet.Ready) return;
            float s = pet.UiScale, W = Screen.width;
            if (boxStyle == null)
            {
                var bg = new Texture2D(1, 1); bg.SetPixel(0, 0, new Color(1, 1, 1, 0.97f)); bg.Apply();
                boxStyle = new GUIStyle(GUI.skin.box) { normal = { background = bg } };
                fieldStyle = new GUIStyle(GUI.skin.textField) { normal = { textColor = new Color(0.2f, 0.18f, 0.3f) }, focused = { textColor = new Color(0.2f, 0.18f, 0.3f) } };
                hintStyle = new GUIStyle(GUI.skin.label) { normal = { textColor = new Color(0.55f, 0.52f, 0.62f) } };
            }
            fieldStyle.fontSize = Mathf.RoundToInt(15 * s);
            hintStyle.fontSize = Mathf.RoundToInt(13 * s);
            float w = Mathf.Min(W * 0.86f, 320 * s), h = 34 * s;
            float y = Mathf.Round(Mathf.Max(6 * s, pet.HeadTopGui - h - 10 * s)), x = Mathf.Round(W * 0.5f - w / 2);
            var e = Event.current;
            // 输入法还在拼字时的回车是确认候选字，不是发送
            if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && string.IsNullOrEmpty(Input.compositionString))
            {
                var msg = draft;
                CloseInput();
                Chat(msg);
                e.Use();
                return;
            }
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { CloseInput(); e.Use(); return; }
            GUI.Box(new Rect(x - 4 * s, y - 4 * s, w + 8 * s, h + 8 * s), GUIContent.none, boxStyle);
            GUI.SetNextControlName("kikoInput");
            draft = GUI.TextField(new Rect(x, y, w, h), draft, 300, fieldStyle);
            GUI.FocusControl("kikoInput");
            if (draft.Length == 0 && string.IsNullOrEmpty(Input.compositionString))
                GUI.Label(new Rect(x + 8 * s, y + 6 * s, w, h), "想说什么？（回车发送，Esc 取消）", hintStyle);
        }
    }
}
