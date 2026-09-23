-- =============================================================================
-- Curate creature_spell_cast and publish it.
--
-- WHAT IS WRONG WITH THE RAW TABLE
--
-- creature_spell_cast is every SMSG_SPELL_START whose caster GUID was a Creature
-- or Vehicle. That gate is correct at the packet level and the table is still not
-- publishable, for three separate reasons:
--
--   1. One cast can emit two START rows. The first carries no matching SPELL_GO
--      (completed = 0) and a second, within about a quarter second, completes.
--      Measured on the Icecrown group, 12 of the 13 sub-1.5s consecutive pairs
--      carry exactly that 0-then-1 signature. Left in, they put a 247 ms "gap"
--      into st_gap, and min_gap_ms is the number an AC cooldown is read from.
--      Corpus-wide 6.0% of all gaps are under half a second.
--
--   2. Some spells are not the creature's. 48210 Haunt sits on 1,285 entries
--      including every raid boss in the corpus, and 11,081 of its 11,770 casts
--      are in sniffs named for a warlock. It is the sniffer's own spell. Others
--      are real casts that are not abilities: 1604 Dazed is the melee proc,
--      29266 Permanent Feign Death is a corpse prop, 18950 is a passive.
--
--   3. 670 spell ids are absent from the 3.3.5a DBC - modern internal ids the
--      Classic client emits. 101,511 casts, 1.7% of the table. Whatever they do,
--      they cannot be published to a 3.3.5a core.
--
-- WHAT THIS SCRIPT DOES NOT DO
--
-- It does not try to detect player spells automatically. There is no clean
-- signal for it in what we have: entry breadth does not work (Enrage is on 163
-- entries and is real, Thrash on 104), SpellFamilyName is 0 for Haunt as well as
-- for creature spells, sniff.sniffer is empty for every row, and wotlkmangos has
-- no skill_line_ability. So sc_exclude is a hand-written list with a reason on
-- every row. Add to it; do not replace it with a threshold.
--
-- The breadth numbers are published as columns instead, so a reviewer can see a
-- suspect without the script having silently dropped it.
--
-- REVERSIBILITY
--
-- Everything here is built from scratch each run. The only objects written
-- outside wpp_ingest are acore_world.sniff_creature_spell and the view
-- acore_world.sniff_creature_smartai, both dropped and recreated.
-- =============================================================================


-- -----------------------------------------------------------------------------
-- 0. The exclusion list. Every row needs a reason a human can check.
-- -----------------------------------------------------------------------------
DROP TABLE IF EXISTS sc_exclude;
CREATE TABLE sc_exclude (
  spell_id INT UNSIGNED NOT NULL PRIMARY KEY,
  reason   VARCHAR(80)  NOT NULL,
  source   VARCHAR(16)  NOT NULL COMMENT 'curated | not_in_dbc'
) ENGINE=InnoDB;

INSERT INTO sc_exclude (spell_id, reason, source) VALUES
  (48210, 'player warlock Haunt - 94 pct of casts are in warlock sniffs', 'curated'),
  ( 1604, 'melee daze proc, not an ability - 1966 entries',               'curated'),
  (29266, 'Permanent Feign Death - cosmetic corpse prop',                 'curated'),
  (18950, 'Invisibility and Stealth Detection - passive',                 'curated'),
  (50398, 'Riding Trainer Advertisement - ambient RP filler',             'curated');

-- Anything the 3.3.5a DBC has never heard of cannot go to a 3.3.5a core.
INSERT IGNORE INTO sc_exclude (spell_id, reason, source)
SELECT DISTINCT c.spell_id, 'not present in the 3.3.5a DBC', 'not_in_dbc'
FROM   creature_spell_cast c
LEFT   JOIN wotlkmangos.spell_template s ON s.Id = c.spell_id
WHERE  s.Id IS NULL;


-- -----------------------------------------------------------------------------
-- 1. Drop the doubled starts. A completed = 0 row followed within 1.5 s by a
--    completed = 1 row for the same creature and spell is the same cast.
-- -----------------------------------------------------------------------------
-- The dedup self-join below is unusable without this, and MySQL has no
-- CREATE INDEX IF NOT EXISTS, so the script has to ask first to stay re-runnable.
SET @has_ix := (SELECT COUNT(*) FROM information_schema.statistics
                WHERE table_schema = DATABASE() AND table_name = 'creature_spell_cast'
                  AND index_name = 'ix_sc_dedup');
SET @ddl := IF(@has_ix > 0, 'DO 0',
   'CREATE INDEX ix_sc_dedup ON creature_spell_cast (sniff_id, guid, spell_id, started_utc)');
PREPARE stmt FROM @ddl; EXECUTE stmt; DEALLOCATE PREPARE stmt;

DROP TABLE IF EXISTS sc_dup;
CREATE TABLE sc_dup (id BIGINT UNSIGNED NOT NULL PRIMARY KEY) ENGINE=InnoDB;
INSERT IGNORE INTO sc_dup (id)
SELECT a.id
FROM   creature_spell_cast a
JOIN   creature_spell_cast b
       ON  b.sniff_id = a.sniff_id AND b.guid = a.guid AND b.spell_id = a.spell_id
       AND b.started_utc >  a.started_utc
       AND b.started_utc <  a.started_utc + INTERVAL 1500000 MICROSECOND
WHERE  a.completed = 0 AND b.completed = 1;

DROP TABLE IF EXISTS sc_cast;
CREATE TABLE sc_cast (
  id          BIGINT UNSIGNED NOT NULL PRIMARY KEY,
  sniff_id    BIGINT UNSIGNED NOT NULL,
  guid        VARCHAR(40)     NOT NULL,
  entry       INT UNSIGNED    NOT NULL,
  spell_id    INT UNSIGNED    NOT NULL,
  started_utc DATETIME(3)     NULL,
  completed   TINYINT(1)      NOT NULL,
  KEY ix_es (entry, spell_id),
  KEY ix_seq (sniff_id, guid, spell_id, started_utc)
) ENGINE=InnoDB;

INSERT INTO sc_cast (id, sniff_id, guid, entry, spell_id, started_utc, completed)
SELECT c.id, c.sniff_id, c.guid, c.entry, c.spell_id, c.started_utc, c.completed
FROM   creature_spell_cast c
LEFT   JOIN sc_dup d      ON d.id       = c.id
LEFT   JOIN sc_exclude x  ON x.spell_id = c.spell_id
WHERE  d.id IS NULL AND x.spell_id IS NULL AND c.started_utc IS NOT NULL;


-- -----------------------------------------------------------------------------
-- 2. Repeat gaps, per guid and never across sniffs (same two rules as
--    spell-timers.sql). Floor 1500 ms: below that is a retransmission, not a
--    cooldown. Ceiling 60 s: above that the creature was idle between fights.
-- -----------------------------------------------------------------------------
DROP TABLE IF EXISTS sc_gap;
CREATE TABLE sc_gap (
  entry    INT UNSIGNED    NOT NULL,
  spell_id INT UNSIGNED    NOT NULL,
  sniff_id BIGINT UNSIGNED NOT NULL,
  guid     VARCHAR(40)     NOT NULL,
  gap_ms   INT             NOT NULL,
  KEY ix_es (entry, spell_id)
) ENGINE=InnoDB;

INSERT INTO sc_gap (entry, spell_id, sniff_id, guid, gap_ms)
SELECT entry, spell_id, sniff_id, guid, gap_ms FROM (
  SELECT entry, spell_id, sniff_id, guid,
         TIMESTAMPDIFF(MICROSECOND, started_utc,
             LEAD(started_utc) OVER (PARTITION BY sniff_id, guid, spell_id
                                     ORDER BY started_utc)) DIV 1000 AS gap_ms
  FROM   sc_cast
) g
WHERE gap_ms BETWEEN 1500 AND 60000;


-- -----------------------------------------------------------------------------
-- 3. One row per entry and spell.
-- -----------------------------------------------------------------------------
DROP TABLE IF EXISTS sc_spell;
CREATE TABLE sc_spell (
  entry             INT UNSIGNED NOT NULL,
  spell_id          INT UNSIGNED NOT NULL,
  casts             INT UNSIGNED NOT NULL,
  completed         INT UNSIGNED NOT NULL,
  creatures         INT UNSIGNED NOT NULL,
  sniffs            INT UNSIGNED NOT NULL,
  initial_pulls     INT UNSIGNED NOT NULL DEFAULT 0,
  initial_min_ms    INT NULL,
  initial_median_ms INT NULL,
  initial_max_ms    INT NULL,
  repeat_gaps       INT UNSIGNED NOT NULL DEFAULT 0,
  repeat_min_ms     INT NULL,
  repeat_median_ms  INT NULL,
  repeat_max_ms     INT NULL,
  PRIMARY KEY (entry, spell_id),
  KEY ix_spell (spell_id)   -- entries_sharing below is a per-spell count; without
                            -- this it degrades into a scan per published row
) ENGINE=InnoDB;

INSERT INTO sc_spell (entry, spell_id, casts, completed, creatures, sniffs)
SELECT entry, spell_id, COUNT(*), SUM(completed),
       COUNT(DISTINCT guid), COUNT(DISTINCT sniff_id)
FROM   sc_cast GROUP BY entry, spell_id;

-- Initial timers. spell_initial_gap already anchors on creature_aggro within a
-- 60 s window and takes the MIN per pull, so a doubled start cannot move it.
UPDATE sc_spell s
JOIN  (SELECT entry, spell_id, COUNT(*) pulls, MIN(gap_ms) lo, MAX(gap_ms) hi
       FROM   spell_initial_gap WHERE gap_ms BETWEEN 0 AND 60000
       GROUP  BY entry, spell_id) i
      ON i.entry = s.entry AND i.spell_id = s.spell_id
SET   s.initial_pulls = i.pulls, s.initial_min_ms = i.lo, s.initial_max_ms = i.hi;

-- The median is taken by rank, not by percentile band. PERCENT_RANK BETWEEN
-- .4 AND .6 looks reasonable and is wrong twice over: with four pulls the ranks
-- are 0, .33, .67, 1 and nothing lands in the band at all, so the median comes
-- back NULL; and on a spell cast at 0 ms on most pulls it drifts upward off the
-- true middle. Avenger's Shield is the check - 41 pulls, AC scripts it as
-- SMART_EVENT_AGGRO, and the band put its median at 524 ms instead of 0.
UPDATE sc_spell s
JOIN  (SELECT entry, spell_id, CAST(AVG(gap_ms) AS SIGNED) AS med FROM (
         SELECT entry, spell_id, gap_ms,
                ROW_NUMBER() OVER (PARTITION BY entry, spell_id ORDER BY gap_ms) rn,
                COUNT(*)     OVER (PARTITION BY entry, spell_id)                 cnt
         FROM   spell_initial_gap WHERE gap_ms BETWEEN 0 AND 60000) r
       WHERE  rn IN (FLOOR((cnt + 1) / 2), FLOOR((cnt + 2) / 2))
       GROUP  BY entry, spell_id) m
      ON m.entry = s.entry AND m.spell_id = s.spell_id
SET   s.initial_median_ms = m.med;

-- Repeat timers.
UPDATE sc_spell s
JOIN  (SELECT entry, spell_id, COUNT(*) n, MIN(gap_ms) lo, MAX(gap_ms) hi
       FROM   sc_gap GROUP BY entry, spell_id) g
      ON g.entry = s.entry AND g.spell_id = s.spell_id
SET   s.repeat_gaps = g.n, s.repeat_min_ms = g.lo, s.repeat_max_ms = g.hi;

UPDATE sc_spell s
JOIN  (SELECT entry, spell_id, CAST(AVG(gap_ms) AS SIGNED) AS med FROM (
         SELECT entry, spell_id, gap_ms,
                ROW_NUMBER() OVER (PARTITION BY entry, spell_id ORDER BY gap_ms) rn,
                COUNT(*)     OVER (PARTITION BY entry, spell_id)                 cnt
         FROM   sc_gap) r
       WHERE  rn IN (FLOOR((cnt + 1) / 2), FLOOR((cnt + 2) / 2))
       GROUP  BY entry, spell_id) m
      ON m.entry = s.entry AND m.spell_id = s.spell_id
SET   s.repeat_median_ms = m.med;


-- -----------------------------------------------------------------------------
-- 4. Publish.
--
-- accuracy follows the convention the rest of the corpus uses - 2 only when the
-- number is provable, never as a shorthand for "most likely":
--
--   2  ten or more pulls AND five or more clean gaps. Both halves measured.
--   1  three or more pulls OR two or more gaps. One half measured.
--   0  anything thinner. Published so the spell is known, not so it is trusted.
--
-- shape says which SmartAI event the numbers fit:
--
--   opener       median initial under 500 ms over 5+ pulls -> SMART_EVENT_AGGRO
--   fixed        repeat max within 3x the min -> a real cooldown
--   conditional  wider than that. The creature waits on something the packets
--                do not carry - health, range, a friendly target. A min/max
--                pair will not describe it; AC needs an event, not a timer.
--   sparse       not enough gaps to say either way
--
-- entries_sharing is how many other creature entries cast the same spell. It is
-- carried, not filtered on: 2-4 is an ability shared by a family of mobs, but
-- Enrage is on 163 entries and is perfectly real. A number in the hundreds is a
-- reason to look, not a reason to drop.
-- -----------------------------------------------------------------------------
DROP TABLE IF EXISTS acore_world.sniff_creature_spell;
CREATE TABLE acore_world.sniff_creature_spell (
  entry             INT UNSIGNED     NOT NULL,
  spell_id          INT UNSIGNED     NOT NULL,
  spell_name        VARCHAR(100)     NOT NULL DEFAULT '',
  casts             INT UNSIGNED     NOT NULL,
  completion_pct    TINYINT UNSIGNED NOT NULL,
  creatures         INT UNSIGNED     NOT NULL,
  sniffs            INT UNSIGNED     NOT NULL,
  entries_sharing   INT UNSIGNED     NOT NULL,
  initial_pulls     INT UNSIGNED     NOT NULL,
  initial_min_ms    INT              NULL,
  initial_median_ms INT              NULL,
  initial_max_ms    INT              NULL,
  repeat_gaps       INT UNSIGNED     NOT NULL,
  repeat_min_ms     INT              NULL,
  repeat_median_ms  INT              NULL,
  repeat_max_ms     INT              NULL,
  shape             VARCHAR(12)      NOT NULL,
  accuracy          TINYINT UNSIGNED NOT NULL,
  PRIMARY KEY (entry, spell_id),
  KEY ix_entry (entry),
  KEY ix_spell (spell_id)
) ENGINE=InnoDB;

INSERT INTO acore_world.sniff_creature_spell
SELECT s.entry, s.spell_id,
       COALESCE(t.SpellName, ''),
       s.casts,
       ROUND(100 * s.completed / s.casts),
       s.creatures, s.sniffs,
       (SELECT COUNT(*) FROM sc_spell o WHERE o.spell_id = s.spell_id),
       s.initial_pulls, s.initial_min_ms, s.initial_median_ms, s.initial_max_ms,
       s.repeat_gaps,   s.repeat_min_ms,  s.repeat_median_ms,  s.repeat_max_ms,
       CASE WHEN s.initial_pulls >= 5 AND s.initial_median_ms <= 500 THEN 'opener'
            WHEN s.repeat_gaps   <  3                               THEN 'sparse'
            WHEN s.repeat_max_ms <= s.repeat_min_ms * 3             THEN 'fixed'
            ELSE 'conditional' END,
       CASE WHEN s.initial_pulls >= 10 AND s.repeat_gaps >= 5 THEN 2
            WHEN s.initial_pulls >=  3 OR  s.repeat_gaps >= 2 THEN 1
            ELSE 0 END
FROM   sc_spell s
LEFT   JOIN wotlkmangos.spell_template t ON t.Id = s.spell_id;


-- -----------------------------------------------------------------------------
-- 5. A SmartAI proposal per creature entry.
--
--   SELECT * FROM acore_world.sniff_creature_smartai WHERE entry = 30989;
--
-- One row per spell, already carrying the smart_scripts column values and a
-- ready-to-paste VALUES tuple. It proposes; it never writes. ac_has_it says
-- whether AzerothCore already scripts that spell on that creature, so the rows
-- worth reading first are the ones where it is 0.
--
-- An opener becomes SMART_EVENT_AGGRO (4) with no timers, which is how AC models
-- Avenger's Shield on 30986 - and the sniff agrees with it at median 0 ms over
-- 41 pulls. Everything else becomes SMART_EVENT_UPDATE_IC (0).
--
-- Only accuracy >= 1 is offered. Accuracy 0 rows stay in sniff_creature_spell
-- to be looked at, but they are not proposals.
-- -----------------------------------------------------------------------------
DROP VIEW IF EXISTS acore_world.sniff_creature_smartai;
CREATE VIEW acore_world.sniff_creature_smartai AS
SELECT
  p.entry,
  p.spell_id,
  p.spell_name,
  p.shape,
  p.accuracy,
  CASE WHEN p.shape = 'opener' THEN 4 ELSE 0 END              AS event_type,
  CASE WHEN p.shape = 'opener' THEN 0 ELSE GREATEST(p.initial_min_ms, 0) END        AS event_param1,
  CASE WHEN p.shape = 'opener' THEN 0 ELSE GREATEST(COALESCE(p.initial_median_ms, p.initial_min_ms), 0) END AS event_param2,
  CASE WHEN p.shape = 'opener' THEN 0 ELSE COALESCE(p.repeat_min_ms, 0) END         AS event_param3,
  CASE WHEN p.shape = 'opener' THEN 0 ELSE COALESCE(p.repeat_max_ms, 0) END         AS event_param4,
  11 AS action_type,
  2  AS target_type,
  p.initial_pulls,
  p.repeat_gaps,
  CASE WHEN a.entryorguid IS NULL THEN 0 ELSE 1 END AS ac_has_it,
  CONCAT('(', p.entry, ',0,',
         ROW_NUMBER() OVER (PARTITION BY p.entry ORDER BY p.accuracy DESC, p.casts DESC) - 1,
         ',0,', CASE WHEN p.shape = 'opener' THEN 4 ELSE 0 END, ',0,100,0,',
         CASE WHEN p.shape = 'opener' THEN '0,0,0,0'
              ELSE CONCAT(GREATEST(p.initial_min_ms, 0), ',',
                          GREATEST(COALESCE(p.initial_median_ms, p.initial_min_ms), 0), ',',
                          COALESCE(p.repeat_min_ms, 0), ',',
                          COALESCE(p.repeat_max_ms, 0)) END,
         ',0,11,', p.spell_id, ',0,0,0,0,0,2,0,0,0,0,0,0,0,',
         '"', REPLACE(COALESCE(c.name, CONCAT('Creature ', p.entry)), '"', ''),
         ' - ', CASE WHEN p.shape = 'opener' THEN 'On Aggro' ELSE 'In Combat' END,
         ' - Cast ', '''', p.spell_name, '''', '")') AS smart_scripts_values
FROM   acore_world.sniff_creature_spell p
LEFT   JOIN acore_world.creature_template c ON c.entry = p.entry
LEFT   JOIN (SELECT DISTINCT entryorguid, action_param1
             FROM   acore_world.smart_scripts
             WHERE  source_type = 0 AND action_type = 11) a
       ON a.entryorguid = p.entry AND a.action_param1 = p.spell_id
WHERE  p.accuracy >= 1;
