using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace Kiko
{
    // Attach to a scene object. Call Speak(replyText) after a chatbot reply.
    [RequireComponent(typeof(AudioSource))]
    public class KikoVoice : MonoBehaviour
    {
        public string endpoint = "http://127.0.0.1:9876/tts";
        public string language = "日文";
        public event Action<bool> SpeakingChanged;
        readonly Queue<string> pending = new Queue<string>();
        AudioSource source;
        Coroutine worker;
        UnityWebRequest activeRequest;
        [Serializable] class Request { public string text; public string language; }

        void Awake() { source = GetComponent<AudioSource>(); source.playOnAwake = false; }

        public void Speak(string text)
        {
            if (!isActiveAndEnabled || string.IsNullOrWhiteSpace(text)) return;
            if (pending.Count >= 8) { Debug.LogWarning("Voice queue full"); return; }
            pending.Enqueue(text);
            if (worker == null) worker = StartCoroutine(PlayQueue());
        }

        IEnumerator PlayQueue()
        {
            while (pending.Count > 0)
            {
                var body = JsonUtility.ToJson(new Request { text = pending.Dequeue(), language = language });
                using (var request = new UnityWebRequest(endpoint, "POST"))
                {
                    activeRequest = request;
                    request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body));
                    request.downloadHandler = new DownloadHandlerAudioClip(endpoint, AudioType.WAV);
                    request.SetRequestHeader("Content-Type", "application/json");
                    request.timeout = 180;
                    yield return request.SendWebRequest();
                    activeRequest = null;
                    if (request.result != UnityWebRequest.Result.Success)
                    { Debug.LogWarning("Voice failed: " + request.error); continue; }
                    var clip = DownloadHandlerAudioClip.GetContent(request);
                    source.clip = clip;
                    source.Play();
                    SpeakingChanged?.Invoke(true);
                    while (source.isPlaying) yield return null;
                    SpeakingChanged?.Invoke(false);
                    source.clip = null;
                    Destroy(clip);
                }
            }
            worker = null;
        }

        public void StopSpeaking()
        {
            pending.Clear();
            activeRequest?.Abort();
            if (worker != null) StopCoroutine(worker);
            worker = null;
            activeRequest = null;
            if (source != null)
            {
                source.Stop();
                if (source.clip != null) Destroy(source.clip);
                source.clip = null;
            }
            SpeakingChanged?.Invoke(false);
        }
        void OnDisable() { StopSpeaking(); }
        [ContextMenu("Test Japanese voice")]
        void TestVoice() { Speak("おかえりなさい。今日も一緒にお話ししましょう。"); }
    }
}
