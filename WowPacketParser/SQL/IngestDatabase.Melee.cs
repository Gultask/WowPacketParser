using System.Collections.Generic;
using System.Linq;
using WowPacketParser.Store.Objects;

namespace WowPacketParser.SQL
{
    public static partial class IngestDatabase
    {
        // Both tables take a surrogate key. The natural one runs through a 512-character aura list,
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
    }
}
