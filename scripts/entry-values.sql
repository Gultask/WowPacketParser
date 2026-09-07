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

-- ------------------------------------------------------- walk or run per segment
-- A move order carries one duration for the whole spline, not a delay per point: the client
-- interpolates at constant speed between them. So a per-point delay would be a computation
-- rather than an observation, and the honest unit is the segment.
--
-- Segment speed = path length / move_time_ms. Compare that against the entry's own speeds -
-- which entry_value_best already holds as AzerothCore multipliers - to say whether the creature
-- walked or ran. That is the number worth having next to movement_id: if MovementInfoID means
-- anything, entries sharing one should agree on how they travel.
--
-- Only segments with at least two points and a real duration can say anything.
DROP TABLE IF EXISTS waypoint_segment_speed;
CREATE TABLE waypoint_segment_speed (
  sniff_id    BIGINT UNSIGNED NOT NULL,
  guid        VARCHAR(40)     NOT NULL,
  entry       INT UNSIGNED    NOT NULL,
  segment_id  INT UNSIGNED    NOT NULL,
  points      INT UNSIGNED    NOT NULL,
  length_yd   DECIMAL(12,3)   NOT NULL,
  move_time_ms INT UNSIGNED   NOT NULL,
  yards_sec   DECIMAL(10,4)   NOT NULL,
  PRIMARY KEY (sniff_id, guid, segment_id),
  KEY ix_wss_entry (entry)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='Observed travel speed per move order. One duration covers the whole spline, so the segment is the smallest honest unit.';

INSERT INTO waypoint_segment_speed (sniff_id, guid, entry, segment_id, points, length_yd, move_time_ms, yards_sec)
SELECT s.sniff_id, s.guid, s.entry, s.segment_id,
       COUNT(*) + 1                                   AS points,
       SUM(s.step)                                    AS length_yd,
       MAX(s.move_time_ms)                            AS move_time_ms,
       SUM(s.step) / (MAX(s.move_time_ms) / 1000.0)   AS yards_sec
FROM (
  SELECT w.sniff_id, w.guid, w.entry, w.segment_id, w.move_time_ms,
         SQRT(POW(w.position_x - LAG(w.position_x) OVER p, 2)
            + POW(w.position_y - LAG(w.position_y) OVER p, 2)
            + POW(w.position_z - LAG(w.position_z) OVER p, 2)) AS step
  FROM   creature_waypoint w
  WHERE  w.move_time_ms > 0
  WINDOW p AS (PARTITION BY w.sniff_id, w.guid, w.segment_id ORDER BY w.point_index)
) s
WHERE s.step IS NOT NULL
GROUP BY s.sniff_id, s.guid, s.entry, s.segment_id
HAVING SUM(s.step) > 0 AND MAX(s.move_time_ms) > 0;

-- Did the entry walk or run? Judged against its own recorded speeds, with a 15% tolerance for
-- spline overhead and the fact that move_time includes acceleration the points do not show.
DROP TABLE IF EXISTS entry_travel_mode;
CREATE TABLE entry_travel_mode (
  entry        INT UNSIGNED  NOT NULL,
  movement_id  INT UNSIGNED  NULL COMMENT 'CreatureMovementInfoID, straight from the query response',
  segments     INT UNSIGNED  NOT NULL,
  median_yd_s  DECIMAL(10,4) NOT NULL,
  walk_yd_s    DECIMAL(10,4) NULL COMMENT 'entry speed_walk multiplier x 2.5',
  run_yd_s     DECIMAL(10,4) NULL COMMENT 'entry speed_run multiplier x 7.0',
  mode         VARCHAR(10)   NOT NULL COMMENT 'walk, run, other or unknown',
  PRIMARY KEY (entry),
  KEY ix_etm_movement (movement_id, mode)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
  COMMENT='How each entry was observed travelling, next to its movement_id. Entries sharing a movement_id should agree if the id means anything.';

INSERT INTO entry_travel_mode (entry, movement_id, segments, median_yd_s, walk_yd_s, run_yd_s, mode)
SELECT m.entry, ct.movement_id, m.segments, m.median_yd_s,
       w.value * 2.5 AS walk_yd_s,
       r.value * 7.0 AS run_yd_s,
       CASE WHEN w.value IS NULL AND r.value IS NULL              THEN 'unknown'
            WHEN ABS(m.median_yd_s - w.value * 2.5) <= 0.15 * w.value * 2.5 THEN 'walk'
            WHEN ABS(m.median_yd_s - r.value * 7.0) <= 0.15 * r.value * 7.0 THEN 'run'
            ELSE 'other' END AS mode
FROM (
  SELECT entry, COUNT(*) AS segments,
         CAST(SUBSTRING_INDEX(SUBSTRING_INDEX(GROUP_CONCAT(yards_sec ORDER BY yards_sec), ',',
              CEIL(COUNT(*) / 2)), ',', -1) AS DECIMAL(10,4)) AS median_yd_s
  FROM   waypoint_segment_speed
  GROUP  BY entry
) m
LEFT JOIN entry_value_best w  ON w.entry = m.entry AND w.field = 'speed_walk'
LEFT JOIN entry_value_best r  ON r.entry = m.entry AND r.field = 'speed_run'
LEFT JOIN (SELECT entry, MAX(movement_id) AS movement_id FROM creature_template GROUP BY entry) ct
       ON ct.entry = m.entry;

SELECT mode, COUNT(*) AS entries, ROUND(AVG(segments),1) AS avg_segments
FROM   entry_travel_mode GROUP BY mode ORDER BY entries DESC;
