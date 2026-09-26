using System;
using System.Collections.Generic;
using WowPacketParser.Misc;

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
        public uint? Difficulty;

        /// <summary>The stay it was folded in; not stored. Difficulty is settled from it once the file is read.</summary>
        public MapVisit Visit;

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
        public uint? Difficulty;
        public MapVisit Visit;
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

    /// <summary>
    /// Kill experience per victim entry and the two levels that decide it, from SMSG_LOG_XP_GAIN.
    /// </summary>
    public sealed class CreatureXpRecord
    {
        public ulong SniffId;
        public uint Entry;
        public uint Map;
        public uint? Difficulty;
        public MapVisit Visit;
        public uint Zone;
        public uint Level;
        public uint PlayerLevel;
        public string Owner;
        public float GroupBonus;

        public readonly HashSet<string> Guids = new();
        public int Kills;
        public int AmountMin = int.MaxValue;
        public int AmountMax = int.MinValue;
        public long AmountSum;
        public long OriginalSum;
    }

    /// <summary>
    /// One stat sheet an entry was sent with, and how many sheet updates said exactly that.
    /// </summary>
    public sealed class CreatureStatsRecord
    {
        public ulong SniffId;
        public uint Entry;
        public string UnitType;
        public string Relation;
        public uint Map;
        public uint? Difficulty;
        public MapVisit Visit;
        public uint Level;
        public uint Class;
        public string Auras;
        public long MaxHealth;
        public int? BaseHealth;
        public int? BaseMana;
        public float? MinDamage;
        public float? MaxDamage;
        public float? MinOffHandDamage;
        public float? MaxOffHandDamage;
        public float? MinRangedDamage;
        public float? MaxRangedDamage;
        public int? AttackPower;
        public int? AttackPowerModPos;
        public int? AttackPowerModNeg;
        public float? AttackPowerMultiplier;
        public int? RangedAttackPower;
        public uint AttackTime;
        public uint OffHandAttackTime;
        public uint RangedAttackTime;
        public int? Armor;
        public string Stats;
        public string StatPosBuff;
        public string StatNegBuff;
        public string Resistances;
        public string ResistancePos;
        public string ResistanceNeg;

        public readonly HashSet<string> Guids = new();
        public int Updates;

        public string Key() => string.Join("|", Entry, UnitType, Relation, Map, Level, Class, Auras, MaxHealth, BaseHealth,
            BaseMana, MinDamage, MaxDamage, MinOffHandDamage, MaxOffHandDamage, MinRangedDamage, MaxRangedDamage,
            AttackPower, AttackPowerModPos, AttackPowerModNeg, AttackPowerMultiplier, RangedAttackPower, AttackTime,
            OffHandAttackTime, RangedAttackTime, Stats, StatPosBuff, StatNegBuff, Resistances, ResistancePos,
            ResistanceNeg);
    }
}
