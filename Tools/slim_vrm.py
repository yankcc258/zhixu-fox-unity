"""把买来的 VRM 里的贴图缩小（4096 → 2048，法线/遮罩 → 1024），别的原样不动。
UniVRM 导入的贴图是不压缩的 RGBA32：4096² 一张 85MB 内存，11 张就 900MB；桌宠只有几百像素高，2048 足够。
usage: python3 Tools/slim_vrm.py in.vrm out.vrm [--main 2048] [--aux 1024]
"""
import argparse, io, json, struct
from PIL import Image

ap = argparse.ArgumentParser()
ap.add_argument('src'); ap.add_argument('dst')
ap.add_argument('--main', type=int, default=2048)
ap.add_argument('--aux', type=int, default=1024)
a = ap.parse_args()

d = open(a.src, 'rb').read()
L = struct.unpack('<I', d[12:16])[0]
j = json.loads(d[20:20 + L])
b0 = 20 + L
BIN = d[b0 + 8:b0 + 8 + struct.unpack('<I', d[b0:b0 + 4])[0]]

img_of = {im['bufferView']: im for im in j['images'] if 'bufferView' in im}
out = bytearray()
for i, v in enumerate(j['bufferViews']):
    raw = BIN[v.get('byteOffset', 0):v.get('byteOffset', 0) + v['byteLength']]
    if i in img_of:
        im = img_of[i]
        name = (im.get('name') or '').lower()
        cap = a.aux if any(k in name for k in ('normal', 'mask', 'emi')) else a.main
        pic = Image.open(io.BytesIO(raw))
        if max(pic.size) > cap:
            before = pic.size
            pic = pic.resize((max(1, pic.width * cap // max(before)), max(1, pic.height * cap // max(before))), Image.LANCZOS)
            buf = io.BytesIO(); pic.save(buf, 'PNG', optimize=True); raw = buf.getvalue()
            im['mimeType'] = 'image/png'
            print(f"  {im.get('name')}: {before} → {pic.size}, {len(raw) // 1024} KB")
    while len(out) % 4: out += b'\0'
    v['byteOffset'] = len(out); v['byteLength'] = len(raw)
    out += raw
while len(out) % 4: out += b'\0'
j['buffers'][0]['byteLength'] = len(out)
js = json.dumps(j, ensure_ascii=False, separators=(',', ':')).encode()
while len(js) % 4: js += b' '
res = struct.pack('<III', 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(out)) + struct.pack('<II', len(js), 0x4E4F534A) + js + struct.pack('<II', len(out), 0x004E4942) + bytes(out)
open(a.dst, 'wb').write(res)
print(a.dst, len(res) // 1048576, 'MB')
