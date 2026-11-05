"""从 kiko.vrm 的身体贴图生成 Unity 版透肤丝袜用的两张图（Tauri 版是运行时着色器，Unity 版改成预混 + lilToon 边缘阴影）。
usage: python3 Tools/make_sheer.py Assets/Kiko/Models/kiko.vrm Assets/Kiko/Textures
  body_sheer.png      : 丝袜区域（贴图 alpha 0.5–0.85 的记号，paint_socks.py 画的）在 gamma 空间预混 38% 肤色，alpha 还原成 1
  body_sheer_mask.png : 丝袜区域白、其余黑（给 lilToon _RimShadeMask，只压暗丝袜侧面）
"""
import io, json, struct, sys
import numpy as np
from PIL import Image

src, outdir = sys.argv[1], sys.argv[2]
d = open(src, 'rb').read()
L = struct.unpack('<I', d[12:16])[0]
j = json.loads(d[20:20 + L])
BIN = d[20 + L + 8:]
m = next(m for m in j['materials'] if 'Body_00_SKIN' in m['name'])
img = j['images'][j['textures'][m['pbrMetallicRoughness']['baseColorTexture']['index']]['source']]
bv = j['bufferViews'][img['bufferView']]
o = bv.get('byteOffset', 0)
a = np.array(Image.open(io.BytesIO(BIN[o:o + bv['byteLength']])).convert('RGBA')).astype(np.float32) / 255
mask = (a[..., 3] >= 0.5) & (a[..., 3] <= 0.85)
skin = (np.array([0.95, 0.66, 0.57]) * 0.8) ** (1 / 2.2)  # 和 vrm.js TIGHTS.skin 一样
OP = 0.62  # 正面的丝袜浓度（TIGHTS.min）
out = a.copy()
out[mask, :3] = skin * (1 - OP) + a[mask, :3] * OP
out[mask, 3] = 1
Image.fromarray((np.clip(out, 0, 1) * 255).astype(np.uint8)).save(f'{outdir}/body_sheer.png')
mk = np.zeros(mask.shape, np.uint8)
mk[mask] = 255
Image.fromarray(mk).convert('RGB').save(f'{outdir}/body_sheer_mask.png')
print('sheer texels', int(mask.sum()))
