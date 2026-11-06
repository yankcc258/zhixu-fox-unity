"""给 kiko.vrm 的脸加「屑狐狸」用的新形变（主人 11-06：表情太乖了，这是屑狐狸）。
VRoid 自带的嘴型都是左右对称、幅度很小的乖巧款，坏笑需要「一边嘴角翘、一边眉毛挑」。
这里把现有形变的位移按左右半脸加权混合成新的形变，再挂成 VRM 自定义表情（名字 kuzu_*）。

usage: python3 Tools/add_kuzu_morphs.py in.vrm out.vrm
（可以原地覆盖；重复运行会先删掉旧的 kuzu_* 再加）
VRM 1.0 朝 +Z，角色自己的右边 = -X。
"""
import json, struct, sys
import numpy as np

# 名字: [(原形变名, 系数, 哪半边 R/L/B), ...]
RECIPES = {
    # 右嘴角往上翘的坏笑（闭着嘴），左边稍微压平 → 歪嘴笑
    'kuzu_SmirkR': [('Fcl_MTH_Fun', 1.0, 'R'), ('Fcl_MTH_Up', 0.6, 'R'), ('Fcl_MTH_Joy', 0.15, 'R')],  # 试过 4 种，这个最歪
    # 右边咧开露虎牙的坏笑（张一边嘴）
    'kuzu_GrinR': [('Fcl_MTH_Joy', 0.85, 'R'), ('Fcl_MTH_Joy', 0.2, 'L'), ('Fcl_MTH_Fun', 0.8, 'B')],
    # 一边眉毛挑起、一边压低：算计脸
    'kuzu_BrowScheme': [('Fcl_BRW_Surprised', 1.0, 'L'), ('Fcl_BRW_Angry', 0.9, 'R')],
    # 半眯眼（ジト目）：上眼皮平着压下来一些
    'kuzu_Jito': [('Fcl_EYE_Close', 0.38, 'B'), ('Fcl_EYE_Angry', 0.35, 'B')],
}
EDGE = 0.004  # 中线附近 ±4mm 平滑过渡


def main(src, dst):
    d = open(src, 'rb').read()
    L = struct.unpack('<I', d[12:16])[0]
    j = json.loads(d[20:20 + L])
    b0 = 20 + L
    BL = struct.unpack('<I', d[b0:b0 + 4])[0]
    BIN = bytearray(d[b0 + 8:b0 + 8 + BL])

    def read(ai):
        A = j['accessors'][ai]; v = j['bufferViews'][A['bufferView']]
        off = v.get('byteOffset', 0) + A.get('byteOffset', 0)
        return np.frombuffer(bytes(BIN[off:off + A['count'] * 12]), np.float32).reshape(A['count'], 3).copy()

    def write(arr, with_minmax):
        nonlocal BIN
        while len(BIN) % 4: BIN.append(0)
        off = len(BIN); raw = arr.astype(np.float32).tobytes(); BIN += raw
        j['bufferViews'].append({'buffer': 0, 'byteOffset': off, 'byteLength': len(raw)})
        acc = {'bufferView': len(j['bufferViews']) - 1, 'byteOffset': 0, 'type': 'VEC3', 'componentType': 5126, 'count': len(arr)}
        if with_minmax:
            acc['min'] = arr.min(0).astype(float).tolist(); acc['max'] = arr.max(0).astype(float).tolist()
        j['accessors'].append(acc)
        return len(j['accessors']) - 1

    ext = j['extensions']['VRMC_vrm']['expressions']
    custom = ext.setdefault('custom', {})
    face_node = next(i for i, n in enumerate(j['nodes']) if 'mesh' in n and 'Fcl_MTH_Joy' in j['meshes'][n['mesh']].get('extras', {}).get('targetNames', []))
    mesh = j['meshes'][j['nodes'][face_node]['mesh']]
    names = mesh['extras']['targetNames']
    # 重复运行：去掉旧的 kuzu_*（只删末尾的，前面的索引不动）
    while names and names[-1].startswith('kuzu_'):
        names.pop()
        for p in mesh['primitives']: p['targets'].pop()
        if 'weights' in mesh: mesh['weights'].pop()
    for k in [k for k in custom if k.startswith('kuzu_')]: del custom[k]

    for new, recipe in RECIPES.items():
        idx = len(names)
        for p in mesh['primitives']:
            base = read(p['attributes']['POSITION'])
            x = base[:, 0]
            wR = np.clip((EDGE - x) / (2 * EDGE), 0, 1)  # 角色右边（-X）=1
            side = {'R': wR, 'L': 1 - wR, 'B': np.ones_like(wR)}
            dp = np.zeros_like(base); dn = np.zeros_like(base); has_n = True
            for src_name, coef, s in recipe:
                t = p['targets'][names.index(src_name)]
                dp += read(t['POSITION']) * (coef * side[s])[:, None]
                if 'NORMAL' in t: dn += read(t['NORMAL']) * (coef * side[s])[:, None]
                else: has_n = False
            tgt = {'POSITION': write(dp, True)}
            if has_n: tgt['NORMAL'] = write(dn, False)
            p['targets'].append(tgt)
        names.append(new)
        if 'weights' in mesh: mesh['weights'].append(0)
        custom[new] = {'morphTargetBinds': [{'node': face_node, 'index': idx, 'weight': 1.0}], 'isBinary': False,
                       'overrideBlink': 'none', 'overrideLookAt': 'none', 'overrideMouth': 'none'}
        print('added', new, 'index', idx)

    while len(BIN) % 4: BIN.append(0)
    j['buffers'][0]['byteLength'] = len(BIN)
    js = json.dumps(j, ensure_ascii=False, separators=(',', ':')).encode('utf-8')
    while len(js) % 4: js += b' '
    out = struct.pack('<III', 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(BIN))
    out += struct.pack('<II', len(js), 0x4E4F534A) + js + struct.pack('<II', len(BIN), 0x004E4942) + bytes(BIN)
    open(dst, 'wb').write(out)
    print(dst, len(out) // 1024, 'KB')


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
