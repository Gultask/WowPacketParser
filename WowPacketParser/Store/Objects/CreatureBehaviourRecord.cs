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
        public int SelfHits;
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

        /// <summary>0 someone else cast it, 1 the creature itself, 2 the packet did not say.</summary>
        public byte Caster;

        /// <summary>Set when the aura was in the first aura update seen for this guid.</summary>
        public bool OnCreate;
        public int Observations;

        /// <summary>Longest total duration seen, null when the aura never carried one.</summary>
        public int? DurationMs;
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

        /// <summary>The option's own id. 0 on builds before the client started sending one.</summary>
        public int GossipOptionId;
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

    /// <summary>
    /// A vendor's stock, as the client was shown it. Slot is the position in the list, which
    /// AzerothCore's npc_vendor ignores but which orders the rows the way the player saw them.
    /// </summary>
    public sealed class NpcVendorRecord
    {
        public ulong SniffId;
        public uint Entry;
        public int Slot;
        public int ItemId;
        public uint MaxCount;
        public uint ExtendedCost;
        public uint Type;
    }

    /// <summary>
    /// A creature that answered a spell click with a cast. The pairing is what makes the row:
    /// CMSG_SPELL_CLICK names the creature and the SMSG_SPELL_GO that follows names the spell,
    /// so a click with no cast close behind is not evidence of anything.
    /// </summary>
    public sealed class NpcSpellClickRecord
    {
        public ulong SniffId;
        public uint Entry;
        public uint SpellId;
        public uint CastFlags;
        public int DelayMs;
    }

    /// <summary>
    /// The action bar the server sends when a creature is controlled - mind control, charm or
    /// vehicle. This is the only place a creature's own spell list arrives whole and in order,
    /// which is why the index is kept: it is the slot, not a rank.
    ///
    /// Three branches spell this differently. WotLK Classic writes CreatureTemplateSpells with
    /// an index, Cata Classic writes CreatureSpellLists with a position, and the legacy handler
    /// writes SpellsX as a bare list per entry. Reading only one of them silently loses whole
    /// branches, so the collector reads all three and normalises here.
    /// </summary>
    public sealed class CreatureTemplateSpellRecord
    {
        public ulong SniffId;
        public uint Entry;
        public int Index;
        public uint SpellId;
        public string Source;
    }

    /// <summary>An item a creature drops for a quest, from the creature query response.</summary>
    public sealed class CreatureQuestItemRecord
    {
        public ulong SniffId;
        public uint Entry;
        public uint Index;
        public uint ItemId;
    }

    /// <summary>Which gossip menu a creature opened with.</summary>
    public sealed class CreatureGossipRecord
    {
        public ulong SniffId;
        public uint Entry;
        public uint MenuId;
    }

    /// <summary>
    /// One value a creature was seen carrying in an update block - faction, mount, resistances,
    /// npc flags, level, model.
    ///
    /// Long format, one row per distinct value, because these are not constants. Faction is the
    /// clearest case: a quest giver that turns hostile sends a second FactionTemplate later in
    /// the same sniff, and two guids of one entry can differ from each other for the whole of
    /// it. Recording only the last value seen would quietly report the hostile faction as the
    /// creature's own.
    ///
    /// on_create separates the two: a value that arrived in the block that created the creature
    /// is what it spawned with, and anything after that is the world acting on it.
    ///
    /// Resistances carry a caveat of their own. UNIT_FIELD_RESISTANCES is PRIVATE | OWNER |
    /// SPECIAL_INFO, so the server sends it only for a unit the player owns or controls, or when
    /// the client has special info on the target. Absence is not zero.
    /// </summary>
    public sealed class CreatureValueRecord
    {
        public ulong SniffId;
        public uint Entry;
        public uint Map;
        public string Field;
        public decimal Value;
        public int Guids;
        public int OnCreateGuids;
        public int ChangedGuids;
        public int Observations;
    }

    /// <summary>
    /// A creature entering combat, from SMSG_AI_REACTION with a hostile reaction.
    ///
    /// Kept per event rather than per guid because a creature can be pulled, reset and pulled
    /// again, and each pull restarts whatever timer the AI runs on. This is the zero point an
    /// initial cast timer is measured from: the gap to the first SMSG_SPELL_START after it is
    /// the delay AzerothCore stores as CastStartTimeMin/Max, which is a different number from
    /// the gap between two later casts of the same spell.
    /// </summary>
    /// <summary>
    /// The static half of a creature, from SMSG_QUERY_CREATURE_RESPONSE.
    ///
    /// Everything here is a property of the entry that no amount of watching update blocks will
    /// reveal - name, type, family, rank, the modifiers. It is also the only one of the ingest
    /// tables whose values do not need a vote: the server states them.
    ///
    /// The parser also builds a CreatureTemplateNonWDB from update fields - faction, speeds,
    /// flags, attack times. That is deliberately not ingested: entry_value already derives the
    /// same fields across the whole corpus with a guid count and a verdict behind each one,
    /// which is better evidence than one sniff's single guess.
    /// </summary>
    public sealed class CreatureTemplateRecord
    {
        public ulong SniffId;
        public uint Entry;
        public string Name;
        public string FemaleName;
        public string SubName;
        public string TitleAlt;
        public string IconName;
        public uint? Rank;
        public uint? Family;
        public uint? Type;
        public uint? TypeFlags;
        public uint? TypeFlags2;
        public uint? PetSpellDataId;
        public float? HealthModifier;
        public float? ManaModifier;
        public bool RacialLeader;
        public bool Civilian;
        public uint? MovementId;
        public uint? KillCredit1;
        public uint? KillCredit2;
        public uint? RequiredExpansion;
        public uint? VignetteID;
        public uint? UnitClass;
        public int? VerifiedBuild;
    }

    /// <summary>
    /// A display id the query response listed for an entry, kept apart because the count varies:
    /// four fixed slots up to Warlords, a variable list with scale and probability after.
    /// </summary>
    public sealed class CreatureTemplateModelRecord
    {
        public ulong SniffId;
        public uint Entry;
        public uint Index;
        public uint DisplayId;
        public float? DisplayScale;
        public float? Probability;
    }

    public sealed class CreatureAggroRecord
    {
        public ulong SniffId;
        public string Guid;
        public uint Entry;
        public uint Map;
        public DateTime? AggroUtc;
    }
}
