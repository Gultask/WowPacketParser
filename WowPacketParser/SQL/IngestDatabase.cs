using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using MySql.Data.MySqlClient;
using WowPacketParser.Enums;
using WowPacketParser.Misc;
using WowPacketParser.Store.Objects;

namespace WowPacketParser.SQL
{
    /// <summary>
    /// Connection to the ingest database - the pile every sniff is poured into.
    /// Separate from <see cref="SQLConnector"/>, which reads the server's world database to
    /// diff against; this one is written to and owns its own schema.
    /// Statements are parameterised rather than built as text: file names reach this code
    /// unescaped, and nothing here is ever meant to become a .sql file.
    /// </summary>
    public static class IngestDatabase
    {
        private static MySqlConnection _conn;
        private static bool _schemaChecked;

        public static bool Enabled => Settings.DumpFormat == DumpFormatType.Database;

        private static string ConnectionString =>
            $"Server={Settings.Server};Port={Settings.Port};User Id={Settings.Username};" +
            $"Password={Settings.Password};Allow User Variables=True;";

        public static bool Connect()
        {
            if (_conn != null && _conn.State == ConnectionState.Open)
                return true;

            try
            {
                _conn = new MySqlConnection(ConnectionString);
                _conn.Open();
                EnsureSchema();
                return true;
            }
            catch (Exception e)
            {
                Trace.WriteLine($"Could not open the ingest database: {e.Message}");
                _conn = null;
                return false;
            }
        }

        public static void Disconnect()
        {
            _conn?.Close();
            _conn = null;
        }

        [SuppressMessage("Microsoft.Security", "CA2100", Justification = "Schema DDL is constant; the database name is validated first.")]
        private static void EnsureSchema()
        {
            if (_schemaChecked)
                return;

            var db = Settings.IngestDatabase;
            foreach (var c in db)
            {
                if (!char.IsLetterOrDigit(c) && c != '_')
                    throw new ArgumentException($"Invalid IngestDatabase name '{db}' - letters, digits and underscore only.");
            }

            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = $"CREATE DATABASE IF NOT EXISTS `{db}` DEFAULT CHARACTER SET utf8mb4;";
                cmd.ExecuteNonQuery();
            }

            _conn.ChangeDatabase(db);

            foreach (var ddl in new[] { SniffTableDdl, GameObjectSpawnTableDdl, CreatureSpawnTableDdl,
                                        CreatureWaypointTableDdl, LootInstanceTableDdl,
                                        LootInstanceItemTableDdl, MapValidityTableDdl, MapValiditySeed,
                                        SniffCoverageTableDdl, SniffMapTableDdl,
                                        CreatureMovementTableDdl, CreatureSpellCastTableDdl,
                                        SpellTargetTableDdl, SpellDestinationTableDdl,
                                        CreatureEquipTableDdl, CreatureAuraTableDdl,
                                        GossipMenuTableDdl, GossipMenuOptionTableDdl,
                                        NpcTextTableDdl, AreaTriggerTeleportTableDdl,
                                        NpcVendorTableDdl, NpcSpellClickTableDdl,
                                        CreatureTemplateSpellTableDdl, CreatureQuestItemTableDdl,
                                        CreatureGossipTableDdl, CreatureValueTableDdl,
                                        CreatureAggroTableDdl, CreatureTemplateTableDdl,
                                        CreatureTemplateModelTableDdl })
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = ddl;
                    cmd.ExecuteNonQuery();
                }
            }

            _schemaChecked = true;
        }


        private const string NpcVendorTableDdl = @"
CREATE TABLE IF NOT EXISTS `npc_vendor` (
  `sniff_id`      BIGINT UNSIGNED NOT NULL,
  `entry`         INT UNSIGNED    NOT NULL,
  `slot`          INT             NOT NULL COMMENT 'position in the list the player was shown',
  `item_id`       INT             NOT NULL COMMENT 'negative means a currency, as the client sends it',
  `max_count`     INT UNSIGNED    NOT NULL COMMENT '0 is unlimited stock',
  `extended_cost` INT UNSIGNED    NOT NULL,
  `type`          INT UNSIGNED    NOT NULL COMMENT '1 item, 2 currency',
  PRIMARY KEY (`sniff_id`, `entry`, `slot`, `item_id`),
  KEY `ix_nvendor_entry` (`entry`),
  KEY `ix_nvendor_item` (`item_id`),
  CONSTRAINT `fk_nvendor_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='A vendor list as the client was shown it. Stock is what the vendor had at that moment, not necessarily its full list.';";

        private const string NpcSpellClickTableDdl = @"
CREATE TABLE IF NOT EXISTS `npc_spellclick` (
  `sniff_id`   BIGINT UNSIGNED NOT NULL,
  `entry`      INT UNSIGNED    NOT NULL,
  `spell_id`   INT UNSIGNED    NOT NULL,
  `cast_flags` INT UNSIGNED    NOT NULL,
  `delay_ms`   INT             NOT NULL COMMENT 'click to cast; a large delay means the pairing is a guess',
  PRIMARY KEY (`sniff_id`, `entry`, `spell_id`),
  KEY `ix_nclick_entry` (`entry`),
  CONSTRAINT `fk_nclick_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='A spell click and the cast it produced. Two packets, paired by time, so delay_ms is how much to trust the row.';";

        private const string CreatureTemplateSpellTableDdl = @"
CREATE TABLE IF NOT EXISTS `creature_template_spell` (
  `sniff_id` BIGINT UNSIGNED NOT NULL,
  `entry`    INT UNSIGNED    NOT NULL,
  `idx`      INT             NOT NULL COMMENT 'action bar slot, not a rank',
  `spell_id` INT UNSIGNED    NOT NULL,
  `source`   VARCHAR(24)     NOT NULL COMMENT 'which of the three branch spellings this came from',
  PRIMARY KEY (`sniff_id`, `entry`, `idx`, `spell_id`),
  KEY `ix_ctspell_entry` (`entry`),
  KEY `ix_ctspell_spell` (`spell_id`),
  CONSTRAINT `fk_ctspell_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='The action bar sent when a creature is controlled - mind control, charm or vehicle. The only place a creature spell list arrives whole and in slot order.';";

        private const string CreatureQuestItemTableDdl = @"
CREATE TABLE IF NOT EXISTS `creature_quest_item` (
  `sniff_id` BIGINT UNSIGNED NOT NULL,
  `entry`    INT UNSIGNED    NOT NULL,
  `idx`      INT UNSIGNED    NOT NULL,
  `item_id`  INT UNSIGNED    NOT NULL,
  PRIMARY KEY (`sniff_id`, `entry`, `idx`),
  KEY `ix_cqitem_entry` (`entry`),
  KEY `ix_cqitem_item` (`item_id`),
  CONSTRAINT `fk_cqitem_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='Quest drops declared by the creature query response.';";

        private const string CreatureGossipTableDdl = @"
CREATE TABLE IF NOT EXISTS `creature_gossip` (
  `sniff_id` BIGINT UNSIGNED NOT NULL,
  `entry`    INT UNSIGNED    NOT NULL,
  `menu_id`  INT UNSIGNED    NOT NULL,
  PRIMARY KEY (`sniff_id`, `entry`, `menu_id`),
  KEY `ix_cgossip_entry` (`entry`),
  KEY `ix_cgossip_menu` (`menu_id`),
  CONSTRAINT `fk_cgossip_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='Which gossip menu a creature opened with. One entry can have more than one, by condition.';";

        private const string CreatureValueTableDdl = @"
CREATE TABLE IF NOT EXISTS `creature_value` (
  `sniff_id`        BIGINT UNSIGNED NOT NULL,
  `entry`           INT UNSIGNED    NOT NULL,
  `map`             INT UNSIGNED    NOT NULL,
  `field`           VARCHAR(24)     NOT NULL,
  `value`           DECIMAL(20,6)   NOT NULL COMMENT 'decimal so reaches, radii and speeds land exactly alongside the integer fields',
  `guids`           INT UNSIGNED    NOT NULL COMMENT 'distinct creatures of this entry seen with this value',
  `on_create_guids` INT UNSIGNED    NOT NULL COMMENT 'how many had it in the block that created them',
  `changed_guids`   INT UNSIGNED    NOT NULL COMMENT 'guids of this entry and field seen with more than one value - a creature that changed, not two that differ',
  `observations`    INT             NOT NULL,
  PRIMARY KEY (`sniff_id`, `entry`, `map`, `field`, `value`),
  KEY `ix_cvalue_entry` (`entry`, `field`, `value`),
  KEY `ix_cvalue_field` (`field`, `value`),
  CONSTRAINT `fk_cvalue_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='What each entry was seen carrying, aggregated within the sniff. Counted by distinct guid because one creature standing in view resends its fields on every update block. Long format because none of these are constants. changed_guids separates a creature that changed from two that always differed. Resistances are PRIVATE|OWNER|SPECIAL_INFO, so their absence is not zero.';";

        private const string CreatureTemplateTableDdl = @"
CREATE TABLE IF NOT EXISTS `creature_template` (
  `sniff_id`           BIGINT UNSIGNED NOT NULL,
  `entry`              INT UNSIGNED    NOT NULL,
  `name`               VARCHAR(200)    NULL,
  `female_name`        VARCHAR(200)    NULL,
  `sub_name`           VARCHAR(200)    NULL,
  `title_alt`          VARCHAR(200)    NULL,
  `icon_name`          VARCHAR(100)    NULL,
  `rank`               INT UNSIGNED    NULL,
  `family`             INT UNSIGNED    NULL,
  `type`               INT UNSIGNED    NULL,
  `type_flags`         INT UNSIGNED    NULL,
  `type_flags2`        INT UNSIGNED    NULL,
  `pet_spell_data_id`  INT UNSIGNED    NULL,
  `health_modifier`    FLOAT           NULL,
  `mana_modifier`      FLOAT           NULL,
  `racial_leader`      TINYINT(1)      NOT NULL,
  `civilian`           TINYINT(1)      NOT NULL,
  `movement_id`        INT UNSIGNED    NULL,
  `kill_credit1`       INT UNSIGNED    NULL,
  `kill_credit2`       INT UNSIGNED    NULL,
  `required_expansion` INT UNSIGNED    NULL,
  `vignette_id`        INT UNSIGNED    NULL,
  `unit_class`         INT UNSIGNED    NULL,
  `verified_build`     INT             NULL,
  PRIMARY KEY (`sniff_id`, `entry`),
  KEY `ix_ctemplate_entry` (`entry`),
  KEY `ix_ctemplate_name` (`name`(64)),
  CONSTRAINT `fk_ctemplate_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='The static half of a creature, stated by SMSG_QUERY_CREATURE_RESPONSE rather than inferred. The fields the query does not carry - faction, speeds, flags - are derived in entry_value instead.';";

        private const string CreatureTemplateModelTableDdl = @"
CREATE TABLE IF NOT EXISTS `creature_template_model` (
  `sniff_id`      BIGINT UNSIGNED NOT NULL,
  `entry`         INT UNSIGNED    NOT NULL,
  `idx`           INT UNSIGNED    NOT NULL,
  `display_id`    INT UNSIGNED    NOT NULL,
  `display_scale` FLOAT           NULL,
  `probability`   FLOAT           NULL,
  PRIMARY KEY (`sniff_id`, `entry`, `idx`),
  KEY `ix_ctmodel_entry` (`entry`),
  KEY `ix_ctmodel_display` (`display_id`),
  CONSTRAINT `fk_ctmodel_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='Display ids per entry. Its own table because the count varies: four fixed slots up to Warlords, a variable list with scale and probability after.';";

        private const string CreatureAggroTableDdl = @"
CREATE TABLE IF NOT EXISTS `creature_aggro` (
  `sniff_id`  BIGINT UNSIGNED NOT NULL,
  `guid`      VARCHAR(40)     NOT NULL,
  `entry`     INT UNSIGNED    NOT NULL,
  `map`       INT UNSIGNED    NOT NULL,
  `aggro_utc` DATETIME(3)     NOT NULL,
  PRIMARY KEY (`sniff_id`, `guid`, `aggro_utc`),
  KEY `ix_caggro_entry` (`entry`, `aggro_utc`),
  CONSTRAINT `fk_caggro_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='Every hostile SMSG_AI_REACTION. One row per pull, not per creature, because each pull restarts the AI timers. The zero point an initial cast timer is measured from.';";

        private const string SniffTableDdl = @"
CREATE TABLE IF NOT EXISTS `sniff` (
  `id`                 BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `file_hash`          CHAR(64)        NOT NULL,
  `file_name`          VARCHAR(512)    NOT NULL,
  `file_size`          BIGINT UNSIGNED NULL,
  `sniffer`            VARCHAR(64)     NULL,
  `sniffer_id`         INT             NULL,
  `sniffer_version`    INT             NULL,
  `pkt_version`        VARCHAR(16)     NULL,
  `client_build`       INT             NULL,
  `client_version`     VARCHAR(32)     NULL,
  `client_locale`      VARCHAR(8)      NULL,
  `branch`             VARCHAR(16)     NULL COMMENT 'Retail, Classic, TBC, WotLK, Cata, MoP',
  `header_start_utc`   DATETIME(3)     NULL,
  `first_packet_utc`   DATETIME(3)     NULL,
  `last_packet_utc`    DATETIME(3)     NULL,
  `utc_offset_seconds` INT             NULL COMMENT 'capturing machine offset from UTC; packet times are UTC regardless',
  `utc_offset_source`  VARCHAR(16)     NOT NULL DEFAULT 'unknown',
  `clock_skew_seconds` INT             NULL COMMENT 'first packet minus header start; near a whole hour means the sniffer wrote local time',
  `packet_count`       INT             NULL,
  `parsed_count`       INT             NULL,
  `error_count`        INT             NULL,
  `skipped_count`      INT             NULL,
  `no_structure_count` INT             NULL,
  `structure_version`  INT UNSIGNED    NULL,
  `ingested_at_utc`    DATETIME(3)     NOT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `uk_sniff_file_hash` (`file_hash`),
  KEY `ix_sniff_build` (`client_build`),
  KEY `ix_sniff_first_packet` (`first_packet_utc`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";

        private const string GameObjectSpawnTableDdl = @"
CREATE TABLE IF NOT EXISTS `gameobject_spawn` (
  `id`             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `sniff_id`       BIGINT UNSIGNED NOT NULL,
  `guid`           VARCHAR(40)     NOT NULL,
  `entry`          INT UNSIGNED    NOT NULL,
  `map`            INT UNSIGNED    NOT NULL,
  `area_id`        INT             NULL,
  `zone_id`        INT             NULL,
  `position_x`     FLOAT           NOT NULL,
  `position_y`     FLOAT           NOT NULL,
  `position_z`     FLOAT           NOT NULL,
  `orientation`    FLOAT           NOT NULL,
  `rotation0`      FLOAT           NULL,
  `rotation1`      FLOAT           NULL,
  `rotation2`      FLOAT           NULL,
  `rotation3`      FLOAT           NULL,
  `create_type`    TINYINT         NOT NULL COMMENT '1 = entered visibility range, 2 = spawned in view',
  `phase_mask`     INT UNSIGNED    NULL,
  `phases`         TEXT            NULL,
  `first_seen_utc` DATETIME(3)     NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `uk_go_sniff_guid` (`sniff_id`, `guid`),
  KEY `ix_go_point` (`map`, `position_x`, `position_y`),
  KEY `ix_go_entry` (`entry`),
  CONSTRAINT `fk_go_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";

        // Dead creatures are never inserted, so there is no flag to filter on later: a corpse
        // sits where it fell rather than where it spawned, and the caller drops them.
        private const string CreatureSpawnTableDdl = @"
CREATE TABLE IF NOT EXISTS `creature_spawn` (
  `id`             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `sniff_id`       BIGINT UNSIGNED NOT NULL,
  `guid`           VARCHAR(40)     NOT NULL,
  `entry`          INT UNSIGNED    NOT NULL,
  `map`            INT UNSIGNED    NOT NULL,
  `area_id`        INT             NULL,
  `zone_id`        INT             NULL,
  `position_x`     FLOAT           NOT NULL,
  `position_y`     FLOAT           NOT NULL,
  `position_z`     FLOAT           NOT NULL,
  `orientation`    FLOAT           NOT NULL,
  `create_type`    TINYINT         NOT NULL COMMENT '1 = entered visibility range, 2 = spawned in view',
  `phase_mask`     INT UNSIGNED    NULL,
  `phases`         TEXT            NULL,
  `health`         BIGINT          NULL COMMENT 'instantaneous, and what tells a corpse from a spawn - not an entry attribute',
  `first_seen_utc` DATETIME(3)     NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `uk_creature_sniff_guid` (`sniff_id`, `guid`),
  KEY `ix_creature_point` (`map`, `position_x`, `position_y`),
  KEY `ix_creature_entry` (`entry`),
  CONSTRAINT `fk_creature_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";

        // Real waypoints only - pathfinding filler is never inserted, and segments belonging to a
        // creature that aggroed are dropped by the caller.
        private const string CreatureWaypointTableDdl = @"
CREATE TABLE IF NOT EXISTS `creature_waypoint` (
  `id`              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `sniff_id`        BIGINT UNSIGNED NOT NULL,
  `guid`            VARCHAR(40)     NOT NULL,
  `entry`           INT UNSIGNED    NOT NULL,
  `map`             INT UNSIGNED    NOT NULL,
  `segment_id`      INT             NOT NULL COMMENT 'points sharing this arrived in one packet',
  `point_index`     INT             NOT NULL,
  `position_x`      FLOAT           NOT NULL,
  `position_y`      FLOAT           NOT NULL,
  `position_z`      FLOAT           NOT NULL,
  `orientation`     FLOAT           NULL COMMENT 'only on the final point, when the packet carried a facing',
  `segment_points`  INT             NOT NULL DEFAULT 1 COMMENT '1 = a single random destination; more = an authored spline',
  `spline_flags`    INT UNSIGNED    NOT NULL,
  `creation_spline` TINYINT(1)      NOT NULL COMMENT '1 = arrived in a CreateObject block, e.g. a flying path',
  `move_time_ms`    INT UNSIGNED    NULL,
  `seen_utc`        DATETIME(3)     NULL,
  PRIMARY KEY (`id`),
  KEY `ix_wp_segment` (`sniff_id`, `guid`, `segment_id`, `point_index`),
  KEY `ix_wp_point` (`map`, `position_x`, `position_y`),
  KEY `ix_wp_entry` (`entry`),
  CONSTRAINT `fk_wp_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";

        // Instances that yielded nothing are stored on purpose - they are the denominator for any
        // drop chance. Loot taken while grouped never gets here; the caller drops it.
        // Items hang off (sniff_id, loot_index) rather than the generated id, so parents and
        // children can both be written in batches without a round trip per row.
        private const string LootInstanceTableDdl = @"
CREATE TABLE IF NOT EXISTS `loot_instance` (
  `id`              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `sniff_id`        BIGINT UNSIGNED NOT NULL,
  `loot_index`      INT             NOT NULL COMMENT 'ordinal within the sniff; items join on this',
  `owner_guid`      VARCHAR(40)     NULL,
  `owner_entry`     INT UNSIGNED    NULL,
  `owner_type`      VARCHAR(16)     NULL,
  `owner_level`     INT             NULL,
  `map`             INT UNSIGNED    NULL,
  `acquire_reason`  TINYINT         NOT NULL,
  `acquire_name`    VARCHAR(32)     NULL,
  `loot_method`     TINYINT         NOT NULL,
  `loot_method_name` VARCHAR(32)    NULL,
  `threshold`       TINYINT         NOT NULL,
  `coins`           INT UNSIGNED    NOT NULL,
  `item_count`      INT             NOT NULL,
  `seen_utc`        DATETIME(3)     NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `uk_loot_sniff_index` (`sniff_id`, `loot_index`),
  KEY `ix_loot_owner_entry` (`owner_entry`),
  CONSTRAINT `fk_loot_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";

        private const string LootInstanceItemTableDdl = @"
CREATE TABLE IF NOT EXISTS `loot_instance_item` (
  `id`             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `sniff_id`       BIGINT UNSIGNED NOT NULL,
  `loot_index`     INT             NOT NULL,
  `slot`           INT             NOT NULL,
  `item_id`        INT             NOT NULL,
  `quantity`       INT UNSIGNED    NOT NULL,
  `ui_type`        INT             NULL,
  `random_prop_id` INT UNSIGNED    NULL,
  `loot_item_type` INT             NULL,
  PRIMARY KEY (`id`),
  KEY `ix_lootitem_parent` (`sniff_id`, `loot_index`),
  KEY `ix_lootitem_item` (`item_id`),
  CONSTRAINT `fk_lootitem_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";

        private const string CreatureMovementTableDdl = @"
CREATE TABLE IF NOT EXISTS `creature_movement` (
  `sniff_id`             BIGINT UNSIGNED NOT NULL,
  `guid`                 VARCHAR(40)     NOT NULL,
  `entry`                INT UNSIGNED    NOT NULL,
  `map`                  INT UNSIGNED    NOT NULL,
  `points`               INT             NOT NULL,
  `segments`             INT             NOT NULL,
  `multi_point_segments` INT             NOT NULL COMMENT 'authored splines; the rest are single random destinations',
  `transitions`          INT             NOT NULL,
  `pauses`               INT             NOT NULL COMMENT 'transitions where movement had finished before the next order arrived',
  `median_x`             FLOAT           NOT NULL,
  `median_y`             FLOAT           NOT NULL,
  `median_z`             FLOAT           NOT NULL,
  `radius`               FLOAT           NOT NULL COMMENT 'furthest destination from the median',
  `radius_robust`        FLOAT           NOT NULL COMMENT 'same at p99, so one excursion cannot define it',
  `first_seen_utc`       DATETIME(3)     NULL,
  `last_seen_utc`        DATETIME(3)     NULL,
  PRIMARY KEY (`sniff_id`, `guid`),
  KEY `ix_move_entry` (`entry`),
  KEY `ix_move_point` (`map`, `median_x`, `median_y`),
  KEY `ix_move_kind` (`multi_point_segments`, `points`),
  CONSTRAINT `fk_move_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='One row per creature per sniff, movement reduced to what the digest needs. Classification is per spawn, never per entry - the same entry can patrol in one place and wander in another.';";

        private const string MapValidityTableDdl = @"
CREATE TABLE IF NOT EXISTS `map_validity` (
  `target`          VARCHAR(16)  NOT NULL COMMENT 'content version being built for, e.g. 3.3.5',
  `map`             INT UNSIGNED NOT NULL,
  `usable_branches` VARCHAR(128) NOT NULL COMMENT 'comma separated ClientBranch names whose terrain matches the target',
  `note`            VARCHAR(255) NULL,
  PRIMARY KEY (`target`, `map`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='Which sniffs terrain-match a target. Says nothing about spawn lists - a Northrend sniff from a modern client has valid coordinates but not necessarily the same creatures.';";

        // Generated from 3.3.5a Map.dbc: a map is usable from the branch of the expansion that
        // introduced it onward, until something rebuilt its terrain. The thirteen exceptions are
        // rebuilds - Cataclysm reshaped the old world and five dungeons, Mists three more,
        // Warlords another three. Every other instance took minor adjustments at most, which is
        // why a Cataclysm capture of Zul'Farrak, or a Shadowlands one of Outland, is still good
        // evidence. IngestMapGate.RebuiltAfter holds the same list and must move with this one.
        private const string MapValiditySeed = @"
INSERT INTO `map_validity` (target, map, usable_branches, note) VALUES
('3.3.5',    0, 'Classic,TBC,WotLK', 'Cataclysm reshaped Eastern Kingdoms'),
('3.3.5',    1, 'Classic,TBC,WotLK', 'Cataclysm reshaped Kalimdor'),
('3.3.5',   13, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',   25, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',   30, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',   33, 'Classic,TBC,WotLK', 'Cataclysm rebuilt Shadowfang Keep'),
('3.3.5',   34, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',   35, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',   36, 'Classic,TBC,WotLK', 'Cataclysm rebuilt Deadmines'),
('3.3.5',   37, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',   42, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',   43, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',   44, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',   47, 'Classic,TBC,WotLK,Cata,MoP', 'Warlords reworked Razorfen Kraul'),
('3.3.5',   48, 'Classic,TBC,WotLK,Cata,MoP', 'Warlords reworked Blackfathom Deeps'),
('3.3.5',   70, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',   90, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  109, 'Classic,TBC,WotLK', 'Cataclysm reworked the Sunken Temple'),
('3.3.5',  129, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  169, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  189, 'Classic,TBC,WotLK,Cata', 'Mists rebuilt Scarlet Monastery'),
('3.3.5',  209, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  229, 'Classic,TBC,WotLK,Cata,MoP', 'Warlords rebuilt Blackrock Spire'),
('3.3.5',  230, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  249, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  269, 'TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  289, 'Classic,TBC,WotLK,Cata', 'Mists rebuilt Scholomance'),
('3.3.5',  309, 'Classic,TBC,WotLK', 'Cataclysm rebuilt Zul''Gurub'),
('3.3.5',  329, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  349, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  369, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  389, 'Classic,TBC,WotLK,Cata', 'Mists revamped Ragefire Chasm'),
('3.3.5',  409, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  429, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  449, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  450, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  451, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  469, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  489, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  509, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  529, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  530, 'TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  531, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  532, 'TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  533, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  534, 'TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  540, 'TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  542, 'TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  543, 'TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  544, 'TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  545, 'TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  546, 'TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  547, 'TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  548, 'TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  550, 'TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  552, 'TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  553, 'TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  554, 'TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  555, 'TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  556, 'TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  557, 'TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  558, 'TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  559, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  560, 'TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  562, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  564, 'TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  565, 'TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  566, 'TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  568, 'TBC,WotLK', 'Cataclysm rebuilt Zul''Aman'),
('3.3.5',  571, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  572, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  573, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  574, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  575, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  576, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  578, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  580, 'TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  582, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  584, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  585, 'TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  586, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  587, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  588, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  589, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  590, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  591, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  592, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  593, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  594, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  595, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  596, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  597, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  598, 'TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  599, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  600, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  601, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  602, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  603, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  604, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  605, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  606, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  607, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  608, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  609, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  610, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  612, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  613, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  614, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  615, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  616, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  617, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  618, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  619, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  620, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  621, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  622, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  623, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  624, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  628, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  631, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  632, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  641, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  642, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  647, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  649, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  650, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  658, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  668, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  672, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  673, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  712, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  713, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  718, 'WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  723, 'Classic,TBC,WotLK,Cata,MoP,Retail', NULL),
('3.3.5',  724, 'WotLK,Cata,MoP,Retail', NULL)
ON DUPLICATE KEY UPDATE usable_branches = VALUES(usable_branches), note = VALUES(note);";

        private const string SniffCoverageTableDdl = @"
CREATE TABLE IF NOT EXISTS `sniff_coverage` (
  `sniff_id`          BIGINT UNSIGNED NOT NULL,
  `capability`        VARCHAR(32)     NOT NULL,
  `status`            VARCHAR(16)     NOT NULL COMMENT 'ok, empty, or unsupported',
  `collector_version` INT             NOT NULL COMMENT 'bumped when the collector learns to extract more',
  `rows_written`      INT             NOT NULL,
  `reason`            VARCHAR(255)    NULL COMMENT 'why, when the status is not ok',
  PRIMARY KEY (`sniff_id`, `capability`),
  KEY `ix_cov_capability` (`capability`, `status`, `collector_version`),
  CONSTRAINT `fk_cov_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='What each sniff could and could not give up. An empty table and an unsupported opcode both look like zero rows afterwards, so the difference is recorded here at parse time.';";

        private const string SniffMapTableDdl = @"
CREATE TABLE IF NOT EXISTS `sniff_map` (
  `sniff_id`          BIGINT UNSIGNED NOT NULL,
  `map`               INT UNSIGNED    NOT NULL,
  `creature_spawns`   INT             NOT NULL DEFAULT 0,
  `gameobject_spawns` INT             NOT NULL DEFAULT 0,
  `waypoints`         INT             NOT NULL DEFAULT 0,
  `loot_instances`    INT             NOT NULL DEFAULT 0,
  `creature_spells`   INT             NOT NULL DEFAULT 0,
  `packets`           INT             NOT NULL DEFAULT 0 COMMENT 'packets that arrived on this map, gated or not',
  `gated_packets`     INT             NOT NULL DEFAULT 0 COMMENT 'of those, never handed to a handler',
  PRIMARY KEY (`sniff_id`, `map`),
  KEY `ix_sniffmap_map` (`map`),
  CONSTRAINT `fk_sniffmap_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='Which maps a sniff touched and how much it yielded on each, so a later run can be scoped by map without opening the files again.';";

        private const string SniffUpsertSql = @"
INSERT INTO `sniff` (file_hash, file_name, file_size, sniffer, sniffer_id, sniffer_version, pkt_version,
                     client_build, client_version, client_locale, branch,
                     header_start_utc, first_packet_utc, last_packet_utc,
                     utc_offset_seconds, utc_offset_source, clock_skew_seconds,
                     packet_count, parsed_count, error_count, skipped_count, no_structure_count,
                     structure_version, ingested_at_utc)
VALUES (@file_hash, @file_name, @file_size, @sniffer, @sniffer_id, @sniffer_version, @pkt_version,
        @client_build, @client_version, @client_locale, @branch,
        @header_start_utc, @first_packet_utc, @last_packet_utc,
        @utc_offset_seconds, @utc_offset_source, @clock_skew_seconds,
        @packet_count, @parsed_count, @error_count, @skipped_count, @no_structure_count,
        @structure_version, @ingested_at_utc)
ON DUPLICATE KEY UPDATE
    id                 = LAST_INSERT_ID(id),
    file_name          = VALUES(file_name),
    file_size          = VALUES(file_size),
    sniffer            = VALUES(sniffer),
    sniffer_id         = VALUES(sniffer_id),
    sniffer_version    = VALUES(sniffer_version),
    pkt_version        = VALUES(pkt_version),
    client_build       = VALUES(client_build),
    client_version     = VALUES(client_version),
    client_locale      = VALUES(client_locale),
    branch             = VALUES(branch),
    header_start_utc   = VALUES(header_start_utc),
    first_packet_utc   = VALUES(first_packet_utc),
    last_packet_utc    = VALUES(last_packet_utc),
    utc_offset_seconds = VALUES(utc_offset_seconds),
    utc_offset_source  = VALUES(utc_offset_source),
    clock_skew_seconds = VALUES(clock_skew_seconds),
    packet_count       = VALUES(packet_count),
    parsed_count       = VALUES(parsed_count),
    error_count        = VALUES(error_count),
    skipped_count      = VALUES(skipped_count),
    no_structure_count = VALUES(no_structure_count),
    structure_version  = VALUES(structure_version),
    ingested_at_utc    = VALUES(ingested_at_utc);";

        /// <summary>
        /// Inserts the sniff, or updates it in place when the same file has been ingested before.
        /// Returns the row id, which everything else parsed out of this sniff should reference.
        /// </summary>
        public static ulong SaveSniff(SniffRecord sniff)
        {
            if (!Connect())
                return 0;

            try
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = SniffUpsertSql;
                    cmd.Parameters.AddWithValue("@file_hash", sniff.FileHash);
                    cmd.Parameters.AddWithValue("@file_name", sniff.FileName);
                    cmd.Parameters.AddWithValue("@file_size", sniff.FileSize);
                    cmd.Parameters.AddWithValue("@sniffer", sniff.Sniffer);
                    cmd.Parameters.AddWithValue("@sniffer_id", sniff.SnifferId);
                    cmd.Parameters.AddWithValue("@sniffer_version", sniff.SnifferVersion);
                    cmd.Parameters.AddWithValue("@pkt_version", sniff.PktVersion);
                    cmd.Parameters.AddWithValue("@client_build", sniff.ClientBuild);
                    cmd.Parameters.AddWithValue("@client_version", sniff.ClientVersion);
                    cmd.Parameters.AddWithValue("@client_locale", sniff.ClientLocale);
                    cmd.Parameters.AddWithValue("@branch", sniff.Branch);
                    cmd.Parameters.AddWithValue("@header_start_utc", sniff.HeaderStartUtc);
                    cmd.Parameters.AddWithValue("@first_packet_utc", sniff.FirstPacketUtc);
                    cmd.Parameters.AddWithValue("@last_packet_utc", sniff.LastPacketUtc);
                    cmd.Parameters.AddWithValue("@utc_offset_seconds", sniff.UtcOffsetSeconds);
                    cmd.Parameters.AddWithValue("@utc_offset_source", sniff.UtcOffsetSource);
                    cmd.Parameters.AddWithValue("@clock_skew_seconds", sniff.ClockSkewSeconds);
                    cmd.Parameters.AddWithValue("@packet_count", sniff.PacketCount);
                    cmd.Parameters.AddWithValue("@parsed_count", sniff.ParsedCount);
                    cmd.Parameters.AddWithValue("@error_count", sniff.ErrorCount);
                    cmd.Parameters.AddWithValue("@skipped_count", sniff.SkippedCount);
                    cmd.Parameters.AddWithValue("@no_structure_count", sniff.NoStructureCount);
                    cmd.Parameters.AddWithValue("@structure_version", sniff.StructureVersion);
                    cmd.Parameters.AddWithValue("@ingested_at_utc", sniff.IngestedAtUtc);
                    cmd.ExecuteNonQuery();
                    sniff.Id = (ulong)cmd.LastInsertedId;
                }
            }
            catch (Exception e)
            {
                Trace.WriteLine($"Could not write the sniff row: {e.Message}");
                return 0;
            }

            return sniff.Id;
        }

        /// <summary>
        /// Replaces this sniff's rows in one table and writes the given ones in batches.
        /// The delete runs even when there is nothing to write, so re-ingesting a sniff that no
        /// longer yields rows clears the stale ones instead of leaving them behind.
        /// Every column after sniff_id is positional: rows must match <paramref name="columns"/>.
        /// </summary>
        [SuppressMessage("Microsoft.Security", "CA2100", Justification = "Table and column names are compile time constants; all values are parameters.")]
        private static int SaveRows(string table, ulong sniffId, string columns, IReadOnlyList<object[]> rows, int batchSize = 500)
        {
            if (sniffId == 0 || !Connect())
                return 0;

            var written = 0;

            try
            {
                using (var clear = _conn.CreateCommand())
                {
                    clear.CommandText = $"DELETE FROM `{table}` WHERE sniff_id = @sniff_id;";
                    clear.Parameters.AddWithValue("@sniff_id", sniffId);
                    clear.ExecuteNonQuery();
                }

                if (rows.Count == 0)
                    return 0;

                for (var offset = 0; offset < rows.Count; offset += batchSize)
                {
                    var batch = Math.Min(batchSize, rows.Count - offset);
                    var sql = new StringBuilder("INSERT INTO `").Append(table).Append("` (sniff_id, ")
                                                                .Append(columns).Append(") VALUES ");

                    using (var cmd = _conn.CreateCommand())
                    {
                        for (var i = 0; i < batch; i++)
                        {
                            var row = rows[offset + i];
                            if (i > 0)
                                sql.Append(',');

                            sql.Append("(@s").Append(i);
                            cmd.Parameters.AddWithValue($"@s{i}", sniffId);

                            for (var c = 0; c < row.Length; c++)
                            {
                                sql.Append(",@p").Append(i).Append('_').Append(c);
                                cmd.Parameters.AddWithValue($"@p{i}_{c}", row[c]);
                            }

                            sql.Append(')');
                        }

                        cmd.CommandText = sql.Append(';').ToString();
                        written += cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception e)
            {
                Trace.WriteLine($"Could not write {table}: {e.Message}");
                return written;
            }

            return written;
        }

        private const string GameObjectSpawnColumns =
            "guid, entry, map, area_id, zone_id, position_x, position_y, position_z, orientation, " +
            "rotation0, rotation1, rotation2, rotation3, create_type, phase_mask, phases, first_seen_utc";

        public static int SaveGameObjectSpawns(ulong sniffId, IReadOnlyList<GameObjectSpawnRecord> spawns)
        {
            var rows = new List<object[]>(spawns.Count);
            foreach (var go in spawns)
            {
                rows.Add(new object[]
                {
                    go.Guid, go.Entry, go.Map, go.AreaId, go.ZoneId,
                    go.PositionX, go.PositionY, go.PositionZ, go.Orientation,
                    go.Rotation0, go.Rotation1, go.Rotation2, go.Rotation3,
                    go.CreateType, go.PhaseMask, go.Phases, go.FirstSeenUtc
                });
            }

            return SaveRows("gameobject_spawn", sniffId, GameObjectSpawnColumns, rows);
        }

        private const string CreatureSpawnColumns =
            "guid, entry, map, area_id, zone_id, position_x, position_y, position_z, orientation, " +
            "create_type, phase_mask, phases, health, first_seen_utc";

        public static int SaveCreatureSpawns(ulong sniffId, IReadOnlyList<CreatureSpawnRecord> spawns)
        {
            var rows = new List<object[]>(spawns.Count);
            foreach (var c in spawns)
            {
                rows.Add(new object[]
                {
                    c.Guid, c.Entry, c.Map, c.AreaId, c.ZoneId,
                    c.PositionX, c.PositionY, c.PositionZ, c.Orientation,
                    c.CreateType, c.PhaseMask, c.Phases, c.Health, c.FirstSeenUtc
                });
            }

            return SaveRows("creature_spawn", sniffId, CreatureSpawnColumns, rows);
        }

        private const string CreatureWaypointColumns =
            "guid, entry, map, segment_id, point_index, position_x, position_y, position_z, " +
            "orientation, segment_points, spline_flags, creation_spline, move_time_ms, seen_utc";

        // Waypoints run to far more rows per sniff than spawns do, so they go in bigger batches.
        public static int SaveCreatureWaypoints(ulong sniffId, IReadOnlyList<CreatureWaypointRecord> points)
        {
            var rows = new List<object[]>(points.Count);
            foreach (var w in points)
            {
                rows.Add(new object[]
                {
                    w.Guid, w.Entry, w.Map, w.SegmentId, w.PointIndex,
                    w.PositionX, w.PositionY, w.PositionZ, w.Orientation, w.SegmentPoints,
                    w.SplineFlags, w.CreationSpline ? 1 : 0, w.MoveTimeMs, w.SeenUtc
                });
            }

            return SaveRows("creature_waypoint", sniffId, CreatureWaypointColumns, rows, 1000);
        }

        private const string LootInstanceColumns =
            "loot_index, owner_guid, owner_entry, owner_type, owner_level, map, acquire_reason, " +
            "acquire_name, loot_method, loot_method_name, threshold, coins, item_count, seen_utc";

        private const string LootInstanceItemColumns =
            "loot_index, slot, item_id, quantity, ui_type, random_prop_id, loot_item_type";

        /// <summary>
        /// Writes loot instances and the items they contained. Both tables are replaced for this
        /// sniff, so an instance never keeps items from an earlier ingest of the same file.
        /// </summary>
        public static int SaveLootInstances(ulong sniffId, IReadOnlyList<LootInstanceRecord> loots)
        {
            var parents = new List<object[]>(loots.Count);
            var items = new List<object[]>();

            for (var i = 0; i < loots.Count; i++)
            {
                var loot = loots[i];
                parents.Add(new object[]
                {
                    i, loot.OwnerGuid, loot.OwnerEntry, loot.OwnerType, loot.OwnerLevel, loot.Map,
                    loot.AcquireReason, loot.AcquireReasonName, loot.LootMethod, loot.LootMethodName,
                    loot.Threshold, loot.Coins, loot.ItemCount, loot.SeenUtc
                });

                foreach (var item in loot.Items)
                {
                    items.Add(new object[]
                    {
                        i, item.Slot, item.ItemId, item.Quantity,
                        item.UiType, item.RandomPropertiesId, item.LootItemType
                    });
                }
            }

            // Children first, so a failure part way through cannot leave items pointing at a
            // parent row that was already replaced.
            SaveRows("loot_instance_item", sniffId, LootInstanceItemColumns, items, 1000);
            return SaveRows("loot_instance", sniffId, LootInstanceColumns, parents);
        }

        private const string CreatureMovementColumns =
            "guid, entry, map, points, segments, multi_point_segments, transitions, pauses, " +
            "median_x, median_y, median_z, radius, radius_robust, first_seen_utc, last_seen_utc";

        public static int SaveCreatureMovement(ulong sniffId, IReadOnlyList<CreatureMovementRecord> moves)
        {
            var rows = new List<object[]>(moves.Count);
            foreach (var m in moves)
            {
                rows.Add(new object[]
                {
                    m.Guid, m.Entry, m.Map, m.Points, m.Segments, m.MultiPointSegments,
                    m.Transitions, m.Pauses, m.MedianX, m.MedianY, m.MedianZ,
                    m.Radius, m.RadiusRobust, m.FirstSeenUtc, m.LastSeenUtc
                });
            }

            return SaveRows("creature_movement", sniffId, CreatureMovementColumns, rows);
        }

        private const string SniffCoverageColumns =
            "capability, status, collector_version, rows_written, reason";

        /// <summary>
        /// Records what this sniff was able to yield. Written even when everything succeeded -
        /// a row saying 'ok at version 2' is what lets a later run skip the file entirely.
        /// </summary>
        public static int SaveCoverage(ulong sniffId, IReadOnlyList<SniffCoverageRecord> coverage)
        {
            var rows = new List<object[]>(coverage.Count);
            foreach (var c in coverage)
                rows.Add(new object[] { c.Capability, c.Status, c.CollectorVersion, c.RowsWritten, c.Reason });

            return SaveRows("sniff_coverage", sniffId, SniffCoverageColumns, rows);
        }


        private const string CreatureSpellCastTableDdl = @"
CREATE TABLE IF NOT EXISTS `creature_spell_cast` (
  `id`          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `sniff_id`    BIGINT UNSIGNED NOT NULL,
  `guid`        VARCHAR(40)     NOT NULL,
  `entry`       INT UNSIGNED    NOT NULL,
  `map`         INT UNSIGNED    NOT NULL,
  `spell_id`    INT UNSIGNED    NOT NULL,
  `started_utc` DATETIME(3)     NULL,
  `completed`   TINYINT(1)      NOT NULL COMMENT 'a matching SMSG_SPELL_GO arrived',
  PRIMARY KEY (`id`),
  KEY `ix_cast_gap` (`sniff_id`, `guid`, `spell_id`, `started_utc`),
  KEY `ix_cast_entry` (`entry`, `spell_id`),
  KEY `ix_cast_spell` (`spell_id`),
  CONSTRAINT `fk_cast_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='Every SMSG_SPELL_START by a creature - when it decided to cast, which is when the cooldown expired. Raw on purpose: the gap between two starts is the timer observation, the timers have no upper bound, and which quantile stands in for a max belongs to whoever derives them. Take gaps per guid, never per entry: two creatures of one entry cast independently and interleaving them invents gaps no cooldown could produce. See scripts/spell-timers.sql.';";

        private const string SpellTargetTableDdl = @"
CREATE TABLE IF NOT EXISTS `spell_target` (
  `sniff_id`     BIGINT UNSIGNED NOT NULL,
  `spell_id`     INT UNSIGNED    NOT NULL,
  `caster_entry` INT UNSIGNED    NOT NULL,
  `caster_type`  VARCHAR(16)     NULL,
  `target_entry` INT UNSIGNED    NOT NULL,
  `target_type`  VARCHAR(16)     NULL,
  `hits`         INT             NOT NULL,
  PRIMARY KEY (`sniff_id`, `spell_id`, `caster_entry`, `target_entry`),
  KEY `ix_starget_spell` (`spell_id`, `target_entry`),
  CONSTRAINT `fk_starget_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='Which entries a spell actually landed on. The empirical answer to what an entry-targeted spell points at: Spell.dbc names no entry for TARGET_UNIT_NEARBY_ENTRY because the entry lives in the server tables, but SMSG_SPELL_GO lists what was hit.';";

        private const string SpellDestinationTableDdl = @"
CREATE TABLE IF NOT EXISTS `spell_destination` (
  `sniff_id`     BIGINT UNSIGNED NOT NULL,
  `spell_id`     INT UNSIGNED    NOT NULL,
  `caster_entry` INT UNSIGNED    NOT NULL,
  `map`          INT UNSIGNED    NOT NULL,
  `position_x`   FLOAT           NOT NULL,
  `position_y`   FLOAT           NOT NULL,
  `position_z`   FLOAT           NOT NULL,
  `orientation`  FLOAT           NULL,
  `casts`        INT             NOT NULL,
  PRIMARY KEY (`sniff_id`, `spell_id`, `caster_entry`, `position_x`, `position_y`, `position_z`),
  KEY `ix_sdest_spell` (`spell_id`),
  CONSTRAINT `fk_sdest_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='Where a spell was aimed when it was aimed at ground rather than a unit - the observations a spell_target_position row is built from.';";

        private const string CreatureEquipTableDdl = @"
CREATE TABLE IF NOT EXISTS `creature_equip` (
  `sniff_id` BIGINT UNSIGNED NOT NULL,
  `guid`     VARCHAR(40)     NOT NULL,
  `entry`    INT UNSIGNED    NOT NULL,
  `map`      INT UNSIGNED    NOT NULL,
  `item_id1` INT UNSIGNED    NOT NULL,
  `item_id2` INT UNSIGNED    NOT NULL,
  `item_id3` INT UNSIGNED    NOT NULL,
  PRIMARY KEY (`sniff_id`, `guid`),
  KEY `ix_cequip_entry` (`entry`),
  CONSTRAINT `fk_cequip_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='Virtual items a creature was drawn holding: main hand, off hand, ranged.';";

        private const string CreatureAuraTableDdl = @"
CREATE TABLE IF NOT EXISTS `creature_aura` (
  `sniff_id`        BIGINT UNSIGNED NOT NULL,
  `guid`            VARCHAR(40)     NOT NULL,
  `entry`           INT UNSIGNED    NOT NULL,
  `map`             INT UNSIGNED    NOT NULL,
  `spell_id`        INT UNSIGNED    NOT NULL,
  `self_cast`       TINYINT(1)      NOT NULL,
  `on_create`       TINYINT(1)      NOT NULL COMMENT 'already present in the block that created the creature',
  `observations`    INT             NOT NULL,
  `max_duration_ms` INT             NULL COMMENT 'null or negative means permanent',
  PRIMARY KEY (`sniff_id`, `guid`, `spell_id`),
  KEY `ix_caura_entry` (`entry`, `spell_id`),
  CONSTRAINT `fk_caura_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='Auras seen on creatures. on_create with a permanent duration is what a spawn is meant to start with; anything else may be a player acting on it.';";

        private const string GossipMenuTableDdl = @"
CREATE TABLE IF NOT EXISTS `gossip_menu` (
  `sniff_id`       BIGINT UNSIGNED NOT NULL,
  `menu_id`        INT UNSIGNED    NOT NULL,
  `text_id`        INT UNSIGNED    NOT NULL,
  `creature_entry` INT UNSIGNED    NOT NULL,
  `observations`   INT             NOT NULL,
  PRIMARY KEY (`sniff_id`, `menu_id`, `text_id`, `creature_entry`),
  KEY `ix_gmenu_menu` (`menu_id`),
  KEY `ix_gmenu_entry` (`creature_entry`),
  CONSTRAINT `fk_gmenu_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";

        private const string GossipMenuOptionTableDdl = @"
CREATE TABLE IF NOT EXISTS `gossip_menu_option` (
  `sniff_id`     BIGINT UNSIGNED NOT NULL,
  `menu_id`      INT UNSIGNED    NOT NULL,
  `option_index` INT UNSIGNED    NOT NULL,
  `option_icon`  INT             NOT NULL,
  `option_text`  TEXT            NULL,
  `box_money`    INT UNSIGNED    NOT NULL,
  `box_coded`    TINYINT(1)      NOT NULL,
  `box_text`     TEXT            NULL,
  PRIMARY KEY (`sniff_id`, `menu_id`, `option_index`),
  KEY `ix_gopt_menu` (`menu_id`),
  CONSTRAINT `fk_gopt_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";

        private const string NpcTextTableDdl = @"
CREATE TABLE IF NOT EXISTS `npc_text` (
  `sniff_id`          BIGINT UNSIGNED NOT NULL,
  `text_id`           INT UNSIGNED    NOT NULL,
  `slot`              INT             NOT NULL,
  `probability`       FLOAT           NOT NULL,
  `text0`             TEXT            NULL,
  `text1`             TEXT            NULL,
  `language`          INT UNSIGNED    NOT NULL,
  `broadcast_text_id` INT UNSIGNED    NOT NULL,
  PRIMARY KEY (`sniff_id`, `text_id`, `slot`),
  KEY `ix_npctext_text` (`text_id`),
  CONSTRAINT `fk_npctext_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";

        private const string AreaTriggerTeleportTableDdl = @"
CREATE TABLE IF NOT EXISTS `areatrigger_teleport` (
  `id`              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `sniff_id`        BIGINT UNSIGNED NOT NULL,
  `areatrigger_id`  INT UNSIGNED    NOT NULL,
  `from_map`        INT UNSIGNED    NOT NULL,
  `from_x`          FLOAT           NOT NULL,
  `from_y`          FLOAT           NOT NULL,
  `from_z`          FLOAT           NOT NULL,
  `to_map`          INT UNSIGNED    NOT NULL,
  `to_x`            FLOAT           NOT NULL,
  `to_y`            FLOAT           NOT NULL,
  `to_z`            FLOAT           NOT NULL,
  `to_orientation`  FLOAT           NOT NULL,
  `delay_ms`        INT             NOT NULL COMMENT 'trigger to new world; a long delay means the pairing is a guess',
  `seen_utc`        DATETIME(3)     NULL,
  PRIMARY KEY (`id`),
  KEY `ix_attele_sniff` (`sniff_id`),
  KEY `ix_attele_trigger` (`areatrigger_id`),
  CONSTRAINT `fk_attele_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='A client area trigger paired with the world change it produced. The teleport is never in one packet, so the pairing is by adjacency in time and delay_ms is how much to trust it.';";

        private const string CreatureSpellCastColumns =
            "guid, entry, map, spell_id, started_utc, completed";

        public static int SaveCreatureSpellCasts(ulong sniffId, IReadOnlyList<CreatureSpellCastRecord> casts)
        {
            var rows = new List<object[]>(casts.Count);
            foreach (var c in casts)
                rows.Add(new object[] { c.Guid, c.Entry, c.Map, c.SpellId, c.StartedUtc, c.Completed ? 1 : 0 });

            return SaveRows("creature_spell_cast", sniffId, CreatureSpellCastColumns, rows, 1000);
        }

        private const string SpellTargetColumns =
            "spell_id, caster_entry, caster_type, target_entry, target_type, hits";

        public static int SaveSpellTargets(ulong sniffId, IReadOnlyList<SpellTargetRecord> targets)
        {
            var rows = new List<object[]>(targets.Count);
            foreach (var t in targets)
                rows.Add(new object[] { t.SpellId, t.CasterEntry, t.CasterType, t.TargetEntry, t.TargetType, t.Hits });

            return SaveRows("spell_target", sniffId, SpellTargetColumns, rows, 1000);
        }

        private const string SpellDestinationColumns =
            "spell_id, caster_entry, map, position_x, position_y, position_z, orientation, casts";

        public static int SaveSpellDestinations(ulong sniffId, IReadOnlyList<SpellDestinationRecord> dests)
        {
            var rows = new List<object[]>(dests.Count);
            foreach (var d in dests)
            {
                rows.Add(new object[]
                {
                    d.SpellId, d.CasterEntry, d.Map, d.PositionX, d.PositionY, d.PositionZ,
                    d.Orientation, d.Casts
                });
            }

            return SaveRows("spell_destination", sniffId, SpellDestinationColumns, rows, 1000);
        }

        private const string NpcVendorColumns = "entry, slot, item_id, max_count, extended_cost, type";

        public static int SaveNpcVendors(ulong sniffId, IReadOnlyList<NpcVendorRecord> vendors)
        {
            var rows = new List<object[]>(vendors.Count);
            foreach (var v in vendors)
                rows.Add(new object[] { v.Entry, v.Slot, v.ItemId, v.MaxCount, v.ExtendedCost, v.Type });

            return SaveRows("npc_vendor", sniffId, NpcVendorColumns, rows);
        }

        private const string NpcSpellClickColumns = "entry, spell_id, cast_flags, delay_ms";

        public static int SaveNpcSpellClicks(ulong sniffId, IReadOnlyList<NpcSpellClickRecord> clicks)
        {
            var rows = new List<object[]>(clicks.Count);
            foreach (var c in clicks)
                rows.Add(new object[] { c.Entry, c.SpellId, c.CastFlags, c.DelayMs });

            return SaveRows("npc_spellclick", sniffId, NpcSpellClickColumns, rows);
        }

        private const string CreatureTemplateSpellColumns = "entry, idx, spell_id, source";

        public static int SaveCreatureTemplateSpells(ulong sniffId, IReadOnlyList<CreatureTemplateSpellRecord> spells)
        {
            var rows = new List<object[]>(spells.Count);
            foreach (var s in spells)
                rows.Add(new object[] { s.Entry, s.Index, s.SpellId, s.Source });

            return SaveRows("creature_template_spell", sniffId, CreatureTemplateSpellColumns, rows);
        }

        private const string CreatureQuestItemColumns = "entry, idx, item_id";

        public static int SaveCreatureQuestItems(ulong sniffId, IReadOnlyList<CreatureQuestItemRecord> items)
        {
            var rows = new List<object[]>(items.Count);
            foreach (var i in items)
                rows.Add(new object[] { i.Entry, i.Index, i.ItemId });

            return SaveRows("creature_quest_item", sniffId, CreatureQuestItemColumns, rows);
        }

        private const string CreatureGossipColumns = "entry, menu_id";

        public static int SaveCreatureGossips(ulong sniffId, IReadOnlyList<CreatureGossipRecord> gossips)
        {
            var rows = new List<object[]>(gossips.Count);
            foreach (var g in gossips)
                rows.Add(new object[] { g.Entry, g.MenuId });

            return SaveRows("creature_gossip", sniffId, CreatureGossipColumns, rows);
        }

        private const string CreatureValueColumns =
            "entry, map, field, value, guids, on_create_guids, changed_guids, observations";

        public static int SaveCreatureValues(ulong sniffId, IReadOnlyList<CreatureValueRecord> values)
        {
            var rows = new List<object[]>(values.Count);
            foreach (var v in values)
            {
                rows.Add(new object[]
                {
                    v.Entry, v.Map, v.Field, v.Value, v.Guids, v.OnCreateGuids,
                    v.ChangedGuids, v.Observations
                });
            }

            return SaveRows("creature_value", sniffId, CreatureValueColumns, rows, 1000);
        }

        private const string CreatureTemplateColumns =
            "entry, name, female_name, sub_name, title_alt, icon_name, `rank`, family, type, " +
            "type_flags, type_flags2, pet_spell_data_id, health_modifier, mana_modifier, " +
            "racial_leader, civilian, movement_id, kill_credit1, kill_credit2, " +
            "required_expansion, vignette_id, unit_class, verified_build";

        public static int SaveCreatureTemplates(ulong sniffId, IReadOnlyList<CreatureTemplateRecord> templates)
        {
            var rows = new List<object[]>(templates.Count);
            foreach (var t in templates)
            {
                rows.Add(new object[]
                {
                    t.Entry, t.Name, t.FemaleName, t.SubName, t.TitleAlt, t.IconName, t.Rank,
                    t.Family, t.Type, t.TypeFlags, t.TypeFlags2, t.PetSpellDataId,
                    t.HealthModifier, t.ManaModifier, t.RacialLeader ? 1 : 0, t.Civilian ? 1 : 0,
                    t.MovementId, t.KillCredit1, t.KillCredit2, t.RequiredExpansion,
                    t.VignetteID, t.UnitClass, t.VerifiedBuild
                });
            }

            return SaveRows("creature_template", sniffId, CreatureTemplateColumns, rows);
        }

        private const string CreatureTemplateModelColumns =
            "entry, idx, display_id, display_scale, probability";

        public static int SaveCreatureTemplateModels(ulong sniffId, IReadOnlyList<CreatureTemplateModelRecord> models)
        {
            var rows = new List<object[]>(models.Count);
            foreach (var m in models)
                rows.Add(new object[] { m.Entry, m.Index, m.DisplayId, m.DisplayScale, m.Probability });

            return SaveRows("creature_template_model", sniffId, CreatureTemplateModelColumns, rows);
        }

        private const string CreatureAggroColumns = "guid, entry, map, aggro_utc";

        public static int SaveCreatureAggro(ulong sniffId, IReadOnlyList<CreatureAggroRecord> aggro)
        {
            var rows = new List<object[]>(aggro.Count);
            foreach (var a in aggro)
                rows.Add(new object[] { a.Guid, a.Entry, a.Map, a.AggroUtc });

            return SaveRows("creature_aggro", sniffId, CreatureAggroColumns, rows, 1000);
        }

        private const string CreatureEquipColumns = "guid, entry, map, item_id1, item_id2, item_id3";

        public static int SaveCreatureEquipment(ulong sniffId, IReadOnlyList<CreatureEquipRecord> equip)
        {
            var rows = new List<object[]>(equip.Count);
            foreach (var e in equip)
                rows.Add(new object[] { e.Guid, e.Entry, e.Map, e.ItemId1, e.ItemId2, e.ItemId3 });

            return SaveRows("creature_equip", sniffId, CreatureEquipColumns, rows);
        }

        private const string CreatureAuraColumns =
            "guid, entry, map, spell_id, self_cast, on_create, observations, max_duration_ms";

        public static int SaveCreatureAuras(ulong sniffId, IReadOnlyList<CreatureAuraRecord> auras)
        {
            var rows = new List<object[]>(auras.Count);
            foreach (var a in auras)
            {
                rows.Add(new object[]
                {
                    a.Guid, a.Entry, a.Map, a.SpellId, a.SelfCast ? 1 : 0, a.OnCreate ? 1 : 0,
                    a.Observations, a.MaxDurationMs
                });
            }

            return SaveRows("creature_aura", sniffId, CreatureAuraColumns, rows, 1000);
        }

        private const string GossipMenuColumns = "menu_id, text_id, creature_entry, observations";

        public static int SaveGossipMenus(ulong sniffId, IReadOnlyList<GossipMenuRecord> menus)
        {
            var rows = new List<object[]>(menus.Count);
            foreach (var m in menus)
                rows.Add(new object[] { m.MenuId, m.TextId, m.CreatureEntry, m.Observations });

            return SaveRows("gossip_menu", sniffId, GossipMenuColumns, rows);
        }

        private const string GossipMenuOptionColumns =
            "menu_id, option_index, option_icon, option_text, box_money, box_coded, box_text";

        public static int SaveGossipMenuOptions(ulong sniffId, IReadOnlyList<GossipMenuOptionRecord> options)
        {
            var rows = new List<object[]>(options.Count);
            foreach (var o in options)
            {
                rows.Add(new object[]
                {
                    o.MenuId, o.OptionIndex, o.OptionIcon, o.OptionText, o.BoxMoney,
                    o.BoxCoded ? 1 : 0, o.BoxText
                });
            }

            return SaveRows("gossip_menu_option", sniffId, GossipMenuOptionColumns, rows);
        }

        private const string NpcTextColumns =
            "text_id, slot, probability, text0, text1, language, broadcast_text_id";

        public static int SaveNpcTexts(ulong sniffId, IReadOnlyList<NpcTextRecord> texts)
        {
            var rows = new List<object[]>(texts.Count);
            foreach (var t in texts)
            {
                rows.Add(new object[]
                {
                    t.TextId, t.Slot, t.Probability, t.Text0, t.Text1, t.Language, t.BroadcastTextId
                });
            }

            return SaveRows("npc_text", sniffId, NpcTextColumns, rows);
        }

        private const string AreaTriggerTeleportColumns =
            "areatrigger_id, from_map, from_x, from_y, from_z, to_map, to_x, to_y, to_z, " +
            "to_orientation, delay_ms, seen_utc";

        public static int SaveAreaTriggerTeleports(ulong sniffId, IReadOnlyList<AreaTriggerTeleportRecord> teleports)
        {
            var rows = new List<object[]>(teleports.Count);
            foreach (var t in teleports)
            {
                rows.Add(new object[]
                {
                    t.AreaTriggerId, t.FromMap, t.FromX, t.FromY, t.FromZ,
                    t.ToMap, t.ToX, t.ToY, t.ToZ, t.ToOrientation, t.DelayMs, t.SeenUtc
                });
            }

            return SaveRows("areatrigger_teleport", sniffId, AreaTriggerTeleportColumns, rows);
        }

        private const string SniffMapColumns =
            "map, creature_spawns, gameobject_spawns, waypoints, loot_instances, creature_spells, " +
            "packets, gated_packets";

        public static int SaveSniffMaps(ulong sniffId, IReadOnlyList<SniffMapRecord> maps)
        {
            var rows = new List<object[]>(maps.Count);
            foreach (var m in maps)
                rows.Add(new object[] { m.Map, m.CreatureSpawns, m.GameObjectSpawns, m.Waypoints,
                                        m.LootInstances, m.CreatureSpells, m.Packets, m.GatedPackets });

            return SaveRows("sniff_map", sniffId, SniffMapColumns, rows);
        }
    }
}
