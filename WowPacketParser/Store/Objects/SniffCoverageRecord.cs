namespace WowPacketParser.Store.Objects
{
    /// <summary>
    /// What one sniff was actually able to give us, per kind of data.
    ///
    /// The point is to never re-parse the whole corpus again. A sniff that produced no loot
    /// because its build's opcode table has no SMSG_LOOT_RESPONSE is a permanent gap until that
    /// opcode is mapped; a sniff that produced no loot because the player looted nothing is
    /// finished business. Both look like zero rows in loot_instance, so the difference has to be
    /// recorded here at parse time - it cannot be recovered afterwards.
    ///
    /// <see cref="CollectorVersion"/> is the other half: when a collector learns to extract
    /// something it used to miss, its version is bumped, and every sniff still carrying the old
    /// number is exactly the re-parse work list.
    /// </summary>
    public sealed class SniffCoverageRecord
    {
        public string Capability;
        public string Status;
        public int CollectorVersion;
        public int RowsWritten;
        public string Reason;
    }

    /// <summary>
    /// Per map totals for one sniff, so a run can be scoped without opening files again. Instance
    /// sniffs are the expensive ones - a handful of raid logs carry a third of the corpus's
    /// packets - and this is what lets them be filtered out by map rather than by file name.
    /// </summary>
    public sealed class SniffMapRecord
    {
        public uint Map;
        public int CreatureSpawns;
        public int GameObjectSpawns;
        public int Waypoints;
        public int LootInstances;
    }

    /// <summary>
    /// Bump one of these when its collector starts extracting something it previously dropped.
    /// Sniffs recorded under a lower number are then selectable as the work list for a re-parse.
    /// </summary>
    public static class CollectorVersion
    {
        public const string CreatureSpawn = "creature_spawn";
        public const string GameObjectSpawn = "gameobject_spawn";
        public const string CreatureWaypoint = "creature_waypoint";
        public const string Loot = "loot";
        public const string CreatureMovement = "creature_movement";

        public const int CreatureSpawnVersion = 3;   // 2 added emote/stand/sheathe state, 3 added health
        public const int GameObjectSpawnVersion = 2;   // 2 added the static rotation quaternion
        public const int CreatureWaypointVersion = 2;   // 2 added segment point counts
        public const int LootVersion = 1;
        public const int CreatureMovementVersion = 1;

        public const string StatusOk = "ok";
        public const string StatusEmpty = "empty";
        public const string StatusUnsupported = "unsupported";
    }
}
