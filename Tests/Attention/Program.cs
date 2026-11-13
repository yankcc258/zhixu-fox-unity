using System;
using System.Collections;
using System.Reflection;
using Kiko;
using UnityEngine;
using UnityEngine.Networking;
class Test
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static void Call(KikoBrain b, string method) { var result = typeof(KikoBrain).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(b, null); if (result is IEnumerator e) Runner.Run(e); }
    static void Field(KikoBrain b, string field, object value) => typeof(KikoBrain).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(b, value);
    static KikoBrain Brain() { var b = new KikoBrain { pet = new KikoPet(), talk = new KikoTalk() }; Field(b, "cfg", new BrainConfig { @base = "https://local.test", clientId = "test-id-only", clientSecret = "test-only-value" }); Call(b, "LoadAttention"); return b; }
    static void Main()
    {
        const string q = "q-1700000000000"; bool fail = false, quiet = false; int reads = 0, acks = 0; string chat = "{}";
        UnityWebRequest.Respond = r =>
        {
            Check(r.headers["X-Kiko-Desk"] == "1", "原生桌面身份");
            if (r.url.EndsWith("/said")) { acks++; return (fail ? 503 : 200, "{}"); }
            if (r.url.EndsWith("/chat")) return (200, chat);
            reads++; return (200, JsonUtility.ToJson(new AttentionRes { quiet = quiet, items = new[] { new AttentionItem { id = q, say = "瞬时变化率是什么？" } } }));
        };
        var b = Brain();
        b.talk.Chatty = 0; Call(b, "AttentionPoll"); Check(reads == 0, "安静不取问题"); b.talk.Chatty = 2;
        b.pet.Sleeping = true; Call(b, "AttentionPoll"); Check(reads == 0, "睡觉不取问题"); b.pet.Sleeping = false;
        Win32.ForegroundFullscreen = true; Call(b, "AttentionPoll"); Check(reads == 0, "全屏不取问题"); Win32.ForegroundFullscreen = false;
        Field(b, "typing", true); Call(b, "AttentionPoll"); Check(reads == 0, "输入中不打断"); Field(b, "typing", false);
        quiet = true; Call(b, "AttentionPoll"); Check(b.talk.count == 0, "网页安静"); quiet = false;
        Runner.Tick = () => b.pet.Sleeping = true; Call(b, "AttentionPoll"); Check(b.talk.count == 0, "请求途中变忙"); Runner.Tick = null; b.pet.Sleeping = false;
        fail = true; Call(b, "AttentionPoll"); Check(b.talk.count == 1 && acks == 1, "先显示再回执");
        b = Brain(); fail = false; Call(b, "AttentionPoll"); Check(acks == 2 && b.talk.count == 0, "重启只补回执不再播");
        Call(b, "AttentionPoll"); Check(acks == 2, "已确认不重复交");
        PlayerPrefs.Clear(); b = Brain(); Runner.Tick = () => b.talk.Hidden = true; Call(b, "AttentionPoll"); Check(b.talk.count == 0, "隐藏不显示"); Runner.Tick = null;
        PlayerPrefs.Clear(); b = Brain(); chat = JsonUtility.ToJson(new ChatRes { reply = "接着刚才的内容，瞬时变化率是什么？", offered = q }); b.Chat("继续学导数"); b.talk.Hide(); Call(b, "AttentionPoll"); Check(b.talk.count == 2, "聊天 offered 不另外播报");
        chat = JsonUtility.ToJson(new ChatRes { reply = "记下了。", answered = q }); b.Chat("差商的极限"); Check(b.talk.text.Contains("稍后批改"), "回答显示待批改，不编造分数");
        var state = new KikoAttentionState(); state.Remember(q, 1000, false); state.Remember(q, 5000, true); Check(state.receipts[0].at == 1000 && state.Pending().Length == 0, "回执不刷新时间");
        Check(!state.Cooling(1000 + 16 * 60000), "十五分钟后可继续"); state.Remember("bad", 2000, false); Check(state.receipts.Count == 1, "无效编号丢弃");
        Console.WriteLine("PASS: actual KikoBrain coroutine + receipt state: quiet/sleep/fullscreen/typing, in-flight suppression, display then ACK, offline restart, offered, answered.");
    }
}
