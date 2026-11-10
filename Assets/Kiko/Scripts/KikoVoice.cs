// 希狐的声音：气泡里出现一句话，就让本机的 GPT-SoVITS 合成出来播放，嘴跟着声音动。
//  · 语音引擎她自己管：要说话了才在后台悄悄启动 GPT-SoVITS 自带的 api_v2.py（没有窗口、不开网页），
//    一阵子没说话（默认 10 分钟）就关掉把显存还回去；她退出时一起关。不用手动开任何东西。
//  · 新的一句会顶掉上一句（还在合成的取消，正在放的停下）——气泡换了，声音也跟着换。
//  · 括号里的动作描写、表情符号不念；念不出字的（「……」）不送。
//  · 长的话按句子分开，合成好一句先放一句，不用等整段。
//  · 短句存在 persistentDataPath/voice/ 里，下次同一句直接放，不用引擎（戳她的台词来回就那些）。
//  · 有日语版的句子念日语、气泡还是中文：本地台词查 Resources/kiko_lines_ja.json，知序的回复看它带不带 ja 字段；没有日语版就照气泡念中文。
//  · 右键菜单「出声说话」开关，存 PlayerPrefs kiko.voice（默认开）；关掉时引擎也关。
// 设置：%APPDATA%\ZhixuFox\voice.json（第一次启动自动建）——GPT-SoVITS 装在哪、参考音频、参考文本、多久没说话就关引擎……
// 参考音频找的顺序：voice.json 的 refAudio → %APPDATA%\ZhixuFox\reference.wav → 工程里的 Tools/GptSovitsVoice/reference.wav。
// 引擎的输出记在 persistentDataPath/voice_engine.log；pet.log 里以 voice: 开头的是这边的事。
// 本机 http 不走 UnityWebRequest（Unity 默认不让连 http），用 System.Net.Http。
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace Kiko
{
    [Serializable]
    class VoiceConfig
    {
        public string gsvRoot = @"D:\GPT-SoVITS-v4-20250422fix\GPT-SoVITS-v4-20250422fix";
        public string refAudio = "";
        public string promptText = "また、東寺のように、五大明王と呼ばれる、主要な明王の中央に配されることも多い";
        public string promptLang = "ja";
        public string textLang = "auto"; // 中文台词里夹一点日语（こんこん）和英文（Yank）→ 让引擎自己分语种
        public bool japanese = true;     // 有日语版的句子念日语（气泡还是中文）；false = 全部照气泡念中文
        public int port = 9880;
        public float idleMinutes = 10;
        public int steps = 8;            // 采样步数：V4 推理页的默认就是 8；32 更细但慢三四倍
        public bool warmOnStart = true;  // 她一上桌面就先把引擎叫起来（false = 等到要说话才开）
        public int topK = 15;
        public float speed = 1f;
        public float volume = 0.9f;
    }

    [Serializable]
    class TtsReq
    {
        public string text, text_lang, ref_audio_path, prompt_lang, prompt_text;
        public int top_k = 15;
        public float top_p = 1f, temperature = 1f;
        public string text_split_method = "cut5";
        public int batch_size = 1;
        public float speed_factor = 1f;
        public int sample_steps = 32;
        public string media_type = "wav";
        public bool streaming_mode = false, parallel_infer = true;
        public float repetition_penalty = 1.35f, fragment_interval = 0.3f;
    }

    [RequireComponent(typeof(AudioSource))]
    public class KikoVoice : MonoBehaviour
    {
        public KikoPet pet;
        public KikoTalk talk;

        static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        VoiceConfig cfg = new VoiceConfig();
        AudioSource source;
        CancellationTokenSource cts;
        int gen;
        float retryAt;
        bool told;
        string dataDir, assetsDir, refPath;
        readonly float[] buf = new float[512];

        /// 现在嘴该张多大（0–1），KikoPet.UpdateFace 每帧读
        public float Mouth { get; private set; }
        public bool Speaking => source && source.isPlaying;
        public bool Japanese => cfg.japanese;
        public bool Enabled
        {
            get => PlayerPrefs.GetInt("kiko.voice", 1) == 1;
            set
            {
                PlayerPrefs.SetInt("kiko.voice", value ? 1 : 0); PlayerPrefs.Save();
                retryAt = 0; told = false;
                if (!value) { Stop(); StopEngine("关了声音"); }
            }
        }
        static string ConfigDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZhixuFox");
        string Base => "http://127.0.0.1:" + cfg.port;

        void Awake()
        {
            source = GetComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0;
            if (!FindAnyObjectByType<AudioListener>()) (Camera.main ? Camera.main.gameObject : gameObject).AddComponent<AudioListener>();
            dataDir = Application.persistentDataPath;
            assetsDir = Application.dataPath;
            LoadConfig();
            refPath = FindRef();
            KikoPet.Log($"voice: {(Enabled ? "on" : "off")}，GPT-SoVITS {cfg.gsvRoot}，参考音频 {refPath ?? "没找到"}，{cfg.idleMinutes} 分钟不说话关引擎");
        }

        void Start()
        {
            if (cfg.warmOnStart) Warm();
        }

        void LoadConfig()
        {
            try
            {
                var path = Path.Combine(ConfigDir, "voice.json");
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(ConfigDir);
                    File.WriteAllText(path, JsonUtility.ToJson(cfg, true), new UTF8Encoding(false));
                }
                string old = File.ReadAllText(path, Encoding.UTF8);
                cfg = JsonUtility.FromJson<VoiceConfig>(old) ?? new VoiceConfig();
                // 新加的设置项补进文件里（原来填的不动）
                string now = JsonUtility.ToJson(cfg, true);
                if (now != old) File.WriteAllText(path, now, new UTF8Encoding(false));
                cfg.gsvRoot = (cfg.gsvRoot ?? "").Trim().TrimEnd('\\', '/');
                if (cfg.port < 1024 || cfg.port > 65535) cfg.port = 9880;
                cfg.idleMinutes = Mathf.Max(1f, cfg.idleMinutes);
            }
            catch (Exception e) { cfg = new VoiceConfig(); KikoPet.Log("voice: 读 voice.json 出错 " + e.Message); }
        }

        string FindRef()
        {
            if (!string.IsNullOrWhiteSpace(cfg.refAudio) && File.Exists(cfg.refAudio)) return Path.GetFullPath(cfg.refAudio);
            var p = Path.Combine(ConfigDir, "reference.wav");
            if (File.Exists(p)) return p;
            // 工程里的 Tools/GptSovitsVoice（编辑器：Assets 的上一层；打包的 exe 在 Builds/ZhixuFox/ 里，再往上两层）
            var dir = new DirectoryInfo(assetsDir);
            for (int i = 0; i < 5 && dir != null; i++, dir = dir.Parent)
            {
                p = Path.Combine(dir.FullName, "Tools", "GptSovitsVoice", "reference.wav");
                if (File.Exists(p)) return p;
            }
            return null;
        }

        // ---------- 语音引擎（GPT-SoVITS api_v2.py，后台进程）----------
        enum Engine { Off, Starting, Ready }
        Engine es;
        System.Diagnostics.Process engine;
        float lastUsed;
        bool pingOk;
        readonly object logLock = new object();

        /// 快要说话了（比如打开了输入框）：先把引擎叫起来，等回复到的时候就不用等了
        public void Warm()
        {
            lastUsed = Time.unscaledTime;
            if (Enabled && es == Engine.Off && Time.unscaledTime >= retryAt) StartCoroutine(StartEngine());
        }

        IEnumerator PingCo()
        {
            var c = new CancellationTokenSource(2000);
            var t = http.GetAsync(Base + "/", c.Token);
            while (!t.IsCompleted) yield return null;
            pingOk = !t.IsFaulted && !t.IsCanceled; // 回什么都行（404 也算），有人应门就是起来了
            if (pingOk) t.Result.Dispose();
            c.Dispose();
        }

        IEnumerator StartEngine()
        {
            es = Engine.Starting;
            yield return PingCo();
            if (es != Engine.Starting) yield break;
            if (pingOk) { es = Engine.Ready; lastUsed = Time.unscaledTime; KikoPet.Log("voice: 语音引擎已经在跑，接着用"); yield break; }
            string py = Path.Combine(cfg.gsvRoot, "runtime", "python.exe");
            string err = !File.Exists(py) || !File.Exists(Path.Combine(cfg.gsvRoot, "api_v2.py")) ? "找不到 GPT-SoVITS（" + cfg.gsvRoot + "），改 voice.json 的 gsvRoot"
                : refPath == null ? "找不到参考音频 reference.wav" : null;
            if (err == null)
            {
                try
                {
                    string log = Path.Combine(dataDir, "voice_engine.log");
                    File.WriteAllText(log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " start\n");
                    var psi = new System.Diagnostics.ProcessStartInfo(py, $"-I -X utf8 api_v2.py -a 127.0.0.1 -p {cfg.port}")
                    {
                        WorkingDirectory = cfg.gsvRoot, UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                    };
                    var p = new System.Diagnostics.Process { StartInfo = psi };
                    System.Diagnostics.DataReceivedEventHandler onLine = (_, e) =>
                    {
                        if (e.Data == null) return;
                        lock (logLock) { try { File.AppendAllText(log, e.Data + "\n"); } catch { } }
                    };
                    p.OutputDataReceived += onLine;
                    p.ErrorDataReceived += onLine;
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    engine = p;
                }
                catch (Exception e) { err = "语音引擎启动不了：" + e.Message; }
            }
            if (err != null) { es = Engine.Off; Fail(err, 600f); yield break; }
            KikoPet.Log("voice: 启动语音引擎……");
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 180f)
            {
                yield return new WaitForSecondsRealtime(1f);
                if (es != Engine.Starting) yield break; // 等的时候被关掉了
                bool dead = true;
                try { dead = engine == null || engine.HasExited; } catch { }
                if (dead) { engine = null; es = Engine.Off; Fail("语音引擎自己退出了，看 voice_engine.log", 120f); yield break; }
                yield return PingCo();
                if (es != Engine.Starting) yield break;
                if (pingOk)
                {
                    // 热身：第一句要处理参考音频、显卡也是冷的，会多花三四秒 → 先让它念一句没人听的
                    var warm = new StringContent(JsonUtility.ToJson(MakeReq("こんこん。", "ja")), Encoding.UTF8, "application/json");
                    var wt = http.PostAsync(Base + "/tts", warm);
                    while (!wt.IsCompleted) yield return null;
                    if (!wt.IsFaulted && !wt.IsCanceled) wt.Result.Dispose();
                    warm.Dispose();
                    if (es != Engine.Starting) yield break;
                    es = Engine.Ready; lastUsed = Time.unscaledTime;
                    KikoPet.Log($"voice: 语音引擎好了（{Time.realtimeSinceStartup - t0:F0} 秒）");
                    yield break;
                }
            }
            StopEngine("三分钟都没起来");
            Fail("语音引擎三分钟都没起来，看 voice_engine.log", 600f);
        }

        void StopEngine(string why)
        {
            if (es == Engine.Off && engine == null) return;
            bool wasReady = es == Engine.Ready;
            es = Engine.Off;
            var p = engine; engine = null;
            if (p != null)
            {
                try { if (!p.HasExited) p.Kill(); } catch { }
                try { p.Dispose(); } catch { }
            }
            else if (wasReady)
            {
                // 不是这次启动的（上次没关干净留下的）：请它自己退出
                try { http.GetAsync(Base + "/control?command=exit").ContinueWith(t => { var _ = t.Exception; }); } catch { }
            }
            KikoPet.Log("voice: 语音引擎关了（" + why + "）");
        }

        void Update()
        {
            if (es == Engine.Ready && cts == null && !Speaking && Time.unscaledTime - lastUsed > cfg.idleMinutes * 60f)
                StopEngine(cfg.idleMinutes + " 分钟没说话，把显存还回去");
        }

        // ---------- 念哪些字 ----------
        static readonly Regex Aside = new Regex(@"（[^（）]*）|\([^()]*\)|【[^【】]*】|\*[^*]*\*");
        static readonly Regex Noise = new Regex(@"[z]{2,}|[\uD800-\uDFFF\u2600-\u27BF\uFE0F\u200D]", RegexOptions.IgnoreCase);
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
        /// lang：这句话的语种（"ja" = 日语里可以夹英文）；不给就用 voice.json 的 textLang
        public void Speak(string line, string lang = null)
        {
            Stop();
            if (!Enabled || !isActiveAndEnabled) return;
            var t = Clean(line);
            if (t.Length == 0) return;
            StartCoroutine(SpeakCo(Split(t), string.IsNullOrEmpty(lang) ? cfg.textLang : lang, gen));
        }

        public void Stop()
        {
            gen++;
            if (cts != null) { cts.Cancel(); cts = null; }
            if (source && source.isPlaying) source.Stop();
            Mouth = 0;
        }

        // 一句一句合成、合成好一句就先放一句：长回复不用等整段都好了才出声
        static readonly Regex SentenceEnd = new Regex(@"(?<=[。！？!?…]+)(?![。！？!?…])");
        static List<string> Split(string t)
        {
            var parts = new List<string>();
            string cur = "";
            foreach (var piece in SentenceEnd.Split(t))
            {
                cur += piece;
                if (cur.Length >= 8 && Sayable.IsMatch(cur)) { parts.Add(cur.Trim()); cur = ""; } // 太短的（「ふん。」）并到下一句
            }
            if (cur.Trim().Length > 0)
            {
                if (parts.Count > 0 && (cur.Length < 8 || !Sayable.IsMatch(cur))) parts[parts.Count - 1] += cur.Trim();
                else parts.Add(cur.Trim());
            }
            return parts;
        }

        string CachePath(string t, string lang)
        {
            using var sha = SHA1.Create();
            var h = sha.ComputeHash(Encoding.UTF8.GetBytes($"{cfg.promptText}|{lang}|{cfg.steps}|{cfg.topK}|{cfg.speed}|{t}"));
            return Path.Combine(dataDir, "voice", BitConverter.ToString(h, 0, 10).Replace("-", "") + ".wav");
        }

        TtsReq MakeReq(string t, string lang) => new TtsReq
        {
            text = t, text_lang = lang, ref_audio_path = refPath, prompt_lang = cfg.promptLang, prompt_text = cfg.promptText,
            top_k = cfg.topK, speed_factor = cfg.speed, sample_steps = cfg.steps,
            // 已经按句子分好了，短的不让引擎再按逗号切（切碎了每段都有固定开销，停顿也怪）；很长的一句才让它自己切
            text_split_method = t.Length <= 60 ? "cut0" : "cut5",
        };

        class Job { public int id; public bool done, abort; public readonly Queue<AudioClip> clips = new Queue<AudioClip>(); }

        IEnumerator SpeakCo(List<string> parts, string lang, int id)
        {
            var job = new Job { id = id };
            float asked = Time.realtimeSinceStartup;
            bool started = false;
            StartCoroutine(Fetch(job, parts, lang));
            while (id == gen)
            {
                if (job.clips.Count > 0)
                {
                    // 等太久了（引擎刚启动）：已经存下来了，这次不放
                    if (!started && Time.realtimeSinceStartup - asked > 45f) break;
                    started = true;
                    var clip = job.clips.Dequeue();
                    source.clip = clip;
                    source.volume = cfg.volume;
                    source.Play();
                    if (talk) talk.Hold(clip.length + 0.6f); // 话没说完气泡别收
                    float m = Mouth;
                    while (id == gen && source.clip == clip && source.isPlaying)
                    {
                        source.GetOutputData(buf, 0);
                        float sum = 0;
                        for (int i = 0; i < buf.Length; i++) sum += buf[i] * buf[i];
                        float target = Mathf.Clamp01(Mathf.Sqrt(sum / buf.Length) * 9f / Mathf.Max(0.05f, cfg.volume));
                        m += (target - m) * (target > m ? 0.6f : 0.25f); // 张嘴快、合嘴慢一点
                        Mouth = m;
                        yield return null;
                    }
                    if (source.clip == clip) { source.Stop(); source.clip = null; Mouth = 0; }
                    Destroy(clip);
                }
                else if (job.done) break;
                else
                {
                    if (started && talk) talk.Hold(1f); // 下一句还在合成：气泡先留着
                    yield return null;
                }
            }
            job.abort = true;
            while (job.clips.Count > 0) Destroy(job.clips.Dequeue());
        }

        // 按顺序把每一句变成 AudioClip 放进 job.clips（存过的直接读，没存过的找引擎合成）
        IEnumerator Fetch(Job job, List<string> parts, string lang)
        {
            foreach (var t in parts)
            {
                if (job.id != gen || job.abort) break;
                byte[] wav = null;
                string file = CachePath(t, lang);
                if (File.Exists(file))
                {
                    try { wav = File.ReadAllBytes(file); } catch { }
                }
                if (wav == null)
                {
                    if (Time.unscaledTime < retryAt) break;
                    // 引擎没开就开（要等它把模型装进显卡）
                    lastUsed = Time.unscaledTime;
                    if (es == Engine.Off) StartCoroutine(StartEngine());
                    while (es == Engine.Starting && job.id == gen) yield return null;
                    if (es != Engine.Ready || job.id != gen || job.abort) break;

                    var mine = cts = new CancellationTokenSource();
                    var body = new StringContent(JsonUtility.ToJson(MakeReq(t, lang)), Encoding.UTF8, "application/json");
                    float t0 = Time.realtimeSinceStartup;
                    var task = http.PostAsync(Base + "/tts", body, mine.Token);
                    while (!task.IsCompleted) yield return null;
                    body.Dispose();
                    if (cts == mine) cts = null;
                    mine.Dispose();
                    lastUsed = Time.unscaledTime;
                    if (task.IsCanceled || (job.id != gen && task.IsFaulted)) break; // 被新的一句顶掉了
                    if (task.IsFaulted)
                    {
                        // 引擎没了（崩了 / 被别人关了）：下次再说话时重新开
                        StopEngine("连不上");
                        Fail("连不上语音引擎：" + (task.Exception?.GetBaseException().Message ?? "?"), 20f);
                        break;
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
                        string why = data != null ? Encoding.UTF8.GetString(data, 0, Mathf.Min(data.Length, 400)) : "";
                        KikoPet.Log($"voice: 这句没念出来 {code} {why}");
                        continue;
                    }
                    wav = data;
                    told = false;
                    KikoPet.Log($"voice: 合成 {lang} {t.Length} 字 {Time.realtimeSinceStartup - t0:F1}s {wav.Length / 1024}KB");
                    if (t.Length <= 80)
                        try { Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllBytes(file, wav); } catch { }
                    if (job.id != gen || job.abort) break; // 合成好了但气泡已经换了：存下来，这次不放
                }
                var clip = ParseWav(wav);
                if (!clip) { KikoPet.Log("voice: 读不懂返回的 wav"); try { File.Delete(file); } catch { } continue; }
                job.clips.Enqueue(clip);
            }
            job.done = true;
        }

        void Fail(string why, float wait)
        {
            retryAt = Time.unscaledTime + wait;
            if (!told) KikoPet.Log($"voice: {why}（{wait:F0} 秒后再试；这期间只冒气泡）");
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
                int frames = dataLen / bytes / ch, n = frames * ch;
                if (frames < 1) return null;
                var f = new float[n];
                for (int i = 0, p = dataAt; i < n; i++, p += bytes)
                    f[i] = fmt == 3 ? BitConverter.ToSingle(b, p)
                        : bytes == 2 ? BitConverter.ToInt16(b, p) / 32768f
                        : bytes == 3 ? ((b[p] | (b[p + 1] << 8) | ((sbyte)b[p + 2] << 16)) / 8388608f)
                        : BitConverter.ToInt32(b, p) / 2147483648f;
                var clip = AudioClip.Create("kiko_voice", frames, ch, rate, false);
                clip.SetData(f, 0);
                return clip;
            }
            catch (Exception e) { KikoPet.Log("voice: wav " + e.Message); return null; }
        }

        void OnDisable() => Stop();
        void OnDestroy() => StopEngine("她退出了");
        void OnApplicationQuit() => StopEngine("她退出了");
    }
}
