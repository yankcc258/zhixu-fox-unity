#!/usr/bin/env python3
"""把 Tauri 版用的真人动捕（.vrma）烘成 Unity 版能直接播的逐帧数据。

  python3 Tools/bake_vrma.py <zpm>/src/vrm/motion Assets/Kiko/Resources/kiko_acts.json Assets/Kiko/Resources/kiko_motions.json

换算和 three-vrm 的 VRMAnimationLoaderPlugin + createVRMAnimationClip 完全一样：
  规范化旋转 = 父人形骨骼静止世界旋转 × 动画局部旋转 × 自己静止世界旋转⁻¹
  髋骨高度按「希狐髋高 / 动画髋高」缩放（这里存成相对静止髋高的比例，Unity 里再乘希狐的髋高）
然后照 vrm.js 的 blendMotion 收幅度（amp：向站姿 slerp）和「收着点」（soft：并腿、手臂贴身、上身别扭小），
最后转成 three.js 的 XYZ 欧拉角（度），Unity 里和关键帧动作走同一套 KikoPose.ToUnity。
髋骨水平位移一律锁住（窗口里的桌宠不该走来走去）。
"""
import json
import math
import struct
import sys
from pathlib import Path

import numpy as np

FPS = 30
FM = 'fm/'
# 和 vrm.js 的 MOTIONS 一致
MOTIONS = {
    'Walk': dict(file='CC0-walk.vrma', loop=True, amp=1, speed=0.9, soft=0.5),
    'Fall': dict(file='falling_idle.vrma', loop=True, amp=0.5, soft=0.6),
    'Land': dict(file='falling_to_landing.vrma', amp=0.7, soft=0.5),
    'Hop': dict(file='jump.vrma', amp=0.6, speed=0.9, soft=0.6),
    'IdleA': dict(file='happy_idle.vrma', amp=0.75, speed=0.85, soft=0.7),
    'IdleB': dict(file='idle.vrma', amp=0.8, speed=0.85, soft=0.7),
    'Wave': dict(file=FM + '004_hello_1.vrma', frm=7.6, to=12.6, soft=0.3),
    'Cheer': dict(file=FM + '007_gekirei.vrma', frm=11.8, to=15.6, soft=0.4),
    'IdleC': dict(file=FM + '005_smartphone.vrma', soft=0.3),
    'IdleD': dict(file=FM + '006_drinkwater.vrma', frm=3.5, to=20.5, soft=0.3),
    'IdleE': dict(file=FM + '001_motion_pose.vrma', frm=6.5, to=14, soft=0.5),
}
# vrm.js 的 BONES（关键帧/弹簧管的 15 根）；amp/soft 只作用在这些上，和 Tauri 版一样
BONES = ['leftUpperArm', 'rightUpperArm', 'leftLowerArm', 'rightLowerArm', 'leftHand', 'rightHand', 'spine', 'chest',
         'neck', 'head', 'hips', 'leftUpperLeg', 'rightUpperLeg', 'leftLowerLeg', 'rightLowerLeg']
# three-vrm 的人形父子表
PARENT = {
    'hips': None, 'spine': 'hips', 'chest': 'spine', 'upperChest': 'chest', 'neck': 'upperChest', 'head': 'neck',
    'leftEye': 'head', 'rightEye': 'head', 'jaw': 'head',
}
for s in ('left', 'right'):
    S = s
    PARENT.update({
        f'{S}UpperLeg': 'hips', f'{S}LowerLeg': f'{S}UpperLeg', f'{S}Foot': f'{S}LowerLeg', f'{S}Toes': f'{S}Foot',
        f'{S}Shoulder': 'upperChest', f'{S}UpperArm': f'{S}Shoulder', f'{S}LowerArm': f'{S}UpperArm', f'{S}Hand': f'{S}LowerArm',
        f'{S}ThumbMetacarpal': f'{S}Hand', f'{S}ThumbProximal': f'{S}ThumbMetacarpal', f'{S}ThumbDistal': f'{S}ThumbProximal',
    })
    for f in ('Index', 'Middle', 'Ring', 'Little'):
        PARENT.update({f'{S}{f}Proximal': f'{S}Hand', f'{S}{f}Intermediate': f'{S}{f}Proximal', f'{S}{f}Distal': f'{S}{f}Intermediate'})
SKIP = {'leftEye', 'rightEye', 'jaw'}  # 眼睛归 LookAt 管


def unity_name(n):
    """VRM 1.0 人形骨骼名 → Unity HumanBodyBones 名（拇指三节在 Unity 里叫 Proximal/Intermediate/Distal）"""
    for s in ('left', 'right'):
        if n == f'{s}ThumbMetacarpal': return f'{s.title()}ThumbProximal'
        if n == f'{s}ThumbProximal': return f'{s.title()}ThumbIntermediate'
    return n[0].upper() + n[1:]


# ---------- 四元数（x, y, z, w），乘法约定和 three.js 一样 ----------
def qmul(a, b):
    ax, ay, az, aw = a[..., 0], a[..., 1], a[..., 2], a[..., 3]
    bx, by, bz, bw = b[..., 0], b[..., 1], b[..., 2], b[..., 3]
    return np.stack([ax * bw + aw * bx + ay * bz - az * by,
                     ay * bw + aw * by + az * bx - ax * bz,
                     az * bw + aw * bz + ax * by - ay * bx,
                     aw * bw - ax * bx - ay * by - az * bz], -1)


def qinv(q):
    return q * np.array([-1, -1, -1, 1.0])


def qrot(q, v):
    p = np.array([v[0], v[1], v[2], 0.0])
    return qmul(qmul(q, p), qinv(q))[:3]


def slerp(a, b, t):
    a, b = np.asarray(a, float), np.asarray(b, float)
    d = np.sum(a * b, -1, keepdims=True)
    b = np.where(d < 0, -b, b)
    d = np.abs(d).clip(-1, 1)
    th = np.arccos(d)
    s = np.sin(th)
    t = np.asarray(t, float)
    small = s < 1e-6
    w0 = np.where(small, 1 - t, np.sin((1 - t) * th) / np.where(small, 1, s))
    w1 = np.where(small, t, np.sin(t * th) / np.where(small, 1, s))
    r = w0 * a + w1 * b
    return r / np.linalg.norm(r, axis=-1, keepdims=True)


def q_from_euler(e):  # three.js Euler 'XYZ'（弧度）
    x, y, z = e[..., 0] / 2, e[..., 1] / 2, e[..., 2] / 2
    c1, c2, c3, s1, s2, s3 = np.cos(x), np.cos(y), np.cos(z), np.sin(x), np.sin(y), np.sin(z)
    return np.stack([s1 * c2 * c3 + c1 * s2 * s3, c1 * s2 * c3 - s1 * c2 * s3,
                     c1 * c2 * s3 + s1 * s2 * c3, c1 * c2 * c3 - s1 * s2 * s3], -1)


def euler_from_q(q):  # three.js Euler.setFromQuaternion(q, 'XYZ')（弧度）
    x, y, z, w = q[..., 0], q[..., 1], q[..., 2], q[..., 3]
    m11 = 1 - 2 * (y * y + z * z); m12 = 2 * (x * y - z * w); m13 = 2 * (x * z + y * w)
    m22 = 1 - 2 * (x * x + z * z); m23 = 2 * (y * z - x * w)
    m32 = 2 * (y * z + x * w); m33 = 1 - 2 * (x * x + y * y)
    ey = np.arcsin(np.clip(m13, -1, 1))
    ok = np.abs(m13) < 0.9999999
    ex = np.where(ok, np.arctan2(-m23, m33), np.arctan2(m32, m22))
    ez = np.where(ok, np.arctan2(-m12, m11), 0)
    return np.stack([ex, ey, ez], -1)


# ---------- glTF ----------
def load_glb(path):
    b = path.read_bytes()
    off, j, bin_ = 12, None, None
    while off < len(b):
        n, t = struct.unpack('<II', b[off:off + 8])
        chunk = b[off + 8:off + 8 + n]
        if t == 0x4E4F534A: j = json.loads(chunk)
        elif t == 0x004E4942: bin_ = chunk
        off += 8 + n
    return j, bin_


def accessor(j, bin_, i):
    a = j['accessors'][i]
    assert a['componentType'] == 5126, a
    bv = j['bufferViews'][a['bufferView']]
    n = {'SCALAR': 1, 'VEC3': 3, 'VEC4': 4}[a['type']]
    start = bv.get('byteOffset', 0) + a.get('byteOffset', 0)
    stride = bv.get('byteStride', 4 * n)
    arr = np.frombuffer(bin_, np.float32, count=a['count'] * stride // 4, offset=start).reshape(a['count'], stride // 4)[:, :n]
    return arr.astype(float)


def world_rest(j):
    """每个节点的静止世界旋转和世界矩阵（只用到 TRS）"""
    nodes = j['nodes']
    parent = {}
    for i, n in enumerate(nodes):
        for c in n.get('children', []): parent[c] = i
    rot, mat = {}, {}

    def local_m(n):
        t = np.array(n.get('translation', [0, 0, 0]), float)
        r = np.array(n.get('rotation', [0, 0, 0, 1]), float)
        s = np.array(n.get('scale', [1, 1, 1]), float)
        x, y, z, w = r
        R = np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                      [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                      [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])
        M = np.eye(4); M[:3, :3] = R * s; M[:3, 3] = t
        return M, r

    def get(i):
        if i in rot: return rot[i], mat[i]
        M, r = local_m(nodes[i])
        if i in parent:
            pr, pm = get(parent[i])
            r, M = qmul(pr, r), pm @ M
        rot[i], mat[i] = r, M
        return r, M

    for i in range(len(nodes)): get(i)
    return rot, mat, parent


def sample(times, vals, t, is_rot):
    i = np.searchsorted(times, t, side='right') - 1
    i = np.clip(i, 0, len(times) - 2) if len(times) > 1 else np.zeros_like(i)
    if len(times) == 1: return np.repeat(vals[:1], len(t), 0)
    t0, t1 = times[i], times[i + 1]
    u = np.clip((t - t0) / np.maximum(t1 - t0, 1e-9), 0, 1)[:, None]
    a, b = vals[i], vals[i + 1]
    return slerp(a, b, u) if is_rot else a + (b - a) * u


def bake(path, cfg, stand):
    j, bin_ = load_glb(path)
    ext = j['extensions']['VRMC_vrm_animation']
    hb = {k: v['node'] for k, v in ext['humanoid']['humanBones'].items() if 'node' in v}
    wrot, wmat, nparent = world_rest(j)
    an = j['animations'][0]
    tracks = {}
    dur = 0
    for ch in an['channels']:
        s = an['samplers'][ch['sampler']]
        assert s.get('interpolation', 'LINEAR') == 'LINEAR'
        tm = accessor(j, bin_, s['input'])[:, 0]
        tracks[(ch['target']['node'], ch['target']['path'])] = (tm, accessor(j, bin_, s['output']))
        dur = max(dur, tm[-1])
    t0 = cfg.get('frm', 0.0)
    t1 = min(cfg.get('to', dur), dur)
    n = int(round((t1 - t0) * FPS)) + 1
    ts = t0 + np.arange(n) / FPS
    node2bone = {v: k for k, v in hb.items()}
    out = {}
    for bone, node in hb.items():
        if bone in SKIP or (node, 'rotation') not in tracks: continue
        p = PARENT.get(bone)
        while p is not None and p not in hb: p = PARENT.get(p)
        Pw = wrot[hb[p]] if p is not None else (wrot[nparent[node]] if node in nparent else np.array([0, 0, 0, 1.0]))
        Wi = qinv(wrot[node])
        tm, v = tracks[(node, 'rotation')]
        q = sample(tm, v / np.linalg.norm(v, axis=1, keepdims=True), ts, True)
        out[bone] = qmul(qmul(np.broadcast_to(Pw, q.shape), q), np.broadcast_to(Wi, q.shape))
    # 髋骨高度：先用 hipsParent 的世界矩阵变换，再按静止髋高归一
    hips_frac = np.zeros(n)
    hn = hb['hips']
    rest_y = wmat[hn][1, 3]
    if (hn, 'translation') in tracks:
        tm, v = tracks[(hn, 'translation')]
        pos = sample(tm, v, ts, False)
        pm = wmat[nparent[hn]] if hn in nparent else np.eye(4)
        y = (pm[:3, :3] @ pos.T).T[:, 1] + pm[1, 3]
        hips_frac = y / rest_y - 1
    # ---- vrm.js blendMotion：amp 向站姿 slerp，soft 收着点 ----
    amp, soft = cfg.get('amp', 1), cfg.get('soft', 0)
    D = math.pi / 180
    eul = {}
    for b, q in out.items():
        if b in BONES and amp < 1:
            sq = q_from_euler(np.array(stand[b], float) * D)
            q = slerp(np.broadcast_to(sq, q.shape), q, amp)
        e = euler_from_q(q)
        if b in BONES and soft:
            if b in ('leftUpperLeg', 'rightUpperLeg'):
                sd = 1 if b == 'leftUpperLeg' else -1
                e[:, 2] *= 1 - 0.7 * soft
                e[:, 1] += sd * 6 * D * soft
            elif b in ('leftUpperArm', 'rightUpperArm'):
                down = -72 * D if b == 'leftUpperArm' else 72 * D
                e[:, 2] += (down - e[:, 2]) * 0.35 * soft
                e[:, 0] *= 1 - 0.3 * soft
            elif b in ('spine', 'chest'):
                e[:, 1] *= 1 - 0.5 * soft
                e[:, 2] *= 1 - 0.4 * soft
            # three.js 是把改过的欧拉角写回四元数；再读出来时可能换一组等价解，这里直接用改过的欧拉角（同一个旋转）
        eul[b] = np.unwrap(e, axis=0) / D
    hips_frac *= amp if amp < 1 else 1
    bones = sorted(eul)
    frames = np.stack([eul[b] for b in bones], 1)  # [n, bones, 3]
    return {
        'name': '', 'fps': FPS, 'speed': cfg.get('speed', 1), 'loop': bool(cfg.get('loop')),
        'bones': [unity_name(b) for b in bones],
        'frames': [round(float(x), 1) for x in frames.reshape(-1)],
        'hips': [round(float(x), 4) for x in hips_frac],
        'count': n,
    }


def main():
    src, acts_json, dst = Path(sys.argv[1]), Path(sys.argv[2]), Path(sys.argv[3])
    a = json.loads(acts_json.read_text(encoding='utf-8'))
    stand = {b: [v['x'], v['y'], v['z']] for b, v in zip(a['standBones'], a['stand'])}
    res = []
    for name, cfg in MOTIONS.items():
        f = src / cfg['file']
        if not f.exists():
            print('skip', name, f); continue
        m = bake(f, cfg, stand)
        m['name'] = name
        res.append(m)
        print(f"{name:6s} {m['count']:4d} frames {len(m['bones']):2d} bones  hips {min(m['hips']):+.3f}..{max(m['hips']):+.3f}")
    dst.write_text(json.dumps({'motions': res}, ensure_ascii=False, separators=(',', ':')), encoding='utf-8')
    print(dst, dst.stat().st_size // 1024, 'KB')


if __name__ == '__main__':
    main()
