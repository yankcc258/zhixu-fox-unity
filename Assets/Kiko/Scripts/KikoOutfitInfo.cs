// 衣柜里一套衣服的说明（见 KikoWardrobe.cs）
using UnityEngine;

namespace Kiko
{
    /// 挂在衣服预制体实例的根上（编辑器菜单 M1 放的）
    public class KikoOutfitInfo : MonoBehaviour
    {
        public string label = "衣服";
        public bool keepBase = true;      // 下面留不留底层内衣（比基尼这种自己就是贴身的 → 不留）
        public bool hideUniform = true;   // 穿这套时把模型原来的制服、长袜藏起来
        public string[] bodyShapes = new string[0]; // 穿这套时身体要开的形态键，「名字=数值」（高跟鞋 → Foot_heel=100）
    }
}
