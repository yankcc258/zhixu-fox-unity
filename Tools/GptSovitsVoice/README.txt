GPT-SoVITS voice for Kiko (Unity integration groundwork)

This provides a local voice service and a Unity AudioSource component.
It does not yet connect the Zhixu website/DeepSeek conversation to Unity,
add chat UI, or drive mouth expressions. Unity runtime verification is pending.

Setup
1. Keep the GPT-SoVITS v4 WebUI running on 127.0.0.1:9872.
2. Put the preserved reference.wav next to voice-profile.json in this directory.
   The audio is supplied in the local zhixu-voice package, not this Git branch.
   The exact reference is 001 東寺頭高型ver12dB.wav, 7.077156 seconds,
   SHA256 4944D4726EE9FF98501F39067430F66A42024EF7E518551A917B79F78AE414AB.
3. Run Start-Voice.ps1 -GptSovitsRoot <installation-directory>.
   Dependencies come from the installation's bundled Python (gradio_client 0.14.0
   was tested). This adapter targets the installed 20250422fix WebUI API.
4. Add KikoVoice to a scene object; AudioSource is added automatically.
   In Unity Play mode, use its component menu: Test Japanese voice.
5. Once the chat integration exists, call Speak(replyText) after a completed reply.
   Set language to 日文 for Japanese or 中文 for Chinese. DeepSeek must supply the
   matching language; this service does not translate replies.
   Subscribe to SpeakingChanged for future animation integration.
   Call StopSpeaking when interrupting a reply.

API
POST http://127.0.0.1:9876/tts
Content-Type: application/json
{"text":"おかえりなさい。","language":"日文"}
Returns WAV bytes. Requests are serialized; text is limited to 2000 characters.
GET /health only checks the adapter, not the upstream model service.
The server binds loopback only and rejects browser Origin headers. Use native
Unity networking or a later explicit local bridge, not direct website fetch.
The website-to-desktop transport and authentication remain to be designed.
For a localhost HTTP development endpoint, Unity may require Allow downloads
over HTTP under Player Settings. Do not expose these services to the internet.

The model weights are the existing s1v3.ckpt + gsv-v4-pretrained/s2Gv4.pth.
No new model has been trained. Voice identity comes from the reference plus
transcript. Changing models in the WebUI while this adapter runs is unsupported.
32 sampling steps preserve the user's chosen settings; latency needs measurement
before enabling continuous chat. The Unity queue is bounded and plays sequentially.

Validation on the user's Windows machine:
- Python -> installed WebUI synthesis: passed, 3.56-second Japanese WAV.
- HTTP POST -> bridge -> WebUI -> WAV: passed.
- Unity compilation/playback and live DeepSeek integration: not yet tested.

Audio credit: つくよみちゃんコーパス Vol.1 / CV.夢前黎
Source and terms: https://tyc.rei-yumesaki.net/material/corpus/
Retain required attribution when distributing a speaking application.
