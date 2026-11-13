// 仅供无 Unity 的协议/协程测试，不进入 Assets，不模拟 Windows 渲染或声音。
using System;
using System.Collections;
using System.Collections.Generic;
namespace UnityEngine
{
    public class Object { public static implicit operator bool(Object x) => x != null; }
    public class MonoBehaviour : Object { public void StartCoroutine(IEnumerator e) => Runner.Run(e); }
    public static class Runner { public static Action Tick; public static void Run(IEnumerator e) { while (e.MoveNext()) { if (e.Current is IEnumerator child) Run(child); else Tick?.Invoke(); } } }
    public class WaitForEndOfFrame { }
    public static class Time { public static float time = 20, unscaledTime = 20; }
    public static class Application { public static bool isFocused = true; }
    public static class PlayerPrefs { static Dictionary<string, string> data = new(); public static string GetString(string k, string d) => data.TryGetValue(k, out var v) ? v : d; public static void SetString(string k, string v) => data[k] = v; public static void Save() { } public static void Clear() => data.Clear(); }
    public static class JsonUtility { static System.Text.Json.JsonSerializerOptions opts = new() { IncludeFields = true }; public static T FromJson<T>(string s) => System.Text.Json.JsonSerializer.Deserialize<T>(s, opts); public static string ToJson(object o, bool pretty = false) => System.Text.Json.JsonSerializer.Serialize(o, opts); }
    public static class Mathf { public static float Clamp(float x, float a, float b) => Math.Clamp(x, a, b); public static float Max(float a, float b) => Math.Max(a, b); public static float Min(float a, float b) => Math.Min(a, b); public static int RoundToInt(float a) => (int)Math.Round(a); public static float Round(float a) => (float)Math.Round(a); }
    public static class Input { public static IMECompositionMode imeCompositionMode; public static string compositionString = ""; }
    public enum IMECompositionMode { Auto, On }
    public static class Screen { public static int width = 300; }
    public class Texture2D { public Texture2D(int x, int y) { } public void SetPixel(int x, int y, Color c) { } public void Apply() { } }
    public struct Color { public Color(float a, float b, float c, float d = 1) { } }
    public class GUIStyleState { public object background; public Color textColor; }
    public class GUIStyle { public GUIStyleState normal = new(), focused = new(); public int fontSize; public GUIStyle() { } public GUIStyle(GUIStyle x) { } }
    public class GUISkin { public GUIStyle box = new(), textField = new(), label = new(); }
    public static class GUI { public static GUISkin skin = new(); public static void Box(Rect r, GUIContent c, GUIStyle s) { } public static void SetNextControlName(string s) { } public static string TextField(Rect r, string s, int n, GUIStyle g) => s; public static void FocusControl(string s) { } public static void Label(Rect r, string s, GUIStyle g) { } }
    public struct Rect { public Rect(float x, float y, float w, float h) { } }
    public class GUIContent { public static GUIContent none = new(); }
    public class Event { public static Event current = new(); public EventType type; public KeyCode keyCode; public void Use() { } }
    public enum EventType { KeyDown }
    public enum KeyCode { Return, KeypadEnter, Escape }
}
namespace UnityEngine.Networking
{
    public class DownloadHandlerBuffer { public string text; }
    public class UploadHandlerRaw { public byte[] data; public UploadHandlerRaw(byte[] d) { data = d; } }
    public class UnityWebRequest : IDisposable
    {
        public enum Result { Success, ConnectionError }
        public static Func<UnityWebRequest, (long code, string body)> Respond;
        public string url, method, error; public int timeout; public long responseCode; public Result result;
        public DownloadHandlerBuffer downloadHandler; public UploadHandlerRaw uploadHandler;
        public Dictionary<string, string> headers = new();
        public UnityWebRequest(string u, string m) { url = u; method = m; }
        public void SetRequestHeader(string k, string v) => headers[k] = v;
        public object SendWebRequest() { var r = Respond(this); responseCode = r.code; downloadHandler.text = r.body; result = r.code >= 200 && r.code < 300 ? Result.Success : Result.ConnectionError; return null; }
        public void Dispose() { }
    }
}
namespace Kiko
{
    public class KikoPet : UnityEngine.Object { public bool CanChat = true, Sleeping, Ready = true; public KikoVoice Voice; public float UiScale = 1, HeadTopGui = 60; public static void Log(string s) { } }
    public class KikoTalk : UnityEngine.Object { public bool Hidden, Showing; public int Chatty = 2, count; public string text; public void Say(string s, string mood, float sec, float delay = 0, string voice = null) { text = s; Showing = true; count++; } public bool IsShowing(string s) => Showing && !Hidden && text == s; public void Hide() { Showing = false; } public void Chat(bool force) { } }
    public class KikoVoice : UnityEngine.Object { public bool Speaking; public void Warm() { } }
    public static class Win32 { public static bool ForegroundFullscreen; public static void Focus() { } }
}
