"""把 VRoid 自带的狐狸尾巴网格改成设计稿（A2 三视图）那种形状：根部细、中下段最粗、尾尖收成尖。
主人 11-07：「尾巴和画的不一样，这一大坨太难看了」——VRoid 的尾巴是个中间鼓起来的蛋形，放大 1.6 倍后又宽又圆。

做法：沿尾巴骨骼链（静止姿势）把每个顶点投影到骨骼折线上，得到「沿尾巴的位置 s（0 根 → 1 尖）」和「离中轴的距离」；
按 s 分段量出现在的粗细，再把每个顶点离中轴的距离缩放到目标粗细。只改顶点位置，骨骼和蒙皮权重不动。
重复运行也安全：每次都按「现在量到的粗细 → 目标」缩放，第二次跑基本不变。

目标半径是「世界尺寸」（米），会除以 Unity 里 KikoPet.tailScale 换算回网格尺寸，所以 --scale 要和 tailScale 一致。

usage: python3 Tools/shape_tail.py in.vrm out.vrm [--scale 1.25]
"""
import argparse, json, struct
import numpy as np

# (s, 目标半径 米)：照 A2 三视图背面——根部从裙子开口出来很细，往下慢慢变粗，膝盖附近最粗，尾尖收尖
TARGET = [(0.00, 0.028), (0.12, 0.048), (0.30, 0.076), (0.48, 0.094), (0.62, 0.096),
          (0.74, 0.086), (0.85, 0.064), (0.93, 0.038), (1.00, 0.008)]
CHAIN = ['J_Opt_C_FoxTail1_01', 'J_Opt_C_FoxTail2_01', 'J_Opt_C_FoxTail3_01', 'J_Opt_C_FoxTail4_01', 'J_Opt_C_FoxTail5_01']

ap = argparse.ArgumentParser()
ap.add_argument('src'); ap.add_argument('dst')
ap.add_argument('--scale', type=float, default=1.25, help='和 Unity 里 KikoPet.tailScale 一样')
ap.add_argument('--bins', type=int, default=24)
ap.add_argument('--fur-mask', help='顺便画毛长遮罩（lilToon _FurLengthMask）：尾巴根部藏在裙子里的那段不长毛，免得毛从后裙扎出来')
ap.add_argument('--fur-root', type=float, default=0.20, help='根部多长一段（按尾巴全长比例）毛长从 0 渐变到 1')
a = ap.parse_args()

d = open(a.src, 'rb').read()
L = struct.unpack('<I', d[12:16])[0]
j = json.loads(d[20:20 + L])
b0 = 20 + L
BIN = bytearray(d[b0 + 8:b0 + 8 + struct.unpack('<I', d[b0:b0 + 4])[0]])
DT = {5126: np.float32, 5123: np.uint16, 5125: np.uint32, 5121: np.uint8}
NC = {'VEC2': 2, 'VEC3': 3, 'VEC4': 4, 'SCALAR': 1, 'MAT4': 16}


def view(i):
    A = j['accessors'][i]; v = j['bufferViews'][A['bufferView']]
    off = v.get('byteOffset', 0) + A.get('byteOffset', 0)
    n = NC[A['type']]; dt = DT[A['componentType']]
    assert v.get('byteStride', 0) in (0, n * np.dtype(dt).itemsize)
    return off, np.frombuffer(bytes(BIN[off:off + A['count'] * n * np.dtype(dt).itemsize]), dt).reshape(A['count'], n).copy()


names = [n.get('name', '') for n in j['nodes']]
mi = next(i for i, m in enumerate(j['materials']) if 'FoxTail' in m['name'])
node = next(n for n in j['nodes'] if n.get('mesh') is not None and any(p['material'] == mi for p in j['meshes'][n['mesh']]['primitives']))
prim = next(p for p in j['meshes'][node['mesh']]['primitives'] if p['material'] == mi)
skin = j['skins'][node['skin']]
_, ibm = view(skin['inverseBindMatrices']); ibm = ibm.reshape(-1, 4, 4)
chain = np.array([np.linalg.inv(ibm[skin['joints'].index(names.index(nm))].T)[:3, 3] for nm in CHAIN])

pos_off, pos = view(prim['attributes']['POSITION'])
_, idx = view(prim['indices'])
idx = np.unique(idx.ravel())  # 尾巴用到的顶点（POSITION 是整个身体共用的）
P = pos[idx].astype(np.float64)

# 骨骼折线，末端顺着最后一节方向延长到尾尖（取顶点在这个方向上最远的投影）
dlast = chain[-1] - chain[-2]; dlast /= np.linalg.norm(dlast)
tip = max(0.02, float(((P - chain[-1]) @ dlast).max()))
pl = np.vstack([chain, chain[-1] + dlast * tip])
segs = pl[1:] - pl[:-1]; lens = np.linalg.norm(segs, axis=1); cum = np.concatenate([[0], np.cumsum(lens)])
best = np.full(len(P), 1e9); S = np.zeros(len(P)); C = np.zeros_like(P)
for i, (p0, sg, l) in enumerate(zip(pl[:-1], segs, lens)):
    t = np.clip(((P - p0) @ sg) / (l * l), 0, 1)
    q = p0 + t[:, None] * sg
    dist = np.linalg.norm(P - q, axis=1)
    m = dist < best
    best[m] = dist[m]; S[m] = (cum[i] + t * l)[m]; C[m] = q[m]
s = S / cum[-1]

# 现在的粗细（每段取 75 分位 ≈ 表面），平滑一下
edges = np.linspace(0, 1, a.bins + 1); mids = (edges[:-1] + edges[1:]) / 2
cur = np.array([np.percentile(best[(s >= lo) & (s <= hi)], 75) if ((s >= lo) & (s <= hi)).sum() > 3 else np.nan
                for lo, hi in zip(edges[:-1], edges[1:])])
ok = ~np.isnan(cur); cur = np.interp(mids, mids[ok], cur[ok])
cur = np.convolve(np.pad(cur, 1, mode='edge'), [0.25, 0.5, 0.25], 'valid')
ts, tr = zip(*TARGET)
want = np.interp(s, ts, tr) / a.scale
have = np.maximum(np.interp(s, mids, cur), 0.004)
f = np.clip(want / have, 0.15, 2.5)
newP = C + (P - C) * f[:, None]
pos[idx] = newP.astype(np.float32)
raw = pos.astype(np.float32).tobytes(); BIN[pos_off:pos_off + len(raw)] = raw
A = j['accessors'][prim['attributes']['POSITION']]
if 'min' in A: A['min'] = pos.min(0).astype(float).tolist(); A['max'] = pos.max(0).astype(float).tolist()

if a.fur_mask:
    from PIL import Image
    N = 512
    _, uv = view(prim['attributes']['TEXCOORD_0'])
    _, tri = view(prim['indices']); tri = tri.reshape(-1, 3)
    sv = np.ones(len(pos)); sv[idx] = s
    val = np.clip((sv - a.fur_root * 0.35) / (a.fur_root * 0.65), 0, 1)
    val = val * val * (3 - 2 * val)
    img = np.ones((N, N), np.float32)
    for t in tri:
        P2 = uv[t].astype(np.float64) * N
        if val[t].min() >= 1: continue
        x0, y0 = np.floor(P2.min(0)).astype(int) - 1; x1, y1 = np.ceil(P2.max(0)).astype(int) + 1
        x0, y0 = max(x0, 0), max(y0, 0); x1, y1 = min(x1, N - 1), min(y1, N - 1)
        if x1 < x0 or y1 < y0: continue
        gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
        (ax, ay), (bx, by), (cx, cy) = P2
        den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy)
        if abs(den) < 1e-12: continue
        w1 = ((by - cy) * (gx - cx) + (cx - bx) * (gy - cy)) / den
        w2 = ((cy - ay) * (gx - cx) + (ax - cx) * (gy - cy)) / den
        w3 = 1 - w1 - w2
        ins = (w1 >= -0.05) & (w2 >= -0.05) & (w3 >= -0.05)
        v = w1 * val[t[0]] + w2 * val[t[1]] + w3 * val[t[2]]
        sub = img[y0:y1 + 1, x0:x1 + 1]
        sub[ins] = np.minimum(sub[ins], v[ins])
    Image.fromarray((img * 255).astype(np.uint8), 'L').save(a.fur_mask)  # glTF 的 UV 原点在左上，和图片一样
    print('fur mask', a.fur_mask, 'masked texels', int((img < 0.99).sum()))

print(f'tail verts {len(idx)}, chain length {cum[-1]:.3f} m (×{a.scale} = {cum[-1] * a.scale:.2f} m in Unity)')
for k in range(0, a.bins, max(1, a.bins // 8)):
    print(f'  s {mids[k]:.2f}: {cur[k] * a.scale * 100:5.1f} cm → {np.interp(mids[k], ts, tr) * 100:5.1f} cm')

js = json.dumps(j, ensure_ascii=False, separators=(',', ':')).encode()
while len(js) % 4: js += b' '
B = bytes(BIN)
while len(B) % 4: B += b'\0'
out = struct.pack('<III', 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(B)) + struct.pack('<II', len(js), 0x4E4F534A) + js + struct.pack('<II', len(B), 0x004E4942) + B
open(a.dst, 'wb').write(out)
print(a.dst, len(out) // 1024, 'KB')
