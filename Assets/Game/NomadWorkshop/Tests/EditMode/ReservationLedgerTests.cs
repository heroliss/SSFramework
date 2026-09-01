using NUnit.Framework;

namespace Game.NomadWorkshop.Simulation.Tests
{
    /// <summary>锁定目标、材料和交互位的全有或全无预留语义。</summary>
    public sealed class ReservationLedgerTests
    {
        [Test]
        public void ConflictingBatch_FailsWithoutLeakingPartialReservation()
        {
            var ledger = new ReservationLedger();
            Assert.IsTrue(ledger.TryAcquire(1, new[] { "material:part-01", "station:repair" }, out ReservationLease first));

            bool acquired = ledger.TryAcquire(2, new[] { "tool:wrench", "station:repair" }, out ReservationLease second);

            Assert.IsFalse(acquired);
            Assert.IsNull(second);
            Assert.IsFalse(ledger.TryGetOwner("tool:wrench", out _),
                "冲突批次失败后，前面检查过的空闲键也不能残留部分预留。 ");
            Assert.IsTrue(ledger.TryGetOwner("station:repair", out ulong owner));
            Assert.AreEqual(1UL, owner);
            first.Dispose();
        }

        [Test]
        public void Dispose_ReleasesEveryKeyAndIsIdempotent()
        {
            var ledger = new ReservationLedger();
            Assert.IsTrue(ledger.TryAcquire(7, new[] { "station:water", "item:cup" }, out ReservationLease lease));

            lease.Dispose();
            lease.Dispose();

            Assert.IsTrue(lease.IsReleased);
            Assert.IsFalse(ledger.TryGetOwner("station:water", out _));
            Assert.IsFalse(ledger.TryGetOwner("item:cup", out _));
            Assert.IsTrue(ledger.TryAcquire(8, new[] { "station:water", "item:cup" }, out ReservationLease next));
            next.Dispose();
        }

        [Test]
        public void DuplicateKeys_AreNormalizedIntoOneLeaseEntry()
        {
            var ledger = new ReservationLedger();
            Assert.IsTrue(ledger.TryAcquire(3, new[] { "station:bed", "station:bed" }, out ReservationLease lease));

            Assert.AreEqual(1, lease.Keys.Count);
            Assert.IsTrue(ledger.TryGetOwner("station:bed", out ulong owner));
            Assert.AreEqual(3UL, owner);
            lease.Dispose();
        }
    }
}
