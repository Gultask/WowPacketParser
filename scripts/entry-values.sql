-- What values does a creature entry accept?
--
-- `creature_value` records one row per distinct value per *guid* per sniff, which is the right
-- shape to store but the wrong shape to answer with. This rolls it up to the entry.
--
-- Guids are the unit of evidence, not rows. One creature standing in view for an hour sends its
-- faction on every update block; another sends it once. Counting rows would let the first drown
-- out the second, so everything below counts DISTINCT guid.
--
-- Three columns carry the whole argument:
--
--   guids          how many distinct creatures of this entry were seen with this value
--   guid_share     that as a fraction of all guids of the entry that reported the field
--   on_create_guids how many had it in the block that created them
--
-- A value with guid_share = 1.0 is what the entry is. Anything less means the field is
-- conditional - a faction that flips on a quest, a display id that changes with a disguise -
-- and the row is evidence about the condition, not about the template.
--
-- on_create is the tiebreaker when a field is genuinely split: what a creature spawned with
-- beats what the world did to it afterwards.
--
-- Run after the ingest, against the ingest database. Depends on nothing but `creature_value`,
-- `creature_aggro`, `creature_spell_cast` and `sniff`.

SET SESSION group_concat_max_len = 8192;

-- ---------------------------------------------------------------- entry_value
DROP TABLE IF EXISTS entry_value;
CREATE TABLE entry_value (
  entry            INT UNSIGNED  NOT NULL,
  field            VARCHAR(24)   NOT NULL,
  value            DECIMAL(20,6) NOT NULL,
  guids            INT UNSIGNED  NOT NULL,
  on_create_guids  INT UNSIGNED  NOT NULL,
  sniffs           INT UNSIGNED  NOT NULL,
  branches         VARCHAR(64)   NOT NULL,
  guid_share       DECIMAL(6,4)  NOT NULL COMMENT '1.0 means every guid of this entry agreed',
  PRIMARY KEY (entry, field, value),
  KEY ix_ev_field (field, value),
  KEY ix_ev_share (entry, field, guid_share)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='What each creature entry was observed accepting, counted by distinct guid.';

INSERT INTO entry_value (entry, field, value, guids, on_create_guids, sniffs, branches, guid_share)
SELECT v.entry,
       v.field,
       v.value,
       COUNT(DISTINCT v.guid)                                            AS guids,
       COUNT(DISTINCT CASE WHEN v.on_create = 1 THEN v.guid END)         AS on_create_guids,
       COUNT(DISTINCT v.sniff_id)                                        AS sniffs,
       GROUP_CONCAT(DISTINCT s.branch ORDER BY s.branch SEPARATOR ',')   AS branches,
       COUNT(DISTINCT v.guid) / t.total_guids                            AS guid_share
FROM   creature_value v
JOIN   sniff s ON s.id = v.sniff_id
JOIN   (SELECT entry, field, COUNT(DISTINCT guid) AS total_guids
        FROM   creature_value
        GROUP  BY entry, field) t
       ON t.entry = v.entry AND t.field = v.field
GROUP  BY v.entry, v.field, v.value, t.total_guids;

-- ------------------------------------------------------------- entry_value_best
-- The single value to use per entry and field, and how much to trust it.
--
-- Ordering: guid_share first, then on_create, then raw guid count. A value every guid agreed on
-- needs no tiebreaker; the rest are ranked by whether creatures spawned with it.
DROP TABLE IF EXISTS entry_value_best;
CREATE TABLE entry_value_best (
  entry       INT UNSIGNED  NOT NULL,
  field       VARCHAR(24)   NOT NULL,
  value       DECIMAL(20,6) NOT NULL,
  guids       INT UNSIGNED  NOT NULL,
  guid_share  DECIMAL(6,4)  NOT NULL,
  alternatives INT UNSIGNED NOT NULL COMMENT 'other values this entry was also seen with',
  verdict     VARCHAR(12)   NOT NULL COMMENT 'settled, dominant or split',
  PRIMARY KEY (entry, field),
  KEY ix_evb_verdict (field, verdict)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='One value per entry and field. verdict says whether the evidence actually agreed.';

INSERT INTO entry_value_best (entry, field, value, guids, guid_share, alternatives, verdict)
SELECT entry, field, value, guids, guid_share, alternatives,
       CASE WHEN guid_share >= 0.999 THEN 'settled'
            WHEN guid_share >= 0.800 THEN 'dominant'
            ELSE 'split' END AS verdict
FROM (
  SELECT ev.*,
         COUNT(*) OVER (PARTITION BY ev.entry, ev.field) - 1 AS alternatives,
         ROW_NUMBER() OVER (PARTITION BY ev.entry, ev.field
                            ORDER BY ev.guid_share DESC,
                                     ev.on_create_guids DESC,
                                     ev.guids DESC,
                                     ev.value ASC) AS rn
  FROM   entry_value ev
) ranked
WHERE rn = 1;

-- --------------------------------------------------------------- initial timers
-- The delay from a pull to the creature's first cast of a spell.
--
-- This is not the same number as the gap between two later casts, which is what
-- `spell-timers.sql` measures. AzerothCore keeps both: an initial delay and a repeat range. A
-- creature that opens with a bolt and then casts it every 8s has an initial timer near zero and
-- a repeat timer near 8000, and reading only the repeat would make it silent on the pull.
--
-- Bounded at 60s: past that the creature was almost certainly reacting to something else, and
-- an unbounded window would quietly absorb a second pull that lost its AI reaction packet.
DROP TABLE IF EXISTS spell_initial_gap;
CREATE TABLE spell_initial_gap (
  sniff_id BIGINT UNSIGNED NOT NULL,
  entry    INT UNSIGNED    NOT NULL,
  guid     VARCHAR(40)     NOT NULL,
  spell_id INT UNSIGNED    NOT NULL,
  aggro_utc DATETIME(3)    NOT NULL,
  gap_ms   INT             NOT NULL,
  PRIMARY KEY (sniff_id, guid, spell_id, aggro_utc),
  KEY ix_sig_entry (entry, spell_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='Pull to first cast of each spell. The initial timer, which the repeat timer cannot supply.';

INSERT INTO spell_initial_gap (sniff_id, entry, guid, spell_id, aggro_utc, gap_ms)
SELECT a.sniff_id, a.entry, a.guid, c.spell_id, a.aggro_utc,
       MIN(TIMESTAMPDIFF(MICROSECOND, a.aggro_utc, c.started_utc)) DIV 1000 AS gap_ms
FROM   creature_aggro a
JOIN   creature_spell_cast c
       ON  c.sniff_id = a.sniff_id
       AND c.guid     = a.guid
       AND c.started_utc >= a.aggro_utc
       AND c.started_utc <  a.aggro_utc + INTERVAL 60 SECOND
GROUP  BY a.sniff_id, a.entry, a.guid, c.spell_id, a.aggro_utc;

-- The shape AzerothCore wants: an initial delay range per entry and spell.
DROP TABLE IF EXISTS spell_initial_timer;
CREATE TABLE spell_initial_timer (
  entry     INT UNSIGNED NOT NULL,
  spell_id  INT UNSIGNED NOT NULL,
  pulls     INT UNSIGNED NOT NULL COMMENT 'how many pulls this spell was seen on',
  min_ms    INT          NOT NULL,
  median_ms INT          NOT NULL,
  max_ms    INT          NOT NULL,
  PRIMARY KEY (entry, spell_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='Initial cast delay per entry and spell. pulls is the sample size - one pull proves nothing.';

INSERT INTO spell_initial_timer (entry, spell_id, pulls, min_ms, median_ms, max_ms)
SELECT entry, spell_id, COUNT(*) AS pulls,
       MIN(gap_ms) AS min_ms,
       CAST(SUBSTRING_INDEX(SUBSTRING_INDEX(GROUP_CONCAT(gap_ms ORDER BY gap_ms), ',',
            CEIL(COUNT(*) / 2)), ',', -1) AS SIGNED) AS median_ms,
       MAX(gap_ms) AS max_ms
FROM   spell_initial_gap
GROUP  BY entry, spell_id;

-- ------------------------------------------------------------------- sanity
SELECT 'entry_value'         AS t, COUNT(*) AS rows_, COUNT(DISTINCT entry) AS entries FROM entry_value
UNION ALL SELECT 'entry_value_best',   COUNT(*), COUNT(DISTINCT entry) FROM entry_value_best
UNION ALL SELECT 'spell_initial_gap',  COUNT(*), COUNT(DISTINCT entry) FROM spell_initial_gap
UNION ALL SELECT 'spell_initial_timer',COUNT(*), COUNT(DISTINCT entry) FROM spell_initial_timer;

-- How much of the corpus actually agrees with itself, per field.
SELECT field, verdict, COUNT(*) AS entries
FROM   entry_value_best
GROUP  BY field, verdict
ORDER  BY field, verdict;
