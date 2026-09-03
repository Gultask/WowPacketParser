using System;

namespace WowPacketParser.Store.Objects
{
    /// <summary>
    /// One SMSG_SPELL_START: a creature deciding to cast, at the moment it decided.
    ///
    /// Kept as raw events rather than a summary. The gap between two starts of the same spell
    /// by the same creature is the observation a cooldown is read from, and AzerothCore wants a
    /// min and a max timer per spell - but those timers have no upper bound, so which quantile
    /// stands in for the max is a judgement that belongs to whoever is deriving the timers, not
    /// to the ingest. A summary cannot be un-summarised; 1.1% of packets is affordable.
    ///
    /// The start is the right event, not the completed cast: a start is when the cooldown
    /// expired, while a go lags it by the cast time and never arrives at all if the cast was
    /// interrupted.
    ///
    /// Gaps must be taken per guid. Two creatures of one entry cast independently, so ordering
    /// an entry's rows by time and subtracting invents gaps shorter than any real cooldown.
    /// </summary>
    public sealed class CreatureSpellCastRecord
    {
        public ulong SniffId;
        public string Guid;
        public uint Entry;
        public uint Map;
        public uint SpellId;
        public DateTime? StartedUtc;

        /// <summary>A matching SMSG_SPELL_GO arrived; false means it was interrupted or is a channel.</summary>
        public bool Completed;
    }

    /// <summary>
    /// Which creature entries a spell actually landed on, counted per sniff.
    ///
    /// This is the empirical answer to the question DBCs cannot settle: a spell whose implicit
    /// target is an entry - TARGET_UNIT_NEARBY_ENTRY and friends - names no entry anywhere in
    /// Spell.dbc, because the entry lives in the server's own tables. The packets do name it:
    /// SMSG_SPELL_GO lists what was hit.
    /// </summary>
    public sealed class SpellTargetRecord
    {
        public ulong SniffId;
        public uint SpellId;
        public uint CasterEntry;
        public string CasterType;
        public uint TargetEntry;
        public string TargetType;
        public int Hits;
    }

    /// <summary>
    /// Where a spell was aimed, when it was aimed at ground rather than at a unit. The rows a
    /// spell_target_position is built from.
    /// </summary>
    public sealed class SpellDestinationRecord
    {
        public ulong SniffId;
        public uint SpellId;
        public uint CasterEntry;
        public uint Map;
        public float PositionX;
        public float PositionY;
        public float PositionZ;
        public float? Orientation;
        public int Casts;
    }

    /// <summary>
    /// What a creature was holding. One row per creature per sniff; the three slots are the
    /// main hand, off hand and ranged virtual items the client is told to draw.
    /// </summary>
    public sealed class CreatureEquipRecord
    {
        public ulong SniffId;
        public string Guid;
        public uint Entry;
        public uint Map;
        public uint ItemId1;
        public uint ItemId2;
        public uint ItemId3;
    }

    /// <summary>
    /// An aura seen on a creature. Auras a creature carries on sight are the ones worth having -
    /// they are the ones a spawn is meant to start with - so who cast it is recorded, letting
    /// self-cast be told apart from something a player did to it.
    /// </summary>
    public sealed class CreatureAuraRecord
    {
        public ulong SniffId;
        public string Guid;
        public uint Entry;
        public uint Map;
        public uint SpellId;
        public bool SelfCast;

        /// <summary>Set when the aura was already on the creature in the block that created it.</summary>
        public bool OnCreate;
        public int Observations;
        public int? MaxDurationMs;
    }

    /// <summary>A gossip menu as the server sent it, with the creature that opened it.</summary>
    public sealed class GossipMenuRecord
    {
        public ulong SniffId;
        public uint MenuId;
        public uint TextId;
        public uint CreatureEntry;
        public int Observations;
    }

    /// <summary>One line of a gossip menu.</summary>
    public sealed class GossipMenuOptionRecord
    {
        public ulong SniffId;
        public uint MenuId;
        public uint OptionIndex;
        public int OptionIcon;
        public string OptionText;
        public uint BoxMoney;
        public bool BoxCoded;
        public string BoxText;
    }

    /// <summary>A broadcast text block a gossip menu points at.</summary>
    public sealed class NpcTextRecord
    {
        public ulong SniffId;
        public uint TextId;
        public int Slot;
        public float Probability;
        public string Text0;
        public string Text1;
        public uint Language;
        public uint BroadcastTextId;
    }

    /// <summary>Where one SMSG_NEW_WORLD put the player, kept so a teleport can be paired to it.</summary>
    public sealed class WorldPortRecord
    {
        public DateTime Time;
        public uint Map;
        public float PositionX;
        public float PositionY;
        public float PositionZ;
        public float Orientation;
    }

    /// <summary>
    /// A client area trigger followed by the world change it produced.
    ///
    /// The teleport is never in one packet: the client reports crossing the trigger and the
    /// server answers with a new world some packets later. Pairing them is what makes the row,
    /// and the pairing is only trustworthy when nothing else could have caused the change, so
    /// the two have to be adjacent in time - see the window the collector applies.
    /// </summary>
    public sealed class AreaTriggerTeleportRecord
    {
        public ulong SniffId;
        public uint AreaTriggerId;
        public uint FromMap;
        public float FromX;
        public float FromY;
        public float FromZ;
        public uint ToMap;
        public float ToX;
        public float ToY;
        public float ToZ;
        public float ToOrientation;
        public int DelayMs;
        public DateTime? SeenUtc;
    }
}
