"""把 kiko.vrm 里的图片原样导出到 Assets/Kiko/Textures/vrm/（NN_原名.png）+ index.json，
KikoSetup 换材质时把 VRM 子资源贴图（不压缩）换成这些外部 PNG（BC7 压缩）。换了 kiko.vrm 就重跑一次。
usage: python3 Tools/extract_textures.py Assets/Kiko/Models/kiko.vrm Assets/Kiko/Textures/vrm
"""
import io, json, os, struct, sys
from PIL import Image

src, outdir = sys.argv[1], sys.argv[2]
d = open(src, 'rb').read()
L = struct.unpack('<I', d[12:16])[0]
j = json.loads(d[20:20 + L])
BIN = d[20 + L + 8:]
normal = {j['textures'][m['normalTexture']['index']]['source'] for m in j['materials'] if m.get('normalTexture')}
os.makedirs(outdir, exist_ok=True)
items = []
for i, img in enumerate(j['images']):
    name = img.get('name') or f'img{i}'
    if name == 'Thumbnail':
        continue
    bv = j['bufferViews'][img['bufferView']]
    o = bv.get('byteOffset', 0)
    data = BIN[o:o + bv['byteLength']]
    w, h = Image.open(io.BytesIO(data)).size
    fn = f'{i:02d}_{name}.png'
    open(os.path.join(outdir, fn), 'wb').write(data)
    items.append({'file': fn, 'name': name, 'w': w, 'h': h, 'normal': i in normal})
json.dump({'items': items}, open(os.path.join(outdir, 'index.json'), 'w'), ensure_ascii=False, indent=1)
print(len(items), 'images')
