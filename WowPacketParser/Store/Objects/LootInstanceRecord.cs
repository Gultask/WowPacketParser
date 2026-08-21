using System;
using System.Collections.Generic;

namespace WowPacketParser.Store.Objects
{
    /// <summary>
    /// One SMSG_LOOT_RESPONSE - everything a single corpse or object yielded on one occasion.
    ///
    /// The point of storing whole instances rather than per-item tallies is co-occurrence: which
    /// items came out together answers questions a drop rate table cannot, such as whether two
    /// qualities share one pool or roll independently.
    ///
    /// Instances that yielded nothing are kept deliberately. A mob that dropped no items is the
    /// denominator - without those rows every chance computed from this data would be wrong.
    /// Money and items are independently empty, so both zero cases are real.
    /// </summary>
    public sealed class LootInstanceRecord
    {
        public ulong SniffId;

        public string OwnerGuid;
        public string LootObjectGuid;

        /// <summary>Filled in after parsing, from the object we saw. Null if it was never in view.</summary>
        public uint? OwnerEntry;
        public string OwnerType;
        public int? OwnerLevel;
        public uint? Map;

        public int AcquireReason;
        public string AcquireReasonName;
        public int LootMethod;
        public string LootMethodName;
        public int Threshold;

        public uint Coins;
        public int ItemCount;

        public DateTime? SeenUtc;

        public List<LootInstanceItemRecord> Items = new List<LootInstanceItemRecord>();
    }

    public sealed class LootInstanceItemRecord
    {
        public int Slot;
        public int ItemId;
        public uint Quantity;
        public int UiType;
        public uint RandomPropertiesId;
        public int LootItemType;
    }

    /// <summary>
    /// A party size change, with the time it happened. Loot taken while grouped is discarded, and
    /// because packets are parsed in parallel the decision cannot be made from a running flag in
    /// a handler - these are timestamped and correlated once parsing is done.
    /// </summary>
    public sealed class PartyStateRecord
    {
        public DateTime Time;
        public int PlayerCount;
    }
}
