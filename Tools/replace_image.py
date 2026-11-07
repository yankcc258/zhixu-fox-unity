"""把 VRM 里某个材质的底色贴图换成一张新 PNG（尺寸可以不同，UV 不变）。MToon 的阴影贴图如果指向同一张图也跟着换。
usage: python3 Tools/replace_image.py in.vrm out.vrm <材质名里的一段，如 FoxTail> new.png
"""
import json, struct, sys

src, dst, key, png = sys.argv[1:5]
d = open(src, 'rb').read()
L = struct.unpack('<I', d[12:16])[0]
j = json.loads(d[20:20 + L])
b0 = 20 + L
BIN = bytearray(d[b0 + 8:b0 + 8 + struct.unpack('<I', d[b0:b0 + 4])[0]])
mats = [m for m in j['materials'] if key in m['name']]
assert mats, f'没有名字含 {key} 的材质'
imgs = {j['textures'][m['pbrMetallicRoughness']['baseColorTexture']['index']]['source'] for m in mats}
data = open(png, 'rb').read()
while len(BIN) % 4: BIN.append(0)
off = len(BIN); BIN += data
j['bufferViews'].append({'buffer': 0, 'byteOffset': off, 'byteLength': len(data)})
for im in imgs:
    j['images'][im]['bufferView'] = len(j['bufferViews']) - 1
    j['images'][im]['mimeType'] = 'image/png'
    print('replaced image', im, j['images'][im].get('name'), 'for', [m['name'] for m in mats])
while len(BIN) % 4: BIN.append(0)
j['buffers'][0]['byteLength'] = len(BIN)
js = json.dumps(j, ensure_ascii=False, separators=(',', ':')).encode()
while len(js) % 4: js += b' '
out = struct.pack('<III', 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(BIN)) + struct.pack('<II', len(js), 0x4E4F534A) + js + struct.pack('<II', len(BIN), 0x004E4942) + bytes(BIN)
open(dst, 'wb').write(out)
print(dst, len(out) // 1024, 'KB (旧图的数据还留在 BIN 里没删，体积会大一点)')
