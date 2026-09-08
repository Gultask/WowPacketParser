-- entry-auras.sql - sorting the creature's own auras from the ones players put on it.
--
-- `creature_aura` records every aura seen on a creature, and most of them are a player's. The
-- widest-spread auras in the corpus are Winter's Chill on 2,420 entries, Frost Fever on 2,235,
-- Corruption on 1,989: a sniffer's debuffs, following them from mob to mob. What creature_addon
-- and creature_template_addon want is the other kind, the aura a creature spawns already
-- carrying, and it has to be dug out.
--
-- Four signals, none sufficient alone. The first three judge the spell, the fourth the entry:
--
--   permanence   an aura that never carried a duration. Upstream uses this by itself, and it is
--                the only signal that works on every branch, but it is not enough: Savage Combat
--                is a rogue talent debuff and 39,308 of its 39,308 sightings are permanent.
--
--   caster       who the packet said cast it. Decisive where it exists - across the 58,147
--                sightings of eight known player DoTs on WotLK sniffs, not one is marked
--                self-cast - but eleven version modules read the caster into the protobuf and
--                dropped it on the floor, so collector version 1 wrote "self" for every aura on
--                Cata, MoP, Retail and Classic. Only trusted branches are believed here.
--
--   spell family SpellFamilyName from Spell.dbc. Families 3 to 11 and 15 are the ten player
--                classes, 13 is consumables and 17 is hunter pet talents; all of them arrive on
--                a creature because a player did something. Family 0 is where creature abilities
--                live, and family 1 is generic world auras like Air Bubbles that creatures do
--                legitimately spawn with, so neither is excluded. This is the fallback for
--                entries no trusted branch ever saw.
--
-- Where a spell has trusted evidence anywhere in the corpus, that answers for the spell
-- everywhere, including on branches that cannot be trusted directly. Savage Combat is permanent
-- and family 0 and would survive both other filters; it is caught because 37,228 trusted
-- sightings all say another unit cast it.
--
-- A fourth signal is needed for what survives all three and still is not world content: pet
-- scaling. Hunter Pet Scaling 01 to 04, Pet Health Scaling, the Army of the Dead scalings - all
-- permanent, family 0, genuinely self-cast, and all on summons standing next to the sniffer.
-- UNIT_FLAG_PLAYER_CONTROLLED settles it, and does so at the entry rather than the spell:
-- 63,888 of 63,888 sightings of the Army of the Dead Ghoul carry it, against 0 of 44,430 for a
-- Wild Flower.
--
-- Needs the 3.3.5 Spell.dbc as `wotlkmangos.spell_template`. Without it every family reads 0 and
-- the fallback silently weakens - see the coverage check at the end.
--
-- Run after the ingest, against the ingest database.

SET SESSION group_concat_max_len = 8192;

-- --------------------------------------------------------------- trusted sniffs
-- A sniff whose self_cast column means what it says. Collector version 2 onwards records 2 for
-- "the packet did not say" and never guesses, so any branch is fine. Version 1 guessed self, and
-- only the two branches that happened to read the caster came out right.
DROP TABLE IF EXISTS aura_trusted_sniff;
CREATE TABLE aura_trusted_sniff (
  sniff_id BIGINT UNSIGNED NOT NULL,
  PRIMARY KEY (sniff_id)
) ENGINE=InnoDB
  COMMENT='Sniffs whose creature_aura.self_cast can be believed.';

INSERT INTO aura_trusted_sniff (sniff_id)
SELECT s.id
FROM   sniff s
LEFT   JOIN sniff_coverage c
       ON c.sniff_id = s.id AND c.capability = 'creature_aura'
WHERE  COALESCE(c.collector_version, 1) >= 2
   OR  s.branch IN ('WotLK', 'TBC');

-- ------------------------------------------------------------- controlled entries
-- Which entries are somebody's pet, charm or vehicle rather than world content. Read from the
-- raw unit_flags in `creature_value`, not from `entry_value`, because entry-values.sql strips
-- PlayerControlled as a runtime bit - which it is, and which is exactly what makes it useful.
DROP TABLE IF EXISTS entry_controlled;
CREATE TABLE entry_controlled (
  entry  INT UNSIGNED NOT NULL,
  obs    INT UNSIGNED NOT NULL,
  pc_obs INT UNSIGNED NOT NULL COMMENT 'sightings carrying UNIT_FLAG_PLAYER_CONTROLLED',
  PRIMARY KEY (entry)
) ENGINE=InnoDB
  COMMENT='Entries seen under player control. A majority means the entry is a summon.';

INSERT INTO entry_controlled (entry, obs, pc_obs)
SELECT entry,
       SUM(guids),
       SUM(CASE WHEN CAST(value AS UNSIGNED) & 8 THEN guids ELSE 0 END)
FROM   creature_value
WHERE  field = 'unit_flags'
GROUP  BY entry;

-- ------------------------------------------------------------------- spell_aura
-- What each spell is, judged across the whole corpus rather than one entry at a time.
DROP TABLE IF EXISTS spell_aura;
CREATE TABLE spell_aura (
  spell_id      INT UNSIGNED NOT NULL,
  entries       INT UNSIGNED NOT NULL COMMENT 'distinct creature entries it was seen on',
  obs           INT UNSIGNED NOT NULL,
  perm_obs      INT UNSIGNED NOT NULL,
  trusted_obs   INT UNSIGNED NOT NULL,
  trusted_self  INT UNSIGNED NOT NULL,
  trusted_other INT UNSIGNED NOT NULL,
  family        SMALLINT     NULL COMMENT 'SpellFamilyName, null when the spell is not in 3.3.5 Spell.dbc',
  verdict       VARCHAR(10)  NOT NULL COMMENT 'player, creature or unknown',
  PRIMARY KEY (spell_id),
  KEY ix_sa_verdict (verdict)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='Per spell, whether a creature or a player is the one applying it.';

INSERT INTO spell_aura (spell_id, entries, obs, perm_obs, trusted_obs, trusted_self,
                        trusted_other, family, verdict)
SELECT a.spell_id,
       COUNT(DISTINCT a.entry),
       COUNT(*),
       SUM(a.max_duration_ms IS NULL),
       SUM(t.sniff_id IS NOT NULL),
       SUM(t.sniff_id IS NOT NULL AND a.self_cast = 1),
       SUM(t.sniff_id IS NOT NULL AND a.self_cast = 0),
       st.SpellFamilyName,
       CASE
         -- A class, consumable or pet-talent spell, whoever the packet blamed. An NPC caster
         -- using a player's spell id is real but rare, and losing those costs less than seeding
         -- creature_addon with a warlock's DoTs.
         WHEN st.SpellFamilyName IN (3,4,5,6,7,8,9,10,11,13,15,17) THEN 'player'
         -- Majority of the trusted sightings, not merely one of them. Blood Frenzy has 160
         -- saying self-cast against 3,589 saying otherwise, and any-beats-none made it a
         -- creature aura on 999 entries.
         WHEN SUM(t.sniff_id IS NOT NULL AND a.self_cast = 1)
            > SUM(t.sniff_id IS NOT NULL AND a.self_cast = 0) THEN 'creature'
         WHEN SUM(t.sniff_id IS NOT NULL AND a.self_cast = 0) > 0 THEN 'player'
         ELSE 'unknown'
       END
FROM   creature_aura a
LEFT   JOIN aura_trusted_sniff t ON t.sniff_id = a.sniff_id
LEFT   JOIN wotlkmangos.spell_template st ON st.Id = a.spell_id
GROUP  BY a.spell_id, st.SpellFamilyName;

-- ------------------------------------------------------------------- entry_aura
-- One row per creature entry and spell, with the verdict this pipeline stands behind.
DROP TABLE IF EXISTS entry_aura;
CREATE TABLE entry_aura (
  entry         INT UNSIGNED NOT NULL,
  spell_id      INT UNSIGNED NOT NULL,
  obs           INT UNSIGNED NOT NULL COMMENT 'creature sightings carrying this aura',
  perm_obs      INT UNSIGNED NOT NULL COMMENT 'sightings where it never carried a duration',
  on_create_obs INT UNSIGNED NOT NULL,
  trusted_self  INT UNSIGNED NOT NULL,
  trusted_other INT UNSIGNED NOT NULL,
  sniffs        INT UNSIGNED NOT NULL,
  branches      VARCHAR(64)  NOT NULL,
  spell_verdict VARCHAR(10)  NOT NULL,
  controlled    TINYINT(1)   NOT NULL COMMENT 'the entry is mostly seen under player control, so a pet',
  verdict       VARCHAR(10)  NOT NULL COMMENT 'addon, combat, pet, player or unknown',
  PRIMARY KEY (entry, spell_id),
  KEY ix_ea_verdict (verdict, entry),
  KEY ix_ea_spell (spell_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='Auras per creature entry. verdict addon means permanent and the creature is the caster - creature_template_addon material. combat means its own but timed, so it cast it during a fight. pet means the creature is a summon. player means someone else put it there.';

INSERT INTO entry_aura (entry, spell_id, obs, perm_obs, on_create_obs, trusted_self,
                        trusted_other, sniffs, branches, spell_verdict, controlled, verdict)
SELECT a.entry,
       a.spell_id,
       COUNT(*)                                                        AS obs,
       SUM(a.max_duration_ms IS NULL)                                  AS perm_obs,
       SUM(a.on_create)                                                AS on_create_obs,
       SUM(t.sniff_id IS NOT NULL AND a.self_cast = 1)                 AS trusted_self,
       SUM(t.sniff_id IS NOT NULL AND a.self_cast = 0)                 AS trusted_other,
       COUNT(DISTINCT a.sniff_id)                                      AS sniffs,
       GROUP_CONCAT(DISTINCT s.branch ORDER BY s.branch SEPARATOR ',') AS branches,
       sa.verdict                                                      AS spell_verdict,
       COALESCE(ec.pc_obs > ec.obs / 2, 0)                             AS controlled,
       CASE
         -- Asked first because it is the surest of the four and does not depend on the spell:
         -- whatever a summon is carrying, it is not world content.
         WHEN ec.pc_obs > ec.obs / 2                      THEN 'pet'
         WHEN sa.verdict = 'player'                       THEN 'player'
         -- Seen with a duration even once: something cast it, it did not spawn with it.
         WHEN SUM(a.max_duration_ms IS NULL) < COUNT(*)   THEN 'combat'
         WHEN sa.verdict = 'creature'                     THEN 'addon'
         ELSE 'unknown'
       END                                                             AS verdict
FROM   creature_aura a
JOIN   sniff s      ON s.id = a.sniff_id
JOIN   spell_aura sa ON sa.spell_id = a.spell_id
LEFT   JOIN aura_trusted_sniff t ON t.sniff_id = a.sniff_id
LEFT   JOIN entry_controlled ec ON ec.entry = a.entry
GROUP  BY a.entry, a.spell_id, sa.verdict, ec.pc_obs, ec.obs;

-- ------------------------------------------------------------------------ checks
-- The Spell.dbc join is the one dependency that can vanish without erroring. If almost every
-- spell reads family null, the fallback is not running and the unknown bucket is inflated.
SELECT 'spells matched to Spell.dbc' AS check_,
       CONCAT(SUM(family IS NOT NULL), ' of ', COUNT(*)) AS result
FROM   spell_aura
UNION ALL
SELECT 'entry x spell by verdict', GROUP_CONCAT(line ORDER BY line SEPARATOR '  ')
FROM   (SELECT CONCAT(verdict, '=', COUNT(*)) AS line FROM entry_aura GROUP BY verdict) v
UNION ALL
SELECT 'addon rows per branch coverage',
       CONCAT(SUM(branches LIKE '%WotLK%' OR branches LIKE '%TBC%'), ' of ',
              COUNT(*), ' addon rows have a trusted branch')
FROM   entry_aura WHERE verdict = 'addon';
