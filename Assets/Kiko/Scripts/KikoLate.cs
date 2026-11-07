// 在 VRM 的弹簧骨（Vrm10Instance.LateUpdate）之后再改一下骨骼：坐下时把裙子前片盖到腿上。
using UnityEngine;

namespace Kiko
{
    [DefaultExecutionOrder(30000)]
    public class KikoLate : MonoBehaviour
    {
        public KikoPet pet;
        void LateUpdate() { if (pet) pet.DrapeSkirt(); }
    }
}
