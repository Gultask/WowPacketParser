using System;

namespace WowPacketParser.Store.Objects
{
    /// <summary>
    /// One observed creature instance, as seen in one sniff.
    ///
    /// Only creatures that are plausibly world spawns get this far. Pets, guardians, totems and
    /// anything else with an owner are excluded by <see cref="Unit.IsTemporarySpawn"/>, which
    /// already tests SummonedBy, CreatedBy, CreatedBySpell and DemonCreator. Corpses are
    /// excluded at collection time rather than flagged - a dead creature's position is where it
    /// fell, not where it spawned, and keeping them only leaves noise to filter out later.
    ///
    /// Guids never repeat between sniffs, so cross sniff identity has to be spatial: rows are
    /// matched up by position, not by guid.
    /// </summary>
    public sealed class CreatureSpawnRecord
    {
        public ulong SniffId;

        public string Guid;
        public uint Entry;
        public uint Map;
        public int? AreaId;
        public int? ZoneId;

        public float PositionX;
        public float PositionY;
        public float PositionZ;
        public float Orientation;

        /// <summary>1 = entered visibility range, 2 = created in the world while watching.</summary>
        public int CreateType;

        public uint? PhaseMask;
        public string Phases;

        public int? Level;
        /// <summary>Lets elite and rank be told apart without consulting a world database.</summary>
        public long? Health;
        public int? FactionTemplate;
        public uint? UnitFlags;

        // The three states AzerothCore's creature_addon carries: emote, and the two packed into
        // bytes1/bytes2. Stored raw and separately rather than pre-packed, because the packing
        // differs between cores and a sniff should record what the server said, not one core's
        // encoding of it. AnimTier and VisFlags share bytes1 with StandState and are one line
        // away here if the addon table ever needs rebuilding wholesale.
        public int? EmoteState;
        public byte? StandState;
        public byte? SheatheState;

        public DateTime? FirstSeenUtc;
    }
}
