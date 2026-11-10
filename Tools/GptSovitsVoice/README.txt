2026-11-10: Kiko no longer needs anything in this folder to be started by hand.
KikoVoice.cs starts GPT-SoVITS api_v2.py itself (hidden, on 127.0.0.1:9880) when
she first needs to speak, stops it after 10 idle minutes and when she exits.
Settings: %APPDATA%\ZhixuFox\voice.json. Engine output: voice_engine.log next to
pet.log. Only reference.wav in this folder is still used (kept out of Git).
The WebUI bridge below (voice_bridge.py, start-voice.bat) is kept as a fallback
and for testing; Unity does not call it any more.

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
4. Nothing to add in Unity: KikoPet attaches KikoVoice at startup, and every
   speech bubble (KikoTalk.Say: local lines, chat replies, muse) is sent here.
   A new bubble replaces the previous voice. Stage directions in brackets and
   emoji are not spoken. Short lines are cached under persistentDataPath/voice.
   The mouth follows the audio level. Right-click menu: 出声说话 turns it off/on.
5. KikoVoice sends language 多语种混合 (Chinese lines with some Japanese and
   English). If this service is not running, she only shows bubbles and retries
   after 20 seconds; see pet.log lines starting with "voice:".

API
POST http://127.0.0.1:9876/tts
Content-Type: application/json
{"text":"おかえりなさい。","language":"日文"}
Returns WAV bytes. Requests are serialized; text is limited to 2000 characters.
GET /health only checks the adapter, not the upstream model service.
The server binds loopback only and rejects browser Origin headers. Use native
Unity networking or a later explicit local bridge, not direct website fetch.
The website-to-desktop transport and authentication remain to be designed.
KikoVoice uses System.Net.Http for this loopback endpoint, so Unity's
"Allow downloads over HTTP" setting is not involved. Do not expose these
services to the internet.

The model weights are the existing s1v3.ckpt + gsv-v4-pretrained/s2Gv4.pth.
No new model has been trained. Voice identity comes from the reference plus
transcript. Changing models in the WebUI while this adapter runs is unsupported.
32 sampling steps preserve the user's chosen settings; latency needs measurement
before enabling continuous chat. The Unity queue is bounded and plays sequentially.

Validation on the user's Windows machine:
- Python -> installed WebUI synthesis: passed, 3.56-second Japanese WAV.
- HTTP POST -> bridge -> WebUI -> WAV: passed.
- Unity compilation/playback with live chat replies: see Docs or pet.log.

Audio credit: つくよみちゃんコーパス Vol.1 / CV.夢前黎
Source and terms: https://tyc.rei-yumesaki.net/material/corpus/
Retain required attribution when distributing a speaking application.
