// 动作（关键帧片段）和表情表：数据从 Tauri 版 vrm.js 原样导出（Tools/export_acts.js → Resources/kiko_acts.json），
// 求值规则也和 vrm.js 的 evalClip 一样：没写的骨骼沿用上一帧，最前面隐含一帧 = 站姿；缓动 in/out/inout/back/snap。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kiko
{
    [Serializable] public class KV3 { public float x, y, z; public Vector3 V => new Vector3(x, y, z); }

    [Serializable]
    public class KikoKey
    {
        public float t;
        public string ease;
        public bool hasHips;
        public float hips;
        public string[] bones;
        public KV3[] rot;
    }

    [Serializable]
    public class KikoClip
    {
        public string name;
        public float loop = -1; // 毫秒；-1 = 不循环
        public float until;     // 循环最多播多久（毫秒），0 = 一直
        public bool hold;
        public float earFlat;
        public string[] expNames;
        public float[] expValues;
        public KikoKey[] keys;
        [NonSerialized] public float dur;
        [NonSerialized] public Dictionary<HumanBodyBones, Vector3>[] pose;
    }

    [Serializable] public class KikoExp { public string mood; public string[] names; public float[] values; }
    [Serializable] public class KikoMoodAct { public string mood; public string act; }

    [Serializable]
    public class KikoActData
    {
        public KikoClip[] acts;
        public KikoExp[] exps;
        public KikoMoodAct[] moodAct;
        public string[] eyesShut;
        public string[] idleClips;
        public KikoMoodAct[] bustMap;
        public string[] bustIdle;
        public KV3[] stand;
        public string[] standBones;
    }

    public class KikoActs
    {
        public readonly Dictionary<string, KikoClip> Clips = new();
        public readonly Dictionary<string, Dictionary<string, float>> Exp = new();
        public readonly Dictionary<string, string> MoodAct = new();
        public readonly HashSet<string> EyesShut = new();
        public string[] IdleClips = Array.Empty<string>();
        public readonly Dictionary<HumanBodyBones, Vector3> Stand = new();

        static readonly Dictionary<string, HumanBodyBones> BoneOf = new()
        {
            { "hips", HumanBodyBones.Hips }, { "spine", HumanBodyBones.Spine }, { "chest", HumanBodyBones.Chest },
            { "neck", HumanBodyBones.Neck }, { "head", HumanBodyBones.Head },
            { "leftUpperArm", HumanBodyBones.LeftUpperArm }, { "rightUpperArm", HumanBodyBones.RightUpperArm },
            { "leftLowerArm", HumanBodyBones.LeftLowerArm }, { "rightLowerArm", HumanBodyBones.RightLowerArm },
            { "leftHand", HumanBodyBones.LeftHand }, { "rightHand", HumanBodyBones.RightHand },
            { "leftUpperLeg", HumanBodyBones.LeftUpperLeg }, { "rightUpperLeg", HumanBodyBones.RightUpperLeg },
            { "leftLowerLeg", HumanBodyBones.LeftLowerLeg }, { "rightLowerLeg", HumanBodyBones.RightLowerLeg },
        };

        public static KikoActs Load(string json)
        {
            var d = JsonUtility.FromJson<KikoActData>(json);
            var r = new KikoActs();
            for (int i = 0; i < d.standBones.Length; i++)
                if (BoneOf.TryGetValue(d.standBones[i], out var b)) r.Stand[b] = d.stand[i].V;
            foreach (var c in d.acts)
            {
                c.dur = c.keys.Length > 0 ? c.keys[^1].t : 0;
                c.pose = new Dictionary<HumanBodyBones, Vector3>[c.keys.Length];
                for (int k = 0; k < c.keys.Length; k++)
                {
                    var p = new Dictionary<HumanBodyBones, Vector3>();
                    var key = c.keys[k];
                    for (int j = 0; j < key.bones.Length; j++)
                        if (BoneOf.TryGetValue(key.bones[j], out var b)) p[b] = key.rot[j].V;
                    c.pose[k] = p;
                }
                r.Clips[c.name] = c;
            }
            foreach (var e in d.exps)
            {
                var m = new Dictionary<string, float>();
                for (int i = 0; i < e.names.Length; i++) m[e.names[i]] = e.values[i];
                r.Exp[e.mood] = m;
            }
            foreach (var a in d.moodAct) r.MoodAct[a.mood] = a.act;
            foreach (var s in d.eyesShut) r.EyesShut.Add(s);
            r.IdleClips = d.idleClips;
            return r;
        }

        static float Ease(string e, float t) => e switch
        {
            "in" => t * t,
            "out" => 1 - (1 - t) * (1 - t),
            "back" => 1 + 2.3f * Mathf.Pow(t - 1, 3) + 1.3f * Mathf.Pow(t - 1, 2),
            "snap" => 1,
            _ => 0.5f - 0.5f * Mathf.Cos(t * Mathf.PI),
        };

        /// 在 tm 毫秒处求片段：写进 pose（只写片段里出现过或站姿的骨骼），返回髋骨下沉（米）；片段放完返回 false
        public bool Eval(KikoClip c, float tm, Dictionary<HumanBodyBones, Vector3> pose, out float hips)
        {
            hips = 0;
            if (tm > c.dur)
            {
                if (c.loop >= 0 && !(c.until > 0 && tm > c.until) && c.dur > c.loop) tm = c.loop + (tm - c.loop) % (c.dur - c.loop);
                else if (c.hold) tm = c.dur;
                else return false;
            }
            foreach (var kv in Stand)
            {
                var bone = kv.Key;
                Vector3 pv = kv.Value;
                float pt = 0;
                int nk = -1;
                for (int k = 0; k < c.keys.Length; k++)
                {
                    if (!c.pose[k].TryGetValue(bone, out var v)) continue;
                    if (c.keys[k].t <= tm) { pv = v; pt = c.keys[k].t; }
                    else { nk = k; break; }
                }
                if (nk < 0) pose[bone] = pv;
                else
                {
                    float u = Ease(c.keys[nk].ease, (tm - pt) / Mathf.Max(1e-3f, c.keys[nk].t - pt));
                    pose[bone] = pv + (c.pose[nk][bone] - pv) * u;
                }
            }
            float hv = 0, ht = 0;
            int hn = -1;
            for (int k = 0; k < c.keys.Length; k++)
            {
                if (!c.keys[k].hasHips) continue;
                if (c.keys[k].t <= tm) { hv = c.keys[k].hips; ht = c.keys[k].t; }
                else { hn = k; break; }
            }
            hips = hn < 0 ? hv : hv + (c.keys[hn].hips - hv) * Ease(c.keys[hn].ease, (tm - ht) / Mathf.Max(1e-3f, c.keys[hn].t - ht));
            return true;
        }
    }
}
