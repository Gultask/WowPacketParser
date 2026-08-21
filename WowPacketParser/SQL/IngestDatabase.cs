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
                                        CreatureMovementTableDdl })
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = ddl;
                    cmd.ExecuteNonQuery();
                }
            }

            _schemaChecked = true;
        }

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
  `level`          INT             NULL,
  `faction`        INT             NULL,
  `unit_flags`     INT UNSIGNED    NULL,
  `health`         BIGINT          NULL,
  `emote_state`    INT             NULL,
  `stand_state`    TINYINT UNSIGNED NULL,
  `sheathe_state`  TINYINT UNSIGNED NULL,
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

        private const string MapValiditySeed = @"
INSERT IGNORE INTO `map_validity` (target, map, usable_branches, note) VALUES
('3.3.5',   0, 'Classic,TBC,WotLK',              'Cataclysm reshaped Eastern Kingdoms'),
('3.3.5',   1, 'Classic,TBC,WotLK',              'Cataclysm reshaped Kalimdor'),
('3.3.5', 530, 'TBC,WotLK,Cata,MoP,Retail',      'Outland unchanged since TBC'),
('3.3.5', 571, 'WotLK,Cata,MoP,Retail',          'Northrend unchanged since WotLK'),
('3.3.5', 189, 'Classic,TBC,WotLK,Cata',         'Scarlet Monastery rebuilt in MoP');";

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
            "create_type, phase_mask, phases, level, faction, unit_flags, health, " +
            "emote_state, stand_state, sheathe_state, first_seen_utc";

        public static int SaveCreatureSpawns(ulong sniffId, IReadOnlyList<CreatureSpawnRecord> spawns)
        {
            var rows = new List<object[]>(spawns.Count);
            foreach (var c in spawns)
            {
                rows.Add(new object[]
                {
                    c.Guid, c.Entry, c.Map, c.AreaId, c.ZoneId,
                    c.PositionX, c.PositionY, c.PositionZ, c.Orientation,
                    c.CreateType, c.PhaseMask, c.Phases, c.Level, c.FactionTemplate, c.UnitFlags, c.Health,
                    c.EmoteState, c.StandState, c.SheatheState, c.FirstSeenUtc
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

        private const string SniffMapColumns =
            "map, creature_spawns, gameobject_spawns, waypoints, loot_instances";

        public static int SaveSniffMaps(ulong sniffId, IReadOnlyList<SniffMapRecord> maps)
        {
            var rows = new List<object[]>(maps.Count);
            foreach (var m in maps)
                rows.Add(new object[] { m.Map, m.CreatureSpawns, m.GameObjectSpawns, m.Waypoints, m.LootInstances });

            return SaveRows("sniff_map", sniffId, SniffMapColumns, rows);
        }
    }
}
