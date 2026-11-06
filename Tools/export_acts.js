// 从 Tauri 版 vrm.js 导出动作/表情数据给 Unity（Assets/Kiko/Resources/kiko_acts.json）
// usage: node Tools/export_acts.js <zhixu-whale-maid-pet>/src/vrm.js Assets/Kiko/Resources/kiko_acts.json
const fs = require('fs');
const src = fs.readFileSync(process.argv[2], 'utf8');
const cut = (a, b) => { const i = src.indexOf(a), j = src.indexOf(b, i); if (i < 0 || j < 0) throw new Error('cut ' + a); return src.slice(i, j); };
const block1 = cut('const EXP = {', 'const MODELS');
const block2 = cut('const STAND = {', 'const PEEK = {');
const block3 = cut('const PEEK = {', 'const IDLE_CLIPS');
const block4 = cut('const IDLE_CLIPS', 'const FM =');
const code = `const D = Math.PI/180;\n${block1}\n${block2}\n${block3}\n${block4}\nreturn { EXP, EYES_SHUT: [...EYES_SHUT], ACT, STAND, ACTS, BUST_MAP, BUST_IDLE, IDLE_CLIPS, PEEK };`;
const r = new Function(code)();
const BONES = ['hips','spine','chest','neck','head','leftUpperArm','rightUpperArm','leftLowerArm','rightLowerArm','leftHand','rightHand','leftUpperLeg','rightUpperLeg','leftLowerLeg','rightLowerLeg'];
// Unity JsonUtility 不吃字典：动作 → 关键帧数组，每帧 bones/rot 平行数组
const acts = Object.entries(r.ACTS).map(([name, c]) => ({
  name,
  loop: c.loop === undefined ? -1 : c.loop,
  until: c.until || 0,
  hold: !!c.hold,
  earFlat: c.earFlat || 0,
  expNames: Object.keys(c.exp || {}), expValues: Object.values(c.exp || {}),
  keys: c.keys.map((k) => ({
    t: k.t, ease: k.ease || 'inout', hasHips: 'hips' in k, hips: k.hips || 0,
    bones: Object.keys(k.pose).filter((b) => BONES.includes(b)),
    rot: Object.keys(k.pose).filter((b) => BONES.includes(b)).map((b) => ({ x: k.pose[b][0], y: k.pose[b][1], z: k.pose[b][2] })),
  })),
}));
const exps = Object.entries(r.EXP).map(([mood, e]) => ({ mood, names: Object.keys(e), values: Object.values(e) }));
const moodAct = Object.entries(r.ACT).map(([mood, act]) => ({ mood, act }));
const out = { acts, exps, moodAct, eyesShut: r.EYES_SHUT, idleClips: r.IDLE_CLIPS.filter((n) => r.ACTS[n] && !/^Idle[A-E]$/.test(n)),
  bustMap: Object.entries(r.BUST_MAP).map(([from, to]) => ({ mood: from, act: to })), bustIdle: r.BUST_IDLE,
  stand: BONES.map((b) => ({ x: r.STAND[b][0], y: r.STAND[b][1], z: r.STAND[b][2] })), standBones: BONES };
fs.writeFileSync(process.argv[3], JSON.stringify(out, null, 1));
console.log('acts', acts.length, 'exps', exps.length, 'idle', out.idleClips.join(','));
