// 姿势表：沿用 Tauri 版 vrm.js 的关节角（度，three.js 的 XYZ 欧拉角、VRM 规范化骨骼）。
// three-vrm 的规范化骨骼和 UniVRM 的 ControlRig 都是「T-pose = 单位旋转」，差别只在左右手坐标系：
// UniVRM 导入 VRM1 时把 X 轴取反，所以旋转 (x,y,z,w) → (x,-y,-z,w)。这样那边调好的数字可以原样搬过来。
using System.Collections.Generic;
using UnityEngine;

namespace Kiko
{
    public static class KikoPose
    {
        public static readonly HumanBodyBones[] Bones =
        {
            HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.Neck, HumanBodyBones.Head,
            HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm,
            HumanBodyBones.LeftHand, HumanBodyBones.RightHand,
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg,
        };

        public static Dictionary<HumanBodyBones, Vector3> Make(params (HumanBodyBones b, float x, float y, float z)[] e)
        {
            var d = new Dictionary<HumanBodyBones, Vector3>();
            foreach (var b in Bones) d[b] = Vector3.zero;
            foreach (var (b, x, y, z) in e) d[b] = new Vector3(x, y, z);
            return d;
        }

        // 站姿：女仆式双手交叠放在身前（vrm.js STAND）
        public static readonly Dictionary<HumanBodyBones, Vector3> Stand = Make(
            (HumanBodyBones.LeftUpperArm, 3, -40, -68), (HumanBodyBones.RightUpperArm, -3, 40, 68),
            (HumanBodyBones.LeftLowerArm, 0, -95, 0), (HumanBodyBones.RightLowerArm, 0, 95, 0),
            (HumanBodyBones.LeftHand, 0, -15, 0), (HumanBodyBones.RightHand, 0, 15, 0));

        // 买来的小个子模型（马努卡：上臂 18cm、躯干短、胸大）的站姿：原来那组角度手会抬到胸口、插进胸里（主人 11-10）。
        // 按她的骨架重新解的：手腕在小腹前（比胸低 8cm 左右），手肘贴近身侧，右手叠在左手外面一点。
        public static readonly Dictionary<HumanBodyBones, Vector3> StandSmall = new()
        {
            { HumanBodyBones.LeftUpperArm, new Vector3(27, -28, -43) }, { HumanBodyBones.RightUpperArm, new Vector3(23, 29, 46) },
            { HumanBodyBones.LeftLowerArm, new Vector3(0, -106, 0) }, { HumanBodyBones.RightLowerArm, new Vector3(0, 100, 0) },
            { HumanBodyBones.LeftHand, new Vector3(0, -11, -28) }, { HumanBodyBones.RightHand, new Vector3(0, 12, 30) },
        };

        // 被拎着：手脚垂下、头往后仰一点（vrm.js Hang 的第一帧附近）
        public static readonly Dictionary<HumanBodyBones, Vector3> Dangle = Make(
            (HumanBodyBones.LeftUpperArm, 0, 0, -72), (HumanBodyBones.RightUpperArm, 0, 0, 72),
            (HumanBodyBones.LeftLowerArm, 0, -8, 0), (HumanBodyBones.RightLowerArm, 0, 8, 0),
            (HumanBodyBones.Spine, -4, 0, 0), (HumanBodyBones.Head, -6, 0, 0),
            (HumanBodyBones.LeftUpperLeg, -6, 0, 0), (HumanBodyBones.RightUpperLeg, 4, 0, 0),
            (HumanBodyBones.LeftLowerLeg, 14, 0, 0), (HumanBodyBones.RightLowerLeg, 8, 0, 0));

        // 落地：膝盖弯一下、上身前倾
        public static readonly Dictionary<HumanBodyBones, Vector3> Land = Make(
            (HumanBodyBones.LeftUpperArm, 3, -36, -60), (HumanBodyBones.RightUpperArm, -3, 36, 60),
            (HumanBodyBones.LeftLowerArm, 0, -90, 0), (HumanBodyBones.RightLowerArm, 0, 90, 0),
            (HumanBodyBones.Spine, 10, 0, 0), (HumanBodyBones.Head, -6, 0, 0),
            (HumanBodyBones.LeftUpperLeg, -24, 0, 0), (HumanBodyBones.RightUpperLeg, -24, 0, 0),
            (HumanBodyBones.LeftLowerLeg, 40, 0, 0), (HumanBodyBones.RightLowerLeg, 40, 0, 0));

        // 被戳：缩一下，双手收到胸前（vrm.js Flinch 第一帧）
        public static readonly Dictionary<HumanBodyBones, Vector3> Flinch = Make(
            (HumanBodyBones.Head, -14, 0, 0), (HumanBodyBones.Neck, -6, 0, 0), (HumanBodyBones.Spine, -6, 0, 0),
            (HumanBodyBones.LeftUpperArm, -30, -25, -58), (HumanBodyBones.RightUpperArm, -30, 25, 58),
            (HumanBodyBones.LeftLowerArm, 0, -115, 0), (HumanBodyBones.RightLowerArm, 0, 115, 0),
            (HumanBodyBones.LeftHand, 0, -20, 0), (HumanBodyBones.RightHand, 0, 20, 0));

        /// three.js Euler(x,y,z,'XYZ') 度 → UniVRM ControlRig 的 localRotation
        public static Quaternion ToUnity(Vector3 deg)
        {
            var q = Quaternion.AngleAxis(deg.x, Vector3.right) * Quaternion.AngleAxis(deg.y, Vector3.up) * Quaternion.AngleAxis(deg.z, Vector3.forward);
            return new Quaternion(q.x, -q.y, -q.z, q.w);
        }
    }
}
