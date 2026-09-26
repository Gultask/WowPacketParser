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
        public int CreatureSpells;

        /// <summary>Packets that arrived while the client was on this map, parsed or gated.</summary>
        public int Packets;

        /// <summary>How many of those never reached a handler because the map was not wanted.</summary>
        public int GatedPackets;
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
        public const string CreatureSpell = "creature_spell_cast";
        public const string SpellTarget = "spell_target";
        public const string SpellDestination = "spell_destination";
        public const string CreatureEquip = "creature_equip";
        public const string Gossip = "gossip";
        public const string AreaTriggerTeleport = "areatrigger_teleport";
        public const string NpcVendor = "npc_vendor";
        public const string NpcSpellClick = "npc_spellclick";
        public const string CreatureTemplateSpell = "creature_template_spell";
        public const string CreatureQuestItem = "creature_quest_item";
        public const string CreatureGossip = "creature_gossip";
        public const string CreatureValue = "creature_value";
        public const string CreatureAggro = "creature_aggro";
        public const string CreatureMelee = "creature_melee";
        public const string CreatureArmor = "creature_armor";
        public const string CreatureXp = "creature_xp";
        public const string CreatureStats = "creature_stats";
        public const string CreatureTemplate = "creature_template";
        public const string CreatureTemplateModel = "creature_template_model";
        public const string GameObjectTemplate = "gameobject_template";
        public const string GameObjectQuestItem = "gameobject_quest_item";
        public const string Trainer = "trainer";
        public const string NpcTrainer = "npc_trainer";
        public const string GossipPoi = "gossip_poi";
        public const string QuestPoi = "quest_poi";
        public const string QuestPoiPoint = "quest_poi_point";
        public const string MapVisit = "map_visit";

        public const int CreatureSpawnVersion = 4;   // 2 added emote/stand/sheathe state, 3 added health, 4 added difficulty
        public const int GameObjectSpawnVersion = 3;   // 2 added the static rotation quaternion, 3 added difficulty
        public const int CreatureWaypointVersion = 6;   // 2 added segment point counts, 3 gates owned creatures, 4 every create-block spline in a packet, 5 drops only in-combat segments, 6 added difficulty
        public const int LootVersion = 2;   // 2 added difficulty
        public const int CreatureMovementVersion = 3;   // 2 sees update packets that carried a create-block spline, 3 added difficulty
        public const int CreatureSpellVersion = 3;   // 2 reads SMSG_SPELL_START on TBC Anniversary, 3 added difficulty
        public const int SpellTargetVersion = 2;   // 2 added player hits and self_hits
        public const int SpellDestinationVersion = 1;
        public const int CreatureEquipVersion = 3;   // 2 merged across sniffs, 3 difficulty in the key
        public const int GossipVersion = 2; // 2 added the option's own id next to the order index
        public const int AreaTriggerTeleportVersion = 1;
        public const int NpcVendorVersion = 1;
        public const int NpcSpellClickVersion = 1;
        public const int CreatureTemplateSpellVersion = 1;
        public const int CreatureQuestItemVersion = 2;   // 2 merged across sniffs
        public const int CreatureGossipVersion = 1;
        public const int CreatureValueVersion = 4;   // 2 sees update packets that carried a create-block spline, 3 merged across sniffs without the in-combat bit, 4 difficulty in the key
        public const int CreatureAggroVersion = 2;   // 2 added difficulty
        public const int CreatureMeleeVersion = 2;   // 2 added difficulty
        public const int CreatureArmorVersion = 2;   // 2 added difficulty
        public const int CreatureXpVersion = 2;   // 2 added difficulty
        public const int CreatureStatsVersion = 2;   // 2 added difficulty
        public const int GameObjectTemplateVersion = 1;
        public const int GameObjectQuestItemVersion = 1;
        public const int TrainerVersion = 1;
        public const int NpcTrainerVersion = 1;
        public const int GossipPoiVersion = 1;
        public const int QuestPoiVersion = 1;
        public const int QuestPoiPointVersion = 1;
        public const int CreatureTemplateVersion = 1;
        public const int CreatureTemplateModelVersion = 1;
        public const int MapVisitVersion = 1;

        public const string StatusOk = "ok";
        public const string StatusEmpty = "empty";
        public const string StatusUnsupported = "unsupported";
    }
}
