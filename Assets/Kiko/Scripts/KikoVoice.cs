// 希狐的声音：气泡里出现一句话，就把这句话送到本机的 GPT-SoVITS 语音服务合成出来播放，嘴跟着声音动。
//  · 语音服务：Tools/GptSovitsVoice/（特蕾莎做的 voice_bridge.py，http://127.0.0.1:9876/tts，只听本机）。没开 → 她照常冒气泡，只是不出声，过 20 秒再试。
//  · 新的一句会顶掉上一句（还在合成的取消，正在放的停下）——气泡换了，声音也跟着换。
//  · 括号里的动作描写、表情符号不念；念不出字的（「……」）不送。
//  · 短句存在 persistentDataPath/voice/ 里，下次同一句直接放，不用再合成（戳她的台词来回就那些）。
//  · 右键菜单「出声说话」开关，存 PlayerPrefs kiko.voice（默认开）。
// 本机 http 不走 UnityWebRequest（Unity 默认不让连 http），用 System.Net.Http。
using System;
using System.Collections;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace Kiko
{
    [RequireComponent(typeof(AudioSource))]
    public class KikoVoice : MonoBehaviour
    {
        public KikoPet pet;
        public KikoTalk talk;
        public string endpoint = "http://127.0.0.1:9876/tts";
        /// 她的台词是中文里夹一点日语（こんこん）和英文（Yank）→ 让语音服务自己分语种
        public string language = "多语种混合";
        public float volume = 0.9f;

        [Serializable] class Req { public string text; public string language; }
        static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

        AudioSource source;
        CancellationTokenSource cts;
        int gen;
        float retryAt;
        bool told;
        readonly float[] buf = new float[512];

        /// 现在嘴该张多大（0–1），KikoPet.UpdateFace 每帧读
        public float Mouth { get; private set; }
        public bool Speaking => source && source.isPlaying;
        public bool Enabled
        {
            get => PlayerPrefs.GetInt("kiko.voice", 1) == 1;
            set { PlayerPrefs.SetInt("kiko.voice", value ? 1 : 0); PlayerPrefs.Save(); retryAt = 0; told = false; if (!value) Stop(); }
        }

        void Awake()
        {
            source = GetComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0;
            if (!FindAnyObjectByType<AudioListener>()) (Camera.main ? Camera.main.gameObject : gameObject).AddComponent<AudioListener>();
            KikoPet.Log($"voice: {(Enabled ? "on" : "off")} → {endpoint}（{language}）");
        }

        // ---------- 念哪些字 ----------
        static readonly Regex Aside = new Regex(@"（[^（）]*）|\([^()]*\)|【[^【】]*】|\*[^*]*\*");
        static readonly Regex Noise = new Regex(@"[z]{2,}|[\uD800-\uDFFF☀-➿️‍]", RegexOptions.IgnoreCase);
        static readonly Regex Sayable = new Regex(@"[\p{L}\p{Nd}]");
        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = Aside.Replace(s, "");
            s = Noise.Replace(s, "");
            s = s.Replace("～", "").Replace("~", "").Replace("——", "，").Replace("\n", "。");
            s = s.Trim().TrimStart('…', '。', '，', '、', '！', '？', '.', ',', ' ');
            if (s.Length > 600) s = s.Substring(0, 600);
            return Sayable.IsMatch(s) ? s : "";
        }

        // ---------- 说 ----------
        public void Speak(string line)
        {
            Stop();
            if (!Enabled || !isActiveAndEnabled) return;
            var t = Clean(line);
            if (t.Length == 0) return;
            StartCoroutine(SpeakCo(t, gen));
        }

        public void Stop()
        {
            gen++;
            if (cts != null) { cts.Cancel(); cts = null; }
            if (source && source.isPlaying) source.Stop();
            Mouth = 0;
        }

        string CachePath(string t)
        {
            using var sha = SHA1.Create();
            var h = sha.ComputeHash(Encoding.UTF8.GetBytes(endpoint + "|" + language + "|" + t));
            return Path.Combine(Application.persistentDataPath, "voice", BitConverter.ToString(h, 0, 10).Replace("-", "") + ".wav");
        }

        IEnumerator SpeakCo(string t, int id)
        {
            byte[] wav = null;
            string file = CachePath(t);
            if (File.Exists(file))
            {
                try { wav = File.ReadAllBytes(file); } catch { }
            }
            if (wav == null)
            {
                if (Time.unscaledTime < retryAt) yield break;
                var mine = cts = new CancellationTokenSource();
                var body = new StringContent(JsonUtility.ToJson(new Req { text = t, language = language }), Encoding.UTF8, "application/json");
                float t0 = Time.realtimeSinceStartup;
                var task = http.PostAsync(endpoint, body, mine.Token);
                while (!task.IsCompleted) yield return null;
                body.Dispose();
                if (cts == mine) cts = null;
                mine.Dispose();
                if (task.IsCanceled || (id != gen && task.IsFaulted)) yield break; // 被新的一句顶掉了
                if (task.IsFaulted)
                {
                    Fail("连不上语音服务（Tools/GptSovitsVoice/Start-Voice.ps1 开了吗）：" + (task.Exception?.GetBaseException().Message ?? "?"));
                    yield break;
                }
                var res = task.Result;
                var read = res.Content.ReadAsByteArrayAsync();
                while (!read.IsCompleted) yield return null;
                bool ok = res.IsSuccessStatusCode && !read.IsFaulted;
                int code = (int)res.StatusCode;
                var data = read.IsFaulted ? null : read.Result;
                res.Dispose();
                if (!ok)
                {
                    string why = data != null ? Encoding.UTF8.GetString(data, 0, Mathf.Min(data.Length, 300)) : "";
                    // 400 = 这句话它念不了（不是服务坏了）；其它 = GPT-SoVITS 推理页没开之类
                    if (code == 400) KikoPet.Log($"voice: 这句念不了 {why}");
                    else Fail($"语音服务报错 {code} {why}");
                    yield break;
                }
                wav = data;
                told = false;
                KikoPet.Log($"voice: 合成 {t.Length} 字 {Time.realtimeSinceStartup - t0:F1}s {wav.Length / 1024}KB");
                if (t.Length <= 80)
                    try { Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllBytes(file, wav); } catch { }
                if (id != gen) yield break; // 合成好了但气泡已经换了：存下来，这次不放
            }
            var clip = ParseWav(wav);
            if (!clip) { KikoPet.Log("voice: 读不懂返回的 wav"); try { File.Delete(file); } catch { } yield break; }
            source.clip = clip;
            source.volume = volume;
            source.Play();
            if (talk) talk.Hold(clip.length + 0.6f); // 话没说完气泡别收
            float m = 0;
            while (id == gen && source.clip == clip && source.isPlaying)
            {
                source.GetOutputData(buf, 0);
                float sum = 0;
                for (int i = 0; i < buf.Length; i++) sum += buf[i] * buf[i];
                float target = Mathf.Clamp01(Mathf.Sqrt(sum / buf.Length) * 9f / Mathf.Max(0.05f, volume));
                m += (target - m) * (target > m ? 0.6f : 0.25f); // 张嘴快、合嘴慢一点
                Mouth = m;
                yield return null;
            }
            if (source.clip == clip) { source.Stop(); source.clip = null; Mouth = 0; }
            Destroy(clip);
        }

        void Fail(string why)
        {
            retryAt = Time.unscaledTime + 20f;
            if (!told) KikoPet.Log("voice: " + why + "（20 秒后再试；这期间只冒气泡）");
            told = true;
        }

        // ---------- wav → AudioClip（16/24/32 位整数、32 位浮点）----------
        static AudioClip ParseWav(byte[] b)
        {
            try
            {
                if (b == null || b.Length < 44 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || Encoding.ASCII.GetString(b, 8, 4) != "WAVE") return null;
                int pos = 12, fmt = 0, ch = 0, rate = 0, bits = 0, dataAt = -1, dataLen = 0;
                while (pos + 8 <= b.Length)
                {
                    string id = Encoding.ASCII.GetString(b, pos, 4);
                    long size = BitConverter.ToUInt32(b, pos + 4);
                    int at = pos + 8;
                    if (id == "fmt ")
                    {
                        fmt = BitConverter.ToUInt16(b, at); ch = BitConverter.ToUInt16(b, at + 2);
                        rate = BitConverter.ToInt32(b, at + 4); bits = BitConverter.ToUInt16(b, at + 14);
                        if (fmt == 0xFFFE && size >= 26) fmt = BitConverter.ToUInt16(b, at + 24); // WAVE_FORMAT_EXTENSIBLE
                    }
                    else if (id == "data")
                    {
                        dataAt = at;
                        dataLen = (int)Math.Min(size, b.Length - at); // 流式写的 wav 长度可能填的是 0xFFFFFFFF
                        break;
                    }
                    pos = at + (int)Math.Min(size + (size & 1), int.MaxValue - at);
                }
                int bytes = bits / 8;
                if (dataAt < 0 || ch < 1 || rate < 8000 || bytes < 2 || bytes > 4 || (fmt != 1 && fmt != 3)) return null;
                int n = dataLen / bytes;
                var f = new float[n];
                for (int i = 0, p = dataAt; i < n; i++, p += bytes)
                    f[i] = fmt == 3 ? BitConverter.ToSingle(b, p)
                        : bytes == 2 ? BitConverter.ToInt16(b, p) / 32768f
                        : bytes == 3 ? ((b[p] | (b[p + 1] << 8) | ((sbyte)b[p + 2] << 16)) / 8388608f)
                        : BitConverter.ToInt32(b, p) / 2147483648f;
                int frames = n / ch;
                if (frames < 1) return null;
                var clip = AudioClip.Create("kiko_voice", frames, ch, rate, false);
                clip.SetData(f, 0);
                return clip;
            }
            catch (Exception e) { KikoPet.Log("voice: wav " + e.Message); return null; }
        }

        void OnDisable() => Stop();
    }
}
