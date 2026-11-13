// 只保存提醒编号与回执，不保存问题/回答/密钥；与 Unity UI 解耦，方便直接测试。
using System;
using System.Collections.Generic;
using System.Linq;

namespace Kiko
{
    [Serializable]
    public class KikoAttentionReceipt
    {
        public string id;
        public long at;
        public bool acked;
    }

    [Serializable]
    public class KikoAttentionState
    {
        public List<KikoAttentionReceipt> receipts = new List<KikoAttentionReceipt>();
        public static bool Valid(string id) => id != null && id.Length == 15 && id.StartsWith("q-") && id.Skip(2).All(c => c >= '0' && c <= '9');
        public bool Seen(string id) => receipts.Exists(x => x.id == id);
        public bool Cooling(long now) => receipts.Exists(x => now - x.at < 15 * 60 * 1000);
        public KikoAttentionReceipt[] Pending() => receipts.Where(x => !x.acked).ToArray();
        public void Clean()
        {
            receipts = (receipts ?? new List<KikoAttentionReceipt>()).Where(x => x != null && Valid(x.id) && x.at > 0).ToList();
        }
        public void Remember(string id, long at, bool acked)
        {
            if (!Valid(id)) return;
            var old = receipts.Find(x => x.id == id);
            if (old != null) old.acked |= acked;
            else receipts.Add(new KikoAttentionReceipt { id = id, at = at, acked = acked });
            var done = receipts.Where(x => x.acked).ToArray();
            foreach (var item in done.Take(Math.Max(0, done.Length - 100))) receipts.Remove(item);
        }
    }
}
