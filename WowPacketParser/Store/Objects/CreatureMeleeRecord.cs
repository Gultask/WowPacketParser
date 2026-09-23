using System;
using System.Collections.Generic;

namespace WowPacketParser.Store.Objects
{
    /// <summary>
    /// An entry's melee swings in one sniff, folded over everything that moves the numbers on the
    /// attacker's side: its level, attack time and the auras it carried at the swing.
    /// </summary>
    public sealed class CreatureMeleeRecord
    {
        public ulong SniffId;
        public uint Entry;
        public uint Map;
        public string Owner;
        public uint Level;
        public uint AttackTime;
        public string Auras;
        public string VictimType;
        public string Kind;
        public uint School;
        public bool Offhand;
        public int MeleeSpell;

        public readonly HashSet<string> Guids = new();
        public int Swings;
        public int OriginalMin = int.MaxValue;
        public int OriginalMax = int.MinValue;
        public long OriginalSum;
        public readonly List<int> Originals = new();
        public DateTime? FirstUtc;
        public DateTime? LastUtc;
    }

    /// <summary>
    /// Swings that landed with nothing but armor between OriginalDamage and Damage, per victim entry:
    /// the ratio of the two sums is the victim's armor reduction against that attacker's level.
    /// </summary>
    public sealed class CreatureArmorRecord
    {
        public ulong SniffId;
        public uint Entry;
        public uint Map;
        public uint Level;
        public int Armor = -1;
        public string Auras;
        public string AttackerType;
        public uint AttackerLevel;

        public readonly HashSet<string> Victims = new();
        public readonly HashSet<string> Attackers = new();
        public int Swings;
        public long OriginalSum;
        public long DamageSum;
        public int DebugSwings;
        public long DebugArmorReductionSum;
    }
}
