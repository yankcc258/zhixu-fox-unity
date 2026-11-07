// 真人动捕片段（Tauri 版的 .vrma，用 Tools/bake_vrma.py 烘成 Resources/kiko_motions.json）。
// 每帧存的是规范化骨骼的 three.js XYZ 欧拉角（度），和关键帧动作同一套坐标，播放时走 KikoPose.ToUnity。
// 幅度（amp）和「收着点」（soft）已经在烘焙时按 vrm.js 的 blendMotion 做好了。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kiko
{
    [Serializable]
    public class KikoMotion
    {
        public string name;
        public float fps = 30;
        public float speed = 1;
        public bool loop;
        public bool pingpong; // 截出来的一段（坐着/躺着的中间部分）首尾接不上 → 来回放
        public string[] bones;
        public float[] frames; // [帧][骨骼][xyz]
        public float[] hips;   // 每帧髋高相对静止髋高的比例（-0.1 = 低 10%）
        public int count;

        [NonSerialized] public HumanBodyBones[] ids;
        public float Duration => (count - 1) / fps;

        /// t 秒处的姿势（帧间线性插值），写进 pose；返回髋高比例
        public float Sample(float t, Dictionary<HumanBodyBones, Vector3> pose)
        {
            float f = Mathf.Clamp(t * fps, 0, count - 1);
            int i = Mathf.Min((int)f, count - 2);
            if (count < 2) i = 0;
            float u = count < 2 ? 0 : f - i;
            int nb = bones.Length;
            for (int b = 0; b < nb; b++)
            {
                if ((int)ids[b] < 0) continue;
                int a = (i * nb + b) * 3, c = count < 2 ? a : a + nb * 3;
                pose[ids[b]] = new Vector3(
                    Mathf.Lerp(frames[a], frames[c], u),
                    Mathf.Lerp(frames[a + 1], frames[c + 1], u),
                    Mathf.Lerp(frames[a + 2], frames[c + 2], u));
            }
            return count < 2 ? hips[0] : Mathf.Lerp(hips[i], hips[i + 1], u);
        }
    }

    [Serializable]
    public class KikoMotionData
    {
        public KikoMotion[] motions;

        public static Dictionary<string, KikoMotion> Load(string json)
        {
            var d = JsonUtility.FromJson<KikoMotionData>(json);
            var r = new Dictionary<string, KikoMotion>();
            foreach (var m in d.motions)
            {
                m.ids = new HumanBodyBones[m.bones.Length];
                for (int i = 0; i < m.bones.Length; i++)
                    m.ids[i] = Enum.TryParse<HumanBodyBones>(m.bones[i], out var b) ? b : (HumanBodyBones)(-1);
                r[m.name] = m;
            }
            return r;
        }
    }
}
