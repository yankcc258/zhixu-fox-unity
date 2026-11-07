"""Local Unity-to-GPT-SoVITS WebUI adapter. Run with the bundled Python."""
import argparse
import json
import threading
from pathlib import Path
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from gradio_client import Client, file

ROOT = Path(__file__).resolve().parent
PROFILE = json.loads((ROOT / 'voice-profile.json').read_text(encoding='utf-8-sig'))
LOCK = threading.Lock()
CLIENT = None

def synthesize(text, language='日文'):
    global CLIENT
    if not isinstance(text, str) or not text.strip() or len(text) > 2000:
        raise ValueError('Text must contain 1–2000 characters.')
    if language not in ('中文', '日文', '英文', '中英混合', '日英混合', '多语种混合'):
        raise ValueError('Unsupported language.')
    with LOCK:
        if CLIENT is None:
            CLIENT = Client('http://127.0.0.1:9872/', verbose=False)
            CLIENT.predict(gpt_path=PROFILE['gpt_model_path'], api_name='/change_gpt_weights')
            CLIENT.predict(sovits_path=PROFILE['sovits_model_path'], prompt_language='日文',
                           text_language=language, api_name='/change_sovits_weights')
        result = CLIENT.predict(
            ref_wav_path=file(str(ROOT / PROFILE['reference_audio_path'])),
            prompt_text=PROFILE['prompt_text'], prompt_language='日文',
            text=text, text_language=language, how_to_cut=PROFILE['webui_text_split_label'],
            top_k=PROFILE['top_k'], top_p=PROFILE['top_p'], temperature=PROFILE['temperature'],
            ref_free=False, speed=PROFILE['speed_factor'], if_freeze=False, inp_refs=None,
            sample_steps=PROFILE['sample_steps'], if_sr=False,
            pause_second=PROFILE['webui_sentence_pause_seconds'], api_name='/get_tts_wav')
        return Path(result).read_bytes()

class Handler(BaseHTTPRequestHandler):
    def reply(self, status, data, mime='application/json'):
        self.send_response(status)
        self.send_header('Content-Type', mime)
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path != '/health':
            self.reply(404, b'{}')
            return
        self.reply(200, b'{"status":"ready","upstream_checked":false}')

    def do_POST(self):
        if self.path != '/tts':
            self.reply(404, b'{}')
            return
        # No browser cross-origin requests; Unity native HTTP needs no Origin.
        if self.headers.get('Origin'):
            self.reply(403, b'{"error":"Browser origins are not accepted"}')
            return
        try:
            size = int(self.headers.get('Content-Length', '0'))
            if size < 1 or size > 20000:
                raise ValueError('Invalid body size')
            data = json.loads(self.rfile.read(size))
            if not isinstance(data, dict):
                raise ValueError('Expected JSON object')
            audio = synthesize(data.get('text'), data.get('language', '日文'))
            self.reply(200, audio, 'audio/wav')
        except (ValueError, TypeError) as error:
            self.reply(400, json.dumps({'error': str(error)}).encode())
        except Exception as error:
            self.reply(502, json.dumps({'error': str(error)}).encode())

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--test', action='store_true')
    args = parser.parse_args()
    if args.test:
        audio = synthesize('おかえりなさい。今日も一緒にお話ししましょう。')
        (ROOT / 'test-japanese.wav').write_bytes(audio)
        print('Saved test-japanese.wav:', len(audio), 'bytes')
    else:
        print('Zhixu voice: http://127.0.0.1:9876/tts', flush=True)
        ThreadingHTTPServer(('127.0.0.1', 9876), Handler).serve_forever()
