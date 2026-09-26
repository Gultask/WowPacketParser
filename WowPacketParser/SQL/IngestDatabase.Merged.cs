using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using WowPacketParser.Misc;
using WowPacketParser.Store.Objects;

namespace WowPacketParser.SQL
{
    public static partial class IngestDatabase
    {
        // Tables that hold one row per distinct thing rather than one per sniff. The same creature
        // template, the same faction on the same entry, is stated by hundreds of sniffs, and a row
        // for each only multiplies the table; what the analysis wants is how many said it. These
        // carry the branch in their key instead of a sniff id, and a `sniffs` count.
        //
        // A sniff adds to them once. Its rows cannot be taken back out, so a re-ingest of a sniff
        // whose coverage row already names the capability writes nothing; rebuilding one of these
        // means emptying the table and ingesting again.

        private const string CreatureValueTableDdl = @"
CREATE TABLE IF NOT EXISTS `creature_value` (
  `entry`           INT UNSIGNED    NOT NULL,
  `map`             INT UNSIGNED    NOT NULL,
  `difficulty`      SMALLINT UNSIGNED NOT NULL DEFAULT 65535 COMMENT 'DifficultyID of the stay the creature was created in; 65535 when the sniff never said, since a key cannot hold NULL',
  `branch`          VARCHAR(16)     NOT NULL,
  `field`           VARCHAR(24)     NOT NULL,
  `value`           DECIMAL(20,6)   NOT NULL COMMENT 'decimal so reaches, radii and speeds land exactly alongside the integer fields; unit_flags without the in-combat bit',
  `sniffs`          INT UNSIGNED    NOT NULL,
  `guids`           INT UNSIGNED    NOT NULL COMMENT 'creature sightings with this value: distinct guids per sniff, summed',
  `on_create_guids` INT UNSIGNED    NOT NULL COMMENT 'how many had it in the block that created them',
  `changed_guids`   INT UNSIGNED    NOT NULL COMMENT 'sightings of a creature that held more than one value for this field - one that changed, not two that differ',
  `observations`    BIGINT UNSIGNED NOT NULL,
  `first_build`     INT             NOT NULL,
  `last_build`      INT             NOT NULL,
  PRIMARY KEY (`entry`, `map`, `difficulty`, `branch`, `field`, `value`),
  KEY `ix_cvalue_field` (`field`, `value`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='What each entry was seen carrying, over the whole corpus. Counted by distinct guid within a sniff, because one creature standing in view resends its fields on every update block. Resistances are PRIVATE|OWNER|SPECIAL_INFO, so their absence is not zero.';";

        private const string CreatureEquipTableDdl = @"
CREATE TABLE IF NOT EXISTS `creature_equip` (
  `entry`       INT UNSIGNED NOT NULL,
  `map`         INT UNSIGNED NOT NULL,
  `difficulty`  SMALLINT UNSIGNED NOT NULL DEFAULT 65535 COMMENT 'DifficultyID of the stay the creature was created in; 65535 when the sniff never said, since a key cannot hold NULL',
  `branch`      VARCHAR(16)  NOT NULL,
  `item_id1`    INT UNSIGNED NOT NULL,
  `item_id2`    INT UNSIGNED NOT NULL,
  `item_id3`    INT UNSIGNED NOT NULL,
  `sniffs`      INT UNSIGNED NOT NULL,
  `guids`       INT UNSIGNED NOT NULL COMMENT 'creature sightings holding this set, summed over sniffs',
  `first_build` INT          NOT NULL,
  `last_build`  INT          NOT NULL,
  PRIMARY KEY (`entry`, `map`, `difficulty`, `branch`, `item_id1`, `item_id2`, `item_id3`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='Virtual items a creature was drawn holding: main hand, off hand, ranged.';";

        private const string CreatureQuestItemTableDdl = @"
CREATE TABLE IF NOT EXISTS `creature_quest_item` (
  `entry`       INT UNSIGNED NOT NULL,
  `branch`      VARCHAR(16)  NOT NULL,
  `idx`         INT UNSIGNED NOT NULL,
  `item_id`     INT UNSIGNED NOT NULL,
  `sniffs`      INT UNSIGNED NOT NULL,
  `first_build` INT          NOT NULL,
  `last_build`  INT          NOT NULL,
  PRIMARY KEY (`entry`, `branch`, `idx`, `item_id`),
  KEY `ix_cqitem_item` (`item_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='Quest drops declared by the creature query response.';";

        private const string GameObjectTemplateTableDdl = @"
CREATE TABLE IF NOT EXISTS `gameobject_template` (
  `entry`              INT UNSIGNED  NOT NULL,
  `branch`             VARCHAR(16)   NOT NULL,
  `variant`            CHAR(32)      NOT NULL COMMENT 'MD5 of every column after it; two builds that disagree about an entry keep a row each',
  `type`               INT UNSIGNED  NULL,
  `display_id`         INT UNSIGNED  NULL,
  `name`               VARCHAR(200)  NULL,
  `icon_name`          VARCHAR(100)  NULL,
  `cast_bar_caption`   VARCHAR(200)  NULL,
  `unk1`               VARCHAR(200)  NULL,
  `size`               FLOAT         NULL,
  `data`               VARCHAR(512)  NULL COMMENT 'Data0..DataN, comma separated, as many as the build sends',
  `required_level`     INT           NULL,
  `content_tuning_id`  INT           NULL,
  `sniffs`             INT UNSIGNED  NOT NULL,
  `first_build`        INT           NOT NULL,
  `last_build`         INT           NOT NULL,
  PRIMARY KEY (`entry`, `branch`, `variant`),
  KEY `ix_gotemplate_name` (`name`(64))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='SMSG_QUERY_GAME_OBJECT_RESPONSE, one row per distinct answer.';";

        private const string GameObjectQuestItemTableDdl = @"
CREATE TABLE IF NOT EXISTS `gameobject_quest_item` (
  `entry`       INT UNSIGNED NOT NULL,
  `branch`      VARCHAR(16)  NOT NULL,
  `idx`         INT UNSIGNED NOT NULL,
  `item_id`     INT UNSIGNED NOT NULL,
  `sniffs`      INT UNSIGNED NOT NULL,
  `first_build` INT          NOT NULL,
  `last_build`  INT          NOT NULL,
  PRIMARY KEY (`entry`, `branch`, `idx`, `item_id`),
  KEY `ix_goqitem_item` (`item_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='Quest drops declared by the gameobject query response.';";

        private const string TrainerTableDdl = @"
CREATE TABLE IF NOT EXISTS `trainer` (
  `trainer_id`  INT UNSIGNED NOT NULL COMMENT 'the creature entry on builds that send no trainer id',
  `branch`      VARCHAR(16)  NOT NULL,
  `variant`     CHAR(32)     NOT NULL COMMENT 'MD5 of type and greeting',
  `type`        INT UNSIGNED NULL,
  `greeting`    TEXT         NULL,
  `sniffs`      INT UNSIGNED NOT NULL,
  `first_build` INT          NOT NULL,
  `last_build`  INT          NOT NULL,
  PRIMARY KEY (`trainer_id`, `branch`, `variant`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='SMSG_TRAINER_LIST headers.';";

        private const string NpcTrainerTableDdl = @"
CREATE TABLE IF NOT EXISTS `npc_trainer` (
  `entry`          INT UNSIGNED NOT NULL COMMENT 'the creature that showed the list; 0 when the parser could not tie it to one',
  `trainer_id`     INT UNSIGNED NOT NULL,
  `branch`         VARCHAR(16)  NOT NULL,
  `spell_id`       INT UNSIGNED NOT NULL,
  `money_cost`     INT UNSIGNED NOT NULL COMMENT 'as sent - after any reputation discount',
  `req_skill_line` INT UNSIGNED NOT NULL,
  `req_skill_rank` INT UNSIGNED NOT NULL,
  `req_ability1`   INT UNSIGNED NOT NULL,
  `req_ability2`   INT UNSIGNED NOT NULL,
  `req_ability3`   INT UNSIGNED NOT NULL,
  `req_level`      INT UNSIGNED NOT NULL,
  `sniffs`         INT UNSIGNED NOT NULL,
  `first_build`    INT          NOT NULL,
  `last_build`     INT          NOT NULL,
  PRIMARY KEY (`entry`, `trainer_id`, `branch`, `spell_id`, `money_cost`, `req_skill_line`, `req_skill_rank`,
               `req_ability1`, `req_ability2`, `req_ability3`, `req_level`),
  KEY `ix_ntrainer_trainer` (`trainer_id`, `spell_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='Every spell a trainer offered. A cost that differs between rows of one spell is usually a reputation discount.';";

        private const string GossipPoiTableDdl = @"
CREATE TABLE IF NOT EXISTS `gossip_poi` (
  `poi_id`         INT          NOT NULL COMMENT '0 on builds that send no id',
  `branch`         VARCHAR(16)  NOT NULL,
  `variant`        CHAR(32)     NOT NULL COMMENT 'MD5 of every column after it',
  `menu_id`        INT UNSIGNED NULL COMMENT 'the gossip option that sent it, where the parser tied the two together',
  `option_index`   INT UNSIGNED NULL,
  `position_x`     FLOAT        NULL,
  `position_y`     FLOAT        NULL,
  `position_z`     FLOAT        NULL,
  `icon`           INT UNSIGNED NULL,
  `flags`          INT UNSIGNED NULL,
  `importance`     INT UNSIGNED NULL,
  `name`           VARCHAR(255) NULL,
  `wmo_group_id`   INT          NULL,
  `sniffs`         INT UNSIGNED NOT NULL,
  `first_build`    INT          NOT NULL,
  `last_build`     INT          NOT NULL,
  PRIMARY KEY (`poi_id`, `branch`, `variant`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='SMSG_GOSSIP_POI - the map pin a guard or innkeeper option drops.';";

        private const string QuestPoiTableDdl = @"
CREATE TABLE IF NOT EXISTS `quest_poi` (
  `quest_id`                        INT          NOT NULL,
  `branch`                          VARCHAR(16)  NOT NULL,
  `blob_index`                      INT          NOT NULL,
  `idx1`                            INT          NOT NULL,
  `variant`                         CHAR(32)     NOT NULL COMMENT 'MD5 of every column after it',
  `objective_index`                 INT          NULL,
  `quest_objective_id`              INT          NULL,
  `quest_object_id`                 INT          NULL,
  `map_id`                          INT          NULL,
  `ui_map_id`                       INT          NULL,
  `world_map_area_id`               INT          NULL,
  `floor`                           INT          NULL,
  `priority`                        INT          NULL,
  `flags`                           INT          NULL,
  `world_effect_id`                 INT          NULL,
  `player_condition_id`             INT          NULL,
  `navigation_player_condition_id`  INT          NULL,
  `spawn_tracking_id`               INT          NULL,
  `always_allow_merging_blobs`      TINYINT(1)   NULL,
  `sniffs`                          INT UNSIGNED NOT NULL,
  `first_build`                     INT          NOT NULL,
  `last_build`                      INT          NOT NULL,
  PRIMARY KEY (`quest_id`, `branch`, `blob_index`, `idx1`, `variant`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='SMSG_QUEST_POI_QUERY_RESPONSE blobs. The outline is quest_poi_point.';";

        private const string QuestPoiPointTableDdl = @"
CREATE TABLE IF NOT EXISTS `quest_poi_point` (
  `quest_id`    INT          NOT NULL,
  `branch`      VARCHAR(16)  NOT NULL,
  `idx1`        INT          NOT NULL,
  `idx2`        INT          NOT NULL,
  `x`           INT          NOT NULL,
  `y`           INT          NOT NULL,
  `z`           INT          NOT NULL,
  `sniffs`      INT UNSIGNED NOT NULL,
  `first_build` INT          NOT NULL,
  `last_build`  INT          NOT NULL,
  PRIMARY KEY (`quest_id`, `branch`, `idx1`, `idx2`, `x`, `y`, `z`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='The polygon points of each quest_poi blob.';";

        /// <summary>
        /// What a merged table keys an unknown difficulty as. Per-sniff tables say NULL; a primary
        /// key cannot, and a NULL there would stop the rows it should merge from ever meeting.
        /// </summary>
        public const ushort UnknownDifficulty = 65535;

        /// <summary>
        /// MD5 of the values, for tables whose rows are too wide to key on the values themselves.
        /// </summary>
        public static string Variant(params object[] values)
        {
            var text = new StringBuilder();
            foreach (var v in values)
                text.Append(v switch
                {
                    null => "\u0001",
                    float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                    _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)
                }).Append('\u0002');

            return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
        }

        /// <summary>What this sniff wrote into a merged table on an earlier run, if it did.</summary>
        private static int? MergedBefore(ulong sniffId, string capability)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT rows_written FROM `sniff_coverage` WHERE sniff_id = @s AND capability = @c;";
            cmd.Parameters.AddWithValue("@s", sniffId);
            cmd.Parameters.AddWithValue("@c", capability);
            var result = cmd.ExecuteScalar();
            return result == null || result is DBNull ? null : Convert.ToInt32(result);
        }

        /// <summary>
        /// Adds this sniff's rows to a table shared by the corpus. Every row gets the sniff's
        /// branch and one more sniff; columns from <paramref name="sumFrom"/> on are added to
        /// what is there, the rest are the key or are fixed by it. Returns the rows this sniff
        /// contributed, or what it contributed last time if it already has.
        /// </summary>
        [SuppressMessage("Microsoft.Security", "CA2100", Justification = "Table and column names are compile time constants; all values are parameters.")]
        private static int SaveMergedRows(string table, ulong sniffId, string capability, string[] columns,
                                          IReadOnlyList<object[]> rows, int sumFrom = -1, int batchSize = 500)
        {
            if (sniffId == 0 || !Connect())
                return 0;

            try
            {
                var before = MergedBefore(sniffId, capability);
                if (before != null)
                {
                    Trace.WriteLine($"{table}: already holds this sniff, left as it is");
                    return before.Value;
                }

                // Two rows of one sniff with the same key would count the sniff twice.
                rows = Collapse(rows, sumFrom < 0 ? columns.Length : sumFrom);
                if (rows.Count == 0)
                    return 0;

                var branch = ClientVersion.Branch.ToString();
                var build = ClientVersion.BuildInt;

                var update = new StringBuilder(" ON DUPLICATE KEY UPDATE sniffs = sniffs + 1, " +
                                               "first_build = LEAST(first_build, VALUES(first_build)), " +
                                               "last_build = GREATEST(last_build, VALUES(last_build))");
                for (var c = sumFrom < 0 ? columns.Length : sumFrom; c < columns.Length; c++)
                    update.Append(", `").Append(columns[c]).Append("` = `").Append(columns[c])
                          .Append("` + VALUES(`").Append(columns[c]).Append("`)");

                var head = "INSERT INTO `" + table + "` (branch, sniffs, first_build, last_build, `" +
                           string.Join("`, `", columns) + "`) VALUES ";

                for (var offset = 0; offset < rows.Count; offset += batchSize)
                {
                    var batch = Math.Min(batchSize, rows.Count - offset);
                    var sql = new StringBuilder(head);

                    using var cmd = _conn.CreateCommand();
                    cmd.Parameters.AddWithValue("@branch", branch);
                    cmd.Parameters.AddWithValue("@build", build);

                    for (var i = 0; i < batch; i++)
                    {
                        var row = rows[offset + i];
                        if (i > 0)
                            sql.Append(',');

                        sql.Append("(@branch,1,@build,@build");
                        for (var c = 0; c < row.Length; c++)
                        {
                            sql.Append(",@p").Append(i).Append('_').Append(c);
                            cmd.Parameters.AddWithValue($"@p{i}_{c}", row[c]);
                        }

                        sql.Append(')');
                    }

                    cmd.CommandText = sql.Append(update).Append(';').ToString();
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception e)
            {
                Trace.WriteLine($"Could not write {table}: {e.Message}");
                return 0;
            }

            return rows.Count;
        }

        private static List<object[]> Collapse(IReadOnlyList<object[]> rows, int sumFrom)
        {
            var merged = new Dictionary<string, object[]>();
            foreach (var row in rows)
            {
                var key = Variant(row[..sumFrom]);
                if (!merged.TryGetValue(key, out var held))
                {
                    merged[key] = (object[])row.Clone();
                    continue;
                }

                for (var c = sumFrom; c < row.Length; c++)
                    held[c] = Convert.ToInt64(held[c]) + Convert.ToInt64(row[c]);
            }

            return new List<object[]>(merged.Values);
        }

        public static int SaveCreatureValues(ulong sniffId, IReadOnlyList<CreatureValueRecord> values)
        {
            var rows = new List<object[]>(values.Count);
            foreach (var v in values)
                rows.Add(new object[] { v.Entry, v.Map, v.Difficulty ?? UnknownDifficulty, v.Field, v.Value, v.Guids, v.OnCreateGuids,
                                        v.ChangedGuids, v.Observations });

            return SaveMergedRows("creature_value", sniffId, CollectorVersion.CreatureValue,
                                  new[] { "entry", "map", "difficulty", "field", "value", "guids", "on_create_guids", "changed_guids", "observations" },
                                  rows, sumFrom: 5, batchSize: 1000);
        }

        public static int SaveCreatureEquipment(ulong sniffId, IReadOnlyList<CreatureEquipRecord> equip)
        {
            // One row per guid comes in; one per set goes out, counting the guids that held it.
            var sets = new Dictionary<(uint, uint, ushort, uint, uint, uint), int>();
            foreach (var e in equip)
            {
                var key = (e.Entry, e.Map, (ushort)(e.Difficulty ?? UnknownDifficulty), e.ItemId1, e.ItemId2, e.ItemId3);
                sets[key] = sets.GetValueOrDefault(key) + 1;
            }

            var rows = new List<object[]>(sets.Count);
            foreach (var pair in sets)
                rows.Add(new object[] { pair.Key.Item1, pair.Key.Item2, pair.Key.Item3, pair.Key.Item4, pair.Key.Item5, pair.Key.Item6,
                                        pair.Value });

            return SaveMergedRows("creature_equip", sniffId, CollectorVersion.CreatureEquip,
                                  new[] { "entry", "map", "difficulty", "item_id1", "item_id2", "item_id3", "guids" }, rows, sumFrom: 6);
        }

        public static int SaveCreatureQuestItems(ulong sniffId, IReadOnlyList<CreatureQuestItemRecord> items)
        {
            var rows = new List<object[]>(items.Count);
            foreach (var i in items)
                rows.Add(new object[] { i.Entry, i.Index, i.ItemId });

            return SaveMergedRows("creature_quest_item", sniffId, CollectorVersion.CreatureQuestItem,
                                  new[] { "entry", "idx", "item_id" }, rows);
        }

        public static int SaveGameObjectTemplates(ulong sniffId, IReadOnlyList<GameObjectTemplateRecord> templates)
        {
            var rows = new List<object[]>(templates.Count);
            foreach (var t in templates)
            {
                var values = new object[]
                {
                    t.Type, t.DisplayId, t.Name, t.IconName, t.CastBarCaption, t.Unk1, t.Size, t.Data,
                    t.RequiredLevel, t.ContentTuningId
                };

                var row = new object[values.Length + 2];
                row[0] = t.Entry;
                row[1] = Variant(values);
                values.CopyTo(row, 2);
                rows.Add(row);
            }

            return SaveMergedRows("gameobject_template", sniffId, CollectorVersion.GameObjectTemplate,
                                  new[] { "entry", "variant", "type", "display_id", "name", "icon_name", "cast_bar_caption",
                                          "unk1", "size", "data", "required_level", "content_tuning_id" }, rows);
        }

        public static int SaveGameObjectQuestItems(ulong sniffId, IReadOnlyList<GameObjectQuestItemRecord> items)
        {
            var rows = new List<object[]>(items.Count);
            foreach (var i in items)
                rows.Add(new object[] { i.Entry, i.Index, i.ItemId });

            return SaveMergedRows("gameobject_quest_item", sniffId, CollectorVersion.GameObjectQuestItem,
                                  new[] { "entry", "idx", "item_id" }, rows);
        }

        public static int SaveTrainers(ulong sniffId, IReadOnlyList<TrainerRecord> trainers)
        {
            var rows = new List<object[]>(trainers.Count);
            foreach (var t in trainers)
                rows.Add(new object[] { t.TrainerId, Variant(t.Type, t.Greeting), t.Type, t.Greeting });

            return SaveMergedRows("trainer", sniffId, CollectorVersion.Trainer,
                                  new[] { "trainer_id", "variant", "type", "greeting" }, rows);
        }

        public static int SaveNpcTrainers(ulong sniffId, IReadOnlyList<NpcTrainerRecord> spells)
        {
            var rows = new List<object[]>(spells.Count);
            foreach (var s in spells)
            {
                rows.Add(new object[]
                {
                    s.Entry, s.TrainerId, s.SpellId, s.MoneyCost, s.ReqSkillLine, s.ReqSkillRank,
                    s.ReqAbility1, s.ReqAbility2, s.ReqAbility3, s.ReqLevel
                });
            }

            return SaveMergedRows("npc_trainer", sniffId, CollectorVersion.NpcTrainer,
                                  new[] { "entry", "trainer_id", "spell_id", "money_cost", "req_skill_line", "req_skill_rank",
                                          "req_ability1", "req_ability2", "req_ability3", "req_level" }, rows);
        }

        public static int SaveGossipPois(ulong sniffId, IReadOnlyList<GossipPoiRecord> pois)
        {
            var rows = new List<object[]>(pois.Count);
            foreach (var p in pois)
            {
                var values = new object[]
                {
                    p.MenuId, p.OptionIndex, p.PositionX, p.PositionY, p.PositionZ, p.Icon, p.Flags,
                    p.Importance, p.Name, p.WmoGroupId
                };

                var row = new object[values.Length + 2];
                row[0] = p.PoiId;
                row[1] = Variant(values);
                values.CopyTo(row, 2);
                rows.Add(row);
            }

            return SaveMergedRows("gossip_poi", sniffId, CollectorVersion.GossipPoi,
                                  new[] { "poi_id", "variant", "menu_id", "option_index", "position_x", "position_y", "position_z",
                                          "icon", "flags", "importance", "name", "wmo_group_id" }, rows);
        }

        public static int SaveQuestPois(ulong sniffId, IReadOnlyList<QuestPoiRecord> pois)
        {
            var rows = new List<object[]>(pois.Count);
            foreach (var p in pois)
            {
                var values = new object[]
                {
                    p.ObjectiveIndex, p.QuestObjectiveId, p.QuestObjectId, p.MapId, p.UiMapId,
                    p.WorldMapAreaId, p.Floor, p.Priority, p.Flags, p.WorldEffectId, p.PlayerConditionId,
                    p.NavigationPlayerConditionId, p.SpawnTrackingId, p.AlwaysAllowMergingBlobs
                };

                var row = new object[values.Length + 4];
                row[0] = p.QuestId;
                row[1] = p.BlobIndex;
                row[2] = p.Idx1;
                row[3] = Variant(values);
                values.CopyTo(row, 4);
                rows.Add(row);
            }

            return SaveMergedRows("quest_poi", sniffId, CollectorVersion.QuestPoi,
                                  new[] { "quest_id", "blob_index", "idx1", "variant", "objective_index", "quest_objective_id",
                                          "quest_object_id", "map_id", "ui_map_id", "world_map_area_id", "floor", "priority",
                                          "flags", "world_effect_id", "player_condition_id", "navigation_player_condition_id",
                                          "spawn_tracking_id", "always_allow_merging_blobs" }, rows);
        }

        public static int SaveQuestPoiPoints(ulong sniffId, IReadOnlyList<QuestPoiPointRecord> points)
        {
            var rows = new List<object[]>(points.Count);
            foreach (var p in points)
                rows.Add(new object[] { p.QuestId, p.Idx1, p.Idx2, p.X, p.Y, p.Z });

            return SaveMergedRows("quest_poi_point", sniffId, CollectorVersion.QuestPoiPoint,
                                  new[] { "quest_id", "idx1", "idx2", "x", "y", "z" }, rows, batchSize: 1000);
        }
    }
}
