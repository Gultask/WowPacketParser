using System.Collections.Generic;
using System.Linq;
using WowPacketParser.Store.Objects;

namespace WowPacketParser.SQL
{
    public static partial class IngestDatabase
    {
        // These tables take a surrogate key. The natural one runs through a 512-character aura list,
        // and InnoDB copies the primary key into every secondary index: rows cost 500 to 850 bytes
        // that way against well under a hundred of actual data.

        private const string CreatureMeleeTableDdl = @"
CREATE TABLE IF NOT EXISTS `creature_melee` (
  `id`              BIGINT UNSIGNED   NOT NULL AUTO_INCREMENT,
  `sniff_id`        BIGINT UNSIGNED   NOT NULL,
  `entry`           INT UNSIGNED      NOT NULL,
  `map`             INT UNSIGNED      NOT NULL,
  `owner`           VARCHAR(8)        NOT NULL COMMENT 'player, pet, creature or spell when summoned, charmed or created; empty for a creature of its own',
  `level`           SMALLINT UNSIGNED NOT NULL COMMENT 'at the swing; 0 if never seen',
  `attack_time`     INT UNSIGNED      NOT NULL COMMENT 'this hand''s attack time at the swing, ms, hasted or slowed as sent; 0 if never seen',
  `auras`           VARCHAR(512)      NOT NULL COMMENT 'spell ids on the attacker at the swing, sorted; a trailing + means cut short',
  `victim_type`     VARCHAR(8)        NOT NULL COMMENT 'player, pet, creature',
  `kind`            VARCHAR(8)        NOT NULL COMMENT 'hit, crit, glance, crush, miss, dodge, parry, evade, immune, deflect',
  `school`          INT UNSIGNED      NOT NULL,
  `offhand`         TINYINT(1)        NOT NULL,
  `melee_spell`     INT               NOT NULL COMMENT 'nonzero for on-next-swing abilities',
  `guids`           INT               NOT NULL COMMENT 'distinct creatures behind the row',
  `swings`          INT               NOT NULL,
  `original_min`    INT               NULL COMMENT 'OriginalDamage: before armor, block, absorb and resist; NULL when nothing landed',
  `original_max`    INT               NULL,
  `original_sum`    BIGINT            NOT NULL,
  `originals`       JSON              NULL COMMENT 'every landed OriginalDamage in swing order, for fits min and max cannot do',
  `first_utc`       DATETIME(3)       NULL,
  `last_utc`        DATETIME(3)       NULL,
  PRIMARY KEY (`id`),
  KEY `ix_cmelee_entry` (`entry`, `level`),
  CONSTRAINT `fk_cmelee_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='A creature entry''s swings from SMSG_ATTACKER_STATE_UPDATE, per state it swung in. OriginalDamage precedes the victim''s mitigation, so this is the creature''s own damage range whatever it hit. Auras are in the key because Enrage and friends are on for part of a fight: the state with no transient auras is the baseline.';";

        private const string CreatureArmorTableDdl = @"
CREATE TABLE IF NOT EXISTS `creature_armor` (
  `id`              BIGINT UNSIGNED   NOT NULL AUTO_INCREMENT,
  `sniff_id`        BIGINT UNSIGNED   NOT NULL,
  `entry`           INT UNSIGNED      NOT NULL COMMENT 'the victim; 0 for a player',
  `map`             INT UNSIGNED      NOT NULL,
  `level`           SMALLINT UNSIGNED NOT NULL,
  `armor`           INT               NOT NULL COMMENT 'as sent at the swing; -1 unknown - only sent for the player and its pets',
  `auras`           VARCHAR(512)      NOT NULL COMMENT 'spell ids on the victim at the swing: Sunder, Faerie Fire and the like live here',
  `attacker_type`   VARCHAR(8)        NOT NULL COMMENT 'player, pet, creature',
  `attacker_level`  SMALLINT UNSIGNED NOT NULL COMMENT 'the armor formula''s level',
  `victims`         INT               NOT NULL,
  `attackers`       INT               NOT NULL,
  `swings`          INT               NOT NULL COMMENT 'physical hits and crits with no block, absorb or resist',
  `original_sum`    BIGINT            NOT NULL,
  `damage_sum`      BIGINT            NOT NULL COMMENT '1 - damage_sum / original_sum = armor reduction',
  `debug_swings`    INT               NOT NULL COMMENT 'swings carrying the HITINFO_UNK0 debug block',
  `debug_armor_reduction_sum` BIGINT  NOT NULL,
  PRIMARY KEY (`id`),
  KEY `ix_carmor_entry` (`entry`, `level`),
  CONSTRAINT `fk_carmor_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='Melee swings with only armor between OriginalDamage and Damage, per victim entry. For a creature victim the reduction gives its armor: a creature attacker has no armor penetration, a player''s only lowers the reading. For a player victim armor is known and the formula can be checked.';";

        private const string CreatureMeleeColumns =
            "entry, map, owner, level, attack_time, auras, victim_type, kind, school, offhand, melee_spell, guids, " +
            "swings, original_min, original_max, original_sum, originals, first_utc, last_utc";

        public static int SaveCreatureMelee(ulong sniffId, IReadOnlyList<CreatureMeleeRecord> melee)
        {
            var rows = melee.Select(m =>
            {
                var landed = m.Originals.Count > 0;
                return new object[]
                {
                    m.Entry, m.Map, m.Owner, m.Level, m.AttackTime, m.Auras, m.VictimType, m.Kind, m.School,
                    m.Offhand, m.MeleeSpell, m.Guids.Count, m.Swings,
                    landed ? m.OriginalMin : null, landed ? m.OriginalMax : null, m.OriginalSum,
                    landed ? "[" + string.Join(",", m.Originals) + "]" : null, m.FirstUtc, m.LastUtc
                };
            }).ToList();

            return SaveRows("creature_melee", sniffId, CreatureMeleeColumns, rows);
        }

        private const string CreatureArmorColumns =
            "entry, map, level, armor, auras, attacker_type, attacker_level, victims, attackers, swings, " +
            "original_sum, damage_sum, debug_swings, debug_armor_reduction_sum";

        public static int SaveCreatureArmor(ulong sniffId, IReadOnlyList<CreatureArmorRecord> armor)
        {
            var rows = armor.Select(a => new object[]
            {
                a.Entry, a.Map, a.Level, a.Armor, a.Auras, a.AttackerType, a.AttackerLevel, a.Victims.Count,
                a.Attackers.Count, a.Swings, a.OriginalSum, a.DamageSum, a.DebugSwings, a.DebugArmorReductionSum
            }).ToList();

            return SaveRows("creature_armor", sniffId, CreatureArmorColumns, rows, 1000);
        }

        private const string CreatureXpTableDdl = @"
CREATE TABLE IF NOT EXISTS `creature_xp` (
  `id`              BIGINT UNSIGNED   NOT NULL AUTO_INCREMENT,
  `sniff_id`        BIGINT UNSIGNED   NOT NULL,
  `entry`           INT UNSIGNED      NOT NULL,
  `map`             INT UNSIGNED      NOT NULL,
  `zone`            INT UNSIGNED      NOT NULL COMMENT 'with map, decides the content bracket and so the base XP',
  `owner`           VARCHAR(8)        NOT NULL COMMENT 'as in creature_melee',
  `level`           SMALLINT UNSIGNED NOT NULL COMMENT 'the victim''s, at the kill',
  `player_level`    SMALLINT UNSIGNED NOT NULL COMMENT 'the sniffer''s, at the kill; 0 if never seen',
  `group_bonus`     FLOAT             NOT NULL COMMENT 'the packet''s group rate; 1 alone or in a pair, so it cannot tell those apart',
  `guids`           INT               NOT NULL,
  `kills`           INT               NOT NULL,
  `amount_min`      INT               NOT NULL COMMENT 'XP before the rested bonus',
  `amount_max`      INT               NOT NULL,
  `amount_sum`      BIGINT            NOT NULL,
  `original_sum`    BIGINT            NOT NULL COMMENT 'XP received, rested bonus included',
  PRIMARY KEY (`id`),
  KEY `ix_cxp_entry` (`entry`, `level`),
  CONSTRAINT `fk_cxp_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='Kill XP from SMSG_LOG_XP_GAIN with the levels that set it. Against the level formula the ratio is ExperienceModifier, doubled for elites.';";

        private const string CreatureXpColumns =
            "entry, map, zone, owner, level, player_level, group_bonus, guids, kills, amount_min, amount_max, " +
            "amount_sum, original_sum";

        public static int SaveCreatureXp(ulong sniffId, IReadOnlyList<CreatureXpRecord> xp)
        {
            var rows = xp.Select(x => new object[]
            {
                x.Entry, x.Map, x.Zone, x.Owner, x.Level, x.PlayerLevel, x.GroupBonus, x.Guids.Count, x.Kills,
                x.AmountMin, x.AmountMax, x.AmountSum, x.OriginalSum
            }).ToList();

            return SaveRows("creature_xp", sniffId, CreatureXpColumns, rows, 1000);
        }

        private const string CreatureStatsTableDdl = @"
CREATE TABLE IF NOT EXISTS `creature_stats` (
  `id`                  BIGINT UNSIGNED   NOT NULL AUTO_INCREMENT,
  `sniff_id`            BIGINT UNSIGNED   NOT NULL,
  `entry`               INT UNSIGNED      NOT NULL,
  `unit_type`           VARCHAR(8)        NOT NULL COMMENT 'creature, vehicle or pet, from the guid',
  `relation`            VARCHAR(8)        NOT NULL COMMENT 'charmed, summoned, created or demon, as last sent; empty if none was',
  `map`                 INT UNSIGNED      NOT NULL,
  `level`               SMALLINT UNSIGNED NOT NULL,
  `class`               TINYINT UNSIGNED  NOT NULL COMMENT 'unit class as sent; 0 if never seen',
  `auras`               VARCHAR(512)      NOT NULL COMMENT 'spell ids on the unit when the sheet was sent, as in creature_melee',
  `max_health`          BIGINT            NOT NULL,
  `base_health`         INT               NULL,
  `base_mana`           INT               NULL,
  `min_damage`          FLOAT             NULL COMMENT 'main hand, as the paperdoll shows it: (damage_base + AP/14) * DamageModifier * attack time',
  `max_damage`          FLOAT             NULL,
  `min_offhand_damage`  FLOAT             NULL,
  `max_offhand_damage`  FLOAT             NULL,
  `min_ranged_damage`   FLOAT             NULL,
  `max_ranged_damage`   FLOAT             NULL,
  `attack_power`        INT               NULL,
  `attack_power_pos`    INT               NULL,
  `attack_power_neg`    INT               NULL,
  `attack_power_mult`   FLOAT             NULL,
  `ranged_attack_power` INT               NULL,
  `attack_time`         INT UNSIGNED      NOT NULL COMMENT 'ms; 0 if never seen',
  `offhand_attack_time` INT UNSIGNED      NOT NULL,
  `ranged_attack_time`  INT UNSIGNED      NOT NULL,
  `armor`               INT               NULL,
  `stats`               VARCHAR(64)       NOT NULL COMMENT 'str,agi,sta,int,spi totals; an empty slot was never sent',
  `stat_pos`            VARCHAR(64)       NOT NULL COMMENT 'the buffs inside those totals, so the base is stats - stat_pos - stat_neg',
  `stat_neg`            VARCHAR(64)       NOT NULL,
  `resistances`         VARCHAR(96)       NOT NULL COMMENT 'armor then holy, fire, nature, frost, shadow, arcane',
  `resistance_pos`      VARCHAR(96)       NOT NULL,
  `resistance_neg`      VARCHAR(96)       NOT NULL,
  `guids`               INT               NOT NULL,
  `updates`             INT               NOT NULL COMMENT 'sheet updates that said exactly this',
  PRIMARY KEY (`id`),
  KEY `ix_cstats_entry` (`entry`, `level`),
  CONSTRAINT `fk_cstats_sniff` FOREIGN KEY (`sniff_id`) REFERENCES `sniff` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='The stat sheet the server sends a unit''s owner, charmer or rider: pets, guardians, vehicles and mind-controlled mobs. The damage range is the server''s own arithmetic, so it gives DamageModifier exactly; armor and resistances are sent as they are.';";

        private const string CreatureStatsColumns =
            "entry, unit_type, relation, map, level, class, auras, max_health, base_health, base_mana, min_damage, " +
            "max_damage, min_offhand_damage, max_offhand_damage, min_ranged_damage, max_ranged_damage, attack_power, " +
            "attack_power_pos, attack_power_neg, attack_power_mult, ranged_attack_power, attack_time, " +
            "offhand_attack_time, ranged_attack_time, armor, stats, stat_pos, stat_neg, resistances, resistance_pos, " +
            "resistance_neg, guids, updates";

        public static int SaveCreatureStats(ulong sniffId, IReadOnlyList<CreatureStatsRecord> stats)
        {
            var rows = stats.Select(x => new object[]
            {
                x.Entry, x.UnitType, x.Relation, x.Map, x.Level, x.Class, x.Auras, x.MaxHealth, x.BaseHealth,
                x.BaseMana, x.MinDamage, x.MaxDamage, x.MinOffHandDamage, x.MaxOffHandDamage, x.MinRangedDamage,
                x.MaxRangedDamage, x.AttackPower, x.AttackPowerModPos, x.AttackPowerModNeg, x.AttackPowerMultiplier,
                x.RangedAttackPower, x.AttackTime, x.OffHandAttackTime, x.RangedAttackTime, x.Armor, x.Stats,
                x.StatPosBuff, x.StatNegBuff, x.Resistances, x.ResistancePos, x.ResistanceNeg, x.Guids.Count,
                x.Updates
            }).ToList();

            return SaveRows("creature_stats", sniffId, CreatureStatsColumns, rows, 500);
        }
    }
}
