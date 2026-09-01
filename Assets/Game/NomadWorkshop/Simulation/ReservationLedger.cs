using System;
using System.Collections.Generic;

namespace Game.NomadWorkshop.Simulation
{
    /// <summary>
    /// 单线程模拟拥有的原子预留表。一次申请要么获得全部目标、材料和交互位，要么不改变任何状态；
    /// 它不负责跨线程同步，也不决定预留键的业务粒度。
    /// </summary>
    public sealed class ReservationLedger
    {
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private long _nextLeaseId = 1;

        /// <summary>
        /// 尝试一次性取得全部非空键。任一键已被占用时返回 false 且不产生部分预留；
        /// 成功租约由调用方持有并在行动完成、失败或取消时释放。
        /// </summary>
        public bool TryAcquire(ulong ownerId, IReadOnlyList<string> keys, out ReservationLease lease)
        {
            if (keys == null) throw new ArgumentNullException(nameof(keys));
            string[] normalized = Normalize(keys);
            for (int i = 0; i < normalized.Length; i++)
            {
                if (_entries.ContainsKey(normalized[i]))
                {
                    lease = null;
                    return false;
                }
            }

            long leaseId = _nextLeaseId++;
            for (int i = 0; i < normalized.Length; i++)
                _entries.Add(normalized[i], new Entry(ownerId, leaseId));

            lease = new ReservationLease(this, ownerId, leaseId, normalized);
            return true;
        }

        /// <summary>查询键是否已预留，并返回其稳定居民 id。</summary>
        public bool TryGetOwner(string key, out ulong ownerId)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                ownerId = default;
                return false;
            }

            if (_entries.TryGetValue(key, out Entry entry))
            {
                ownerId = entry.OwnerId;
                return true;
            }

            ownerId = default;
            return false;
        }

        internal void Release(long leaseId, IReadOnlyList<string> keys)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                string key = keys[i];
                if (_entries.TryGetValue(key, out Entry entry) && entry.LeaseId == leaseId)
                    _entries.Remove(key);
            }
        }

        private static string[] Normalize(IReadOnlyList<string> keys)
        {
            var unique = new SortedSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < keys.Count; i++)
            {
                string key = keys[i];
                if (string.IsNullOrWhiteSpace(key))
                    throw new ArgumentException($"预留键第 {i} 项为空。", nameof(keys));
                unique.Add(key);
            }

            var result = new string[unique.Count];
            unique.CopyTo(result);
            return result;
        }

        private readonly struct Entry
        {
            public Entry(ulong ownerId, long leaseId)
            {
                OwnerId = ownerId;
                LeaseId = leaseId;
            }

            public ulong OwnerId { get; }
            public long LeaseId { get; }
        }
    }

    /// <summary>一次原子预留的所有权句柄；释放幂等，旧租约不会误删后来者的预留。</summary>
    public sealed class ReservationLease : IDisposable
    {
        private ReservationLedger _ledger;
        private readonly long _leaseId;
        private readonly string[] _keys;

        internal ReservationLease(ReservationLedger ledger, ulong ownerId, long leaseId, string[] keys)
        {
            _ledger = ledger;
            OwnerId = ownerId;
            _leaseId = leaseId;
            _keys = keys;
        }

        public ulong OwnerId { get; }
        public IReadOnlyList<string> Keys => _keys;
        public bool IsReleased => _ledger == null;

        public void Dispose()
        {
            ReservationLedger ledger = _ledger;
            if (ledger == null) return;
            _ledger = null;
            ledger.Release(_leaseId, _keys);
        }
    }
}
