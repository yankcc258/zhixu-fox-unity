"""吊带袜：在 body_sheer.png 上画吊带（主人 11-06：丝袜改成吊带袜）。先跑 make_sheer.py 再跑这个。
usage: python3 Tools/make_garter.py Assets/Kiko/Models/kiko.vrm Assets/Kiko/Textures/body_sheer.png [--top 0.46]

做法和 paint_socks.py 一样：把身体网格按 UV 栅格化，每个像素插值出静止姿势下的 3D 位置，
在大腿上（袜口到腰）画吊带：每条腿正面一条、外侧一条，袜口处一个银色小扣（吊带夹）。
VRM 1.0 模型朝 +Z；左腿在 x>0。
"""
import argparse, json, struct
import numpy as np
from PIL import Image

ap = argparse.ArgumentParser()
ap.add_argument('vrm'); ap.add_argument('tex')
ap.add_argument('--top', type=float, default=0.46, help='袜口高度（米），和 paint_socks 的 --top 一样')
ap.add_argument('--hips-top', type=float, default=0.64, help='吊带画到多高（裙子里面，再往上是安全裤）')
ap.add_argument('--width', type=float, default=0.012, help='吊带宽（米）')
ap.add_argument('--front', type=float, default=0.30, help='正面那条往外侧偏多少（弧度）')
a = ap.parse_args()

d = open(a.vrm, 'rb').read(); L = struct.unpack('<I', d[12:16])[0]; j = json.loads(d[20:20 + L])
b0 = 20 + L; BL = struct.unpack('<I', d[b0:b0 + 4])[0]; BIN = d[b0 + 8:b0 + 8 + BL]


def acc(i):
    A = j['accessors'][i]; v = j['bufferViews'][A['bufferView']]; off = v.get('byteOffset', 0) + A.get('byteOffset', 0)
    n = {'VEC2': 2, 'VEC3': 3, 'SCALAR': 1}[A['type']]
    dt = {5126: np.float32, 5123: np.uint16, 5125: np.uint32}[A['componentType']]
    return np.frombuffer(BIN[off:off + A['count'] * n * np.dtype(dt).itemsize], dt).reshape(A['count'], n)


img = Image.open(a.tex).convert('RGBA'); W, H = img.size
px = np.array(img).astype(np.float32)
P3 = np.full((H, W, 3), np.nan, np.float32)  # 每个像素的静止 3D 位置
LEG_X, LEG_Z = 0.07, 0.005

mi = [i for i, m in enumerate(j['materials']) if 'Body_00_SKIN' in m['name']][0]
for mesh in j['meshes']:
    for p in mesh['primitives']:
        if p['material'] != mi: continue
        uv = acc(p['attributes']['TEXCOORD_0']).astype(np.float64)
        pos = acc(p['attributes']['POSITION']).astype(np.float64)
        tri = acc(p['indices']).reshape(-1, 3)
        for t in tri:
            if pos[t, 1].max() > a.hips_top + 0.03 or pos[t, 1].min() < a.top - 0.06: continue  # 只要大腿
            P = uv[t] * [W, H]
            x0, y0 = np.floor(P.min(0)).astype(int); x1, y1 = np.ceil(P.max(0)).astype(int)
            x0 = max(x0, 0); y0 = max(y0, 0); x1 = min(x1, W - 1); y1 = min(y1, H - 1)
            if x1 < x0 or y1 < y0: continue
            gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
            (ax, ay), (bx, by), (cx, cy) = P
            den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy)
            if abs(den) < 1e-12: continue
            w1 = ((by - cy) * (gx - cx) + (cx - bx) * (gy - cy)) / den
            w2 = ((cy - ay) * (gx - cx) + (ax - cx) * (gy - cy)) / den
            w3 = 1 - w1 - w2
            inside = (w1 >= -0.02) & (w2 >= -0.02) & (w3 >= -0.02)
            q = w1[..., None] * pos[t[0]] + w2[..., None] * pos[t[1]] + w3[..., None] * pos[t[2]]
            sub = P3[y0:y1 + 1, x0:x1 + 1]
            sub[inside] = q[inside]

legs = ~np.isnan(P3[..., 0])
X, Y, Z = (np.nan_to_num(P3[..., k]) for k in range(3))
side = np.sign(X)                       # +1 左腿，-1 右腿
lx = (X - side * LEG_X) * side          # >0 = 外侧（离身体中线远）
lz = Z - LEG_Z                          # >0 = 正面
ang = np.arctan2(lz, lx)                # 0 = 外侧，+π/2 = 正面
r = np.maximum(np.hypot(lx, lz), 0.03)
half = (a.width / 2) / r


def adist(x, c):
    return np.abs((x - c + np.pi) % (2 * np.pi) - np.pi)


zone = legs & (Y > a.top - 0.002) & (Y <= a.hips_top)
fc = np.pi / 2 - a.front
strap = zone & ((adist(ang, fc) < half) | (adist(ang, 0.0) < half))
edge = strap & ((adist(ang, fc) > half * 0.72) & (adist(ang, 0.0) > half * 0.72))  # 两边细缝线
clip = legs & (Y > a.top - 0.004) & (Y <= a.top + 0.016) & ((adist(ang, fc) < half * 1.25) | (adist(ang, 0.0) < half * 1.25))

out = px.copy()
# 吊带：和袜口松紧带同色的深炭黑缎带，正面朝光那侧带一点缎面光泽
sheen = np.clip(np.cos(ang - np.pi / 2), 0, 1) ** 2 * 18
for c, base in enumerate((20, 19, 26)):
    out[strap, c] = base + sheen[strap] * (1.15 if c == 2 else 1.0)
out[edge, :3] = [38, 36, 48]
out[strap, 3] = 255
# 吊带夹：银色小扣，中间一道暗线
t = np.clip((Y - (a.top - 0.004)) / 0.02, 0, 1)
for c, base in enumerate((196, 198, 210)):
    out[clip, c] = base - 40 * np.abs(t[clip] - 0.5)
mid = clip & (np.abs(Y - (a.top + 0.006)) < 0.0018)
out[mid, :3] = [110, 112, 126]
out[clip, 3] = 255
Image.fromarray(np.clip(out, 0, 255).astype(np.uint8)).save(a.tex)
print('leg texels', int(legs.sum()), 'strap', int(strap.sum()), 'clip', int(clip.sum()))
