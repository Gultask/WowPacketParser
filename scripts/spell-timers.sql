-- Creature spell timers, derived from raw SMSG_SPELL_START observations.
--
-- AzerothCore models a creature spell as a min and a max timer. Retail does not work that way -
-- a spell has a fixed cooldown and then a chance to be cast on every AI update, roughly every
-- 1200 ms - so a min/max pair is an approximation, and this script produces the numbers to fill
-- it with. Conditional spells (short range AoE, interrupts, positional attacks) will not fit the
-- model at all; they are visible here as a wide spread between the low and high quantiles.
--
-- Two rules the raw table cannot enforce and every query here obeys:
--
--   1. Gaps are taken per guid, never per entry. Two creatures of the same entry cast
--      independently, so ordering an entry's rows by time and subtracting invents gaps far
--      shorter than any real cooldown.
--   2. Gaps do not cross sniffs. The same guid in two captures is two different creatures, and
--      the gap between them is the interval between the captures.
--
-- The timers have no upper bound: a creature that stands idle between fights produces a gap as
-- long as the idle. That is why the maximum published is a quantile and the raw max is only
-- carried alongside for reference.
--
-- Branch matters. Nothing here filters it, because which branches are usable depends on the map
-- and on what changed - see map_validity and the note in PIPELINE.md. Join sniff and filter
-- before trusting a number for a 3.3.5 server.

SET SESSION group_concat_max_len = 1048576;
SET SESSION tmp_table_size = 2147483648;
SET SESSION max_heap_table_size = 2147483648;

-- ---------------------------------------------------------------------------
-- 1. Every gap between two consecutive starts of one spell by one creature.
-- ---------------------------------------------------------------------------

DROP TABLE IF EXISTS st_gap;
CREATE TABLE st_gap (
  entry     INT UNSIGNED NOT NULL,
  spell_id  INT UNSIGNED NOT NULL,
  sniff_id  BIGINT UNSIGNED NOT NULL,
  guid      VARCHAR(40) NOT NULL,
  gap_ms    INT NOT NULL,
  branch    VARCHAR(16) NULL,
  KEY ix_gap (entry, spell_id, gap_ms)
) ENGINE=InnoDB;

INSERT INTO st_gap (entry, spell_id, sniff_id, guid, gap_ms, branch)
SELECT entry, spell_id, sniff_id, guid, gap_ms, branch
FROM (
    SELECT c.entry, c.spell_id, c.sniff_id, c.guid, s.branch,
           TIMESTAMPDIFF(MICROSECOND,
                         LAG(c.started_utc) OVER (PARTITION BY c.sniff_id, c.guid, c.spell_id
                                                  ORDER BY c.started_utc),
                         c.started_utc) / 1000 AS gap_ms
    FROM creature_spell_cast c
    JOIN sniff s ON s.id = c.sniff_id
    WHERE c.started_utc IS NOT NULL
) g
WHERE gap_ms IS NOT NULL AND gap_ms > 0;

-- ---------------------------------------------------------------------------
-- 2. One row per creature entry and spell, with the pair to publish.
--
-- min_timer is the 10th percentile rather than the outright minimum: a spell cast at several
-- targets at once, or a start retransmitted, puts a near-zero gap in the data that no cooldown
-- could produce. max_timer is the 75th - past that the gaps are the creature standing idle
-- rather than waiting on a cooldown.
-- ---------------------------------------------------------------------------

DROP TABLE IF EXISTS st_timer;
CREATE TABLE st_timer (
  entry       INT UNSIGNED NOT NULL,
  spell_id    INT UNSIGNED NOT NULL,
  observations INT NOT NULL,
  creatures   INT NOT NULL COMMENT 'distinct guids; one creature is one fight and proves little',
  sniffs      INT NOT NULL,
  min_gap_ms  INT NOT NULL COMMENT 'raw floor, kept for reference',
  p10_ms      INT NOT NULL COMMENT 'min_timer',
  p25_ms      INT NOT NULL,
  median_ms   INT NOT NULL,
  p75_ms      INT NOT NULL COMMENT 'max_timer',
  p90_ms      INT NOT NULL,
  max_gap_ms  INT NOT NULL COMMENT 'raw ceiling; contaminated by idle time, has no upper bound',
  branches    VARCHAR(64) NULL,
  PRIMARY KEY (entry, spell_id)
) ENGINE=InnoDB;

INSERT INTO st_timer
SELECT entry, spell_id,
       COUNT(*)                          AS observations,
       COUNT(DISTINCT guid)              AS creatures,
       COUNT(DISTINCT sniff_id)          AS sniffs,
       MIN(gap_ms)                       AS min_gap_ms,
       MAX(CASE WHEN pct <= 0.10 THEN gap_ms END) AS p10_ms,
       MAX(CASE WHEN pct <= 0.25 THEN gap_ms END) AS p25_ms,
       MAX(CASE WHEN pct <= 0.50 THEN gap_ms END) AS median_ms,
       MAX(CASE WHEN pct <= 0.75 THEN gap_ms END) AS p75_ms,
       MAX(CASE WHEN pct <= 0.90 THEN gap_ms END) AS p90_ms,
       MAX(gap_ms)                       AS max_gap_ms,
       GROUP_CONCAT(DISTINCT branch ORDER BY branch) AS branches
FROM (
    SELECT entry, spell_id, sniff_id, guid, gap_ms, branch,
           PERCENT_RANK() OVER (PARTITION BY entry, spell_id ORDER BY gap_ms) AS pct
    FROM st_gap
) r
GROUP BY entry, spell_id;

-- The lowest quantile can come back NULL when a spell has a single gap, because PERCENT_RANK
-- gives that one row 0 and nothing sits below it. Fall back to the floor rather than shipping
-- a null into a core table.
UPDATE st_timer
SET p10_ms    = COALESCE(p10_ms, min_gap_ms),
    p25_ms    = COALESCE(p25_ms, min_gap_ms),
    median_ms = COALESCE(median_ms, min_gap_ms),
    p75_ms    = COALESCE(p75_ms, max_gap_ms),
    p90_ms    = COALESCE(p90_ms, max_gap_ms);

-- ---------------------------------------------------------------------------
-- 3. What came out.
-- ---------------------------------------------------------------------------

SELECT COUNT(*) AS timer_rows,
       COUNT(DISTINCT entry) AS entries,
       SUM(observations) AS gaps,
       SUM(creatures >= 3 AND observations >= 20) AS well_evidenced
FROM st_timer;

-- The rows worth publishing first: several creatures, plenty of gaps, and a spread narrow
-- enough that a min/max pair actually describes the spell.
SELECT t.entry, t.spell_id, t.observations, t.creatures, t.sniffs,
       t.p10_ms AS min_timer, t.p75_ms AS max_timer, t.median_ms, t.max_gap_ms, t.branches
FROM st_timer t
WHERE t.creatures >= 3 AND t.observations >= 20
ORDER BY t.observations DESC
LIMIT 40;
