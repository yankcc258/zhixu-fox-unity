"""马努卡 → 希狐配色：把 MANUKA.vrm 里的贴图导出成外部 PNG（+ index.json，KikoSetup 换材质时用），顺手改色：
  · 头发 / 兽耳外侧 / 尾巴：米棕 → 银白（耳朵里面的粉色留着；尾尖的深棕提亮成浅灰白）
  · 眼睛：青绿 → 蓝
原模型不动；输出只在本机的 Assets/LocalModels/Textures/（不进 git）。
usage: python3 Tools/manuka_to_kiko.py MANUKA.vrm outdir
"""
import io, json, os, struct, sys
import numpy as np
from PIL import Image

src, outdir = sys.argv[1], sys.argv[2]
f = open(src, 'rb'); h = f.read(20); L = struct.unpack('<I', h[12:16])[0]; j = json.loads(f.read(L)); f.read(8); base = f.tell()
os.makedirs(outdir, exist_ok=True)


def hsv(a):
    r, g, b = a[..., 0], a[..., 1], a[..., 2]
    mx, mn = a[..., :3].max(-1), a[..., :3].min(-1)
    d = mx - mn
    hh = np.zeros_like(mx)
    m = d > 1e-6
    rc = (mx == r) & m; gc = (mx == g) & m & ~rc; bc = m & ~rc & ~gc
    hh[rc] = ((g - b)[rc] / d[rc]) % 6
    hh[gc] = (b - r)[gc] / d[gc] + 2
    hh[bc] = (r - g)[bc] / d[bc] + 4
    return hh * 60, np.where(mx > 1e-6, d / np.maximum(mx, 1e-6), 0), mx


def from_hsv(hh, s, v):
    c = v * s; x = c * (1 - np.abs((hh / 60) % 2 - 1)); m = v - c
    z = np.zeros_like(c); k = (hh // 60).astype(int) % 6
    r = np.choose(k, [c, x, z, z, x, c]); g = np.choose(k, [x, c, c, x, z, z]); b = np.choose(k, [z, z, x, c, c, x])
    return np.stack([r + m, g + m, b + m], -1)


def white_hair(a):
    """米棕 → 银白；粉色（耳朵里面）不动"""
    hh, s, v = hsv(a)
    lum = a[..., 0] * 0.299 + a[..., 1] * 0.587 + a[..., 2] * 0.114
    pink = ((hh < 12) | (hh > 335)) & (s > 0.28) & (v > 0.6)
    val = 0.60 + 0.40 * np.clip(lum, 0, 1) ** 0.85
    cool = np.array([0.86, 0.89, 0.98])
    t = np.clip((1 - val) * 1.8, 0, 1)[..., None]
    rgb = val[..., None] * ((1 - t) + t * cool)
    out = a.copy()
    out[..., :3] = np.where(pink[..., None], a[..., :3], rgb)
    return out


def kiko_face(a):
    """眼睛青绿 → 蓝"""
    hh, s, v = hsv(a)
    H, W = v.shape
    out = a.copy()
    teal = (hh > 140) & (hh < 215) & (s > 0.12)
    nh = np.where(teal, 205 + (hh - 140) * 0.35, hh)       # 青绿 140–215° → 蓝 205–231°
    ns = np.where(teal, np.clip(s * 1.25, 0, 1), s)
    nv = v  # 眉毛睫毛先保持原来的深棕（白发配深色睫毛没问题；以后想改再在 PSD 里分层改）
    out[..., :3] = from_hsv(nh, ns, nv)
    return out


items = []
for i, im in enumerate(j['images']):
    name = im.get('name') or f'img{i}'
    bv = j['bufferViews'][im['bufferView']]
    f.seek(base + bv.get('byteOffset', 0)); raw = f.read(bv['byteLength'])
    pic = Image.open(io.BytesIO(raw))
    if name == '1':  # 商品图，模型没用到
        continue
    fn = f'{i:02d}_{name}.png'
    edit = white_hair if name == 'Manuka_hair' else kiko_face if name in ('Manuka_face', 'Manuka_face_2') else None
    if edit:
        a = np.asarray(pic.convert('RGBA')).astype(np.float32) / 255
        a = edit(a)
        Image.fromarray((np.clip(a, 0, 1) * 255 + 0.5).astype(np.uint8), 'RGBA').save(os.path.join(outdir, fn))
    else:
        open(os.path.join(outdir, fn), 'wb').write(raw)
    items.append({'file': fn, 'name': name, 'w': pic.width, 'h': pic.height, 'normal': 'normal' in name.lower()})
json.dump({'items': items}, open(os.path.join(outdir, 'index.json'), 'w'), ensure_ascii=False, indent=1)
print(len(items), 'images →', outdir)
