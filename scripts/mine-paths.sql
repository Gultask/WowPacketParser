SET SESSION tmp_table_size = 2147483648;
SET SESSION max_heap_table_size = 2147483648;
SET SESSION sort_buffer_size = 67108864;

-- Path mining from repeated geometry.
--
-- A repeated *position* proves nothing: 17.75% of random-movement waypoints are already
-- byte-identical across sniffs, because a spline's first point is wherever the creature
-- currently stands and an idle creature stands on its spawn point. A repeated ordered *pair*
-- of positions - an edge - is the signal. Random movement picks a fresh destination every
-- time, so two sniffs agreeing on "from here, next go exactly there" is authored data.
--
-- A node is an X,Y at centimetre resolution. Z is NOT part of its identity, because Z is not
-- authored the way X and Y are: the server snaps a ground creature to the terrain height, and
-- the same waypoint comes back a fifth of a yard lower on a different capture. Measured on the
-- 2026-08-23 corpus, 50,148 X,Y positions carried more than one node purely through that snap,
-- and 93% of those splits were under 0.25 yd of Z. The damage was not cosmetic:
--
--   * 26,402 of 84,173 mined paths - 31% - overlapped another path OF THE SAME ENTRY by three
--     or more identical X,Y points. One patrol loop, cut into arcs, published as separate
--     routes. Bael'Modan's guard loop came out as paths 11115 and 11122, same seventeen X,Y
--     in the same order, Z differing by 0.125.
--   * evidence was destroyed outright. The 2-sniff test ran per Z variant, so a waypoint seen
--     once at z=50.18 and once at z=50.31 failed it twice instead of passing it once.
--
-- Positions are packed into one BIGINT at centimetre resolution so grouping never touches
-- float equality:  xy_key = (x' << 22) | y'  with x' = round((x+17100)*100), y' likewise, each
-- inside 22 bits. node_key is (xy_key << 12) | level.
--
-- `level` is almost always 0. Some X,Y really is stacked - a ramp, a tower stair, a bridge over
-- the road below - and merging those would invent an edge between two floors. Phase 1b finds
-- them by spread and separates them by clustering the observed Z values on gaps, not by
-- rounding: a rounding boundary splits 50.04 from 50.06 exactly as readily as the old key did.

SELECT NOW() AS t, 'phase 0: usable points' AS step;

-- Most positions in the capture are not authored data and must not be mined.
--
-- A multi-point SMSG_MONSTER_MOVE carries the server's own computed path: the navmesh corridor
-- it solved, smoothed and snapped to the terrain under it. That is the server's CODE talking,
-- not its database, and it is not reproducible - a different mmap build, a different terrain
-- height, a different smoothing pass and the same waypoint pair comes back as a different
-- string of points. Mining it publishes a straight line of invented waypoints a yard apart
-- and buries the two real ones at its ends.
--
-- Dread Tactician (16959) is the specimen. Segment 1041 arrived as ONE packet: 24 points, one
-- timestamp, one move_time_ms of 14158, running 32.6 yd dead straight - walked length over
-- chord 1.021, every edge 1.00x or 2.02x a 1.023 yd quantum. It published as 23 of path
-- 25266's 24 edges. The one real edge is the 27.51 yd hop off its end - and the proof is that
-- another capture entirely (sniff 724, segment 9215) reached that same point as a
-- single-point move order.
--
-- So: keep the DESTINATION of every move order and throw the interior away. `point_index =
-- segment_points - 1` is that destination, and it collapses to the whole row for the ordinary
-- single-point case. A catmullrom's first point is a control point BEHIND the start, which is
-- another reason the interior cannot be read as waypoints.
--
-- The exception is flying. A creature in the air is not pathfound and not snapped to anything,
-- so a CreateObject spline on one IS the authored route - taxi and patrol paths that exist
-- nowhere else. Those keep every point.
--
-- 0x400 is that bit, confirmed against the corpus rather than read off an enum, because the
-- enum for these builds does not line up: flying creature creation splines carry 0xC80C20
-- while ground splines of the same shape carry 0xC80000. Read it as "not snapped to the
-- ground" rather than strictly flying - it is set on Orca as well as on Frostwyrm, and both
-- are right for this purpose. Grouping every multi-point creation spline in the corpus by the
-- bit returns NO rows without it: Spire Frostwyrm, Monstrous Kaliri, Spotted Hippogryph,
-- Dragonbone Condor, Bat Rider Guard, Wildhammer Gryphon Rider, Cosmetic Toy Plane, all of
-- them airborne. If a ground creature ever lands in this branch the query above will show it.
DROP TABLE IF EXISTS wp_point;
CREATE TABLE wp_point (
  sniff_id    BIGINT UNSIGNED NOT NULL,
  guid        VARCHAR(40) NOT NULL,
  entry       INT UNSIGNED NOT NULL,
  map         INT UNSIGNED NOT NULL,
  segment_id  INT NOT NULL,
  point_index INT NOT NULL,
  position_x FLOAT NOT NULL, position_y FLOAT NOT NULL, position_z FLOAT NOT NULL,
  creation_spline TINYINT(1) NOT NULL,
  air_path    TINYINT(1) NOT NULL COMMENT 'kept as a whole flying route rather than as a destination',
  seen_utc    DATETIME(3) NULL,
  KEY ix_walk (sniff_id, guid, segment_id, point_index),
  KEY ix_entry (entry, map)
) ENGINE=InnoDB
AS
SELECT sniff_id, guid, entry, map, segment_id, point_index,
       position_x, position_y, position_z, creation_spline,
       (creation_spline = 1 AND (spline_flags & 0x400) > 0 AND segment_points > 1) AS air_path,
       seen_utc
FROM creature_waypoint
WHERE position_x BETWEEN -17000 AND 17000
  AND position_y BETWEEN -17000 AND 17000
  AND position_z BETWEEN -2000 AND 4400
  AND (point_index = segment_points - 1
       OR (creation_spline = 1 AND (spline_flags & 0x400) > 0));

SELECT NOW() AS t, COUNT(*) AS usable_points, SUM(air_path) AS kept_as_air_path FROM wp_point;
SELECT NOW() AS t,
       (SELECT COUNT(*) FROM creature_waypoint) AS raw_points,
       (SELECT COUNT(*) FROM wp_point) AS after_spline_drop;

SELECT NOW() AS t, 'phase 1: candidate nodes' AS step;

DROP TABLE IF EXISTS wp_node;
CREATE TABLE wp_node (
  entry     INT UNSIGNED      NOT NULL,
  map       INT UNSIGNED      NOT NULL,
  xy_key    BIGINT            NOT NULL,
  level     SMALLINT UNSIGNED NOT NULL DEFAULT 0 COMMENT '0 unless the X,Y is genuinely stacked',
  node_key  BIGINT            NOT NULL,
  x FLOAT NOT NULL, y FLOAT NOT NULL, z FLOAT NOT NULL,
  z_lo FLOAT NOT NULL, z_hi FLOAT NOT NULL COMMENT 'the Z band this node answers for',
  n_points  INT NOT NULL,
  n_sniffs  INT NOT NULL,
  n_guids   INT NOT NULL,
  PRIMARY KEY (entry, map, xy_key, level),
  KEY ix_node (entry, map, node_key)
) ENGINE=InnoDB;

INSERT INTO wp_node (entry, map, xy_key, level, node_key,
                     x, y, z, z_lo, z_hi, n_points, n_sniffs, n_guids)
SELECT entry, map,
       (CAST(ROUND((position_x+17100)*100) AS SIGNED) << 22)
     +  CAST(ROUND((position_y+17100)*100) AS SIGNED)          AS xy_key,
       0, 0,
       MIN(position_x), MIN(position_y), AVG(position_z),
       MIN(position_z), MAX(position_z),
       COUNT(*), COUNT(DISTINCT sniff_id), COUNT(DISTINCT guid)
FROM wp_point
GROUP BY entry, map, xy_key
-- WHAT CONFIRMS A NODE IS RECURRENCE, NOT INDEPENDENT CAPTURES.
--
-- Random movement never picks the same destination twice - the server rolls a float, and the
-- same centimetre does not come up again. So the moment a point is the target of TWO move
-- orders it is authored, and it makes no difference whether the second one came from a second
-- capture or from the same player watching a second lap.
--
-- This used to read COUNT(DISTINCT sniff_id) >= 2, which threw away every route only one person
-- ever recorded. Deserter Agitator (23602) is the specimen: 6 captures, 55 guids, 260 points,
-- 15 nodes under the old rule and no published route at all, against 36 nodes under this one.
-- Corpus-wide the old rule dropped 929,752 nodes that had already proved themselves by
-- recurring - more than the 820,508 it kept.
--
-- A move order is (sniff_id, guid, segment_id), so two points of ONE creation spline that
-- happen to land on the same centimetre cannot confirm each other.
--
-- What this does NOT relax: 19,014,299 candidate nodes were seen exactly once, ever. That is
-- the wander noise this test exists to remove and it still goes.
HAVING COUNT(DISTINCT sniff_id, guid, segment_id) >= 2;

UPDATE wp_node SET node_key = xy_key << 12;

SELECT NOW() AS t, COUNT(*) AS candidate_nodes FROM wp_node;

SELECT NOW() AS t, 'phase 1b: separate genuinely stacked X,Y' AS step;

-- Two yards. A ground snap is a tenth of that; a floor is several times it.
DROP TABLE IF EXISTS wp_stack;
CREATE TABLE wp_stack (
  entry INT UNSIGNED NOT NULL, map INT UNSIGNED NOT NULL, xy_key BIGINT NOT NULL,
  x FLOAT NOT NULL, y FLOAT NOT NULL,
  PRIMARY KEY (entry, map, xy_key), KEY ix_xy (map, x, y)
) ENGINE=InnoDB
SELECT entry, map, xy_key, x, y FROM wp_node WHERE z_hi - z_lo > 2.0;

SELECT NOW() AS t, COUNT(*) AS stacked_xy FROM wp_stack;

-- Every Z ever seen at those few X,Y. Range-scanned through ix_wp_point rather than matched on
-- float equality, then filtered by recomputing the key - the stored MIN(x) can sit up to a
-- centimetre away from another member of its own group.
DROP TABLE IF EXISTS wp_zobs;
CREATE TABLE wp_zobs (
  entry INT UNSIGNED NOT NULL, map INT UNSIGNED NOT NULL, xy_key BIGINT NOT NULL,
  z FLOAT NOT NULL, sniff_id BIGINT UNSIGNED NOT NULL, guid VARCHAR(40) NOT NULL,
  segment_id INT NOT NULL,
  KEY ix (entry, map, xy_key, z)
) ENGINE=InnoDB
SELECT s.entry, s.map, s.xy_key, w.position_z AS z, w.sniff_id, w.guid, w.segment_id
FROM wp_stack s
JOIN wp_point w
  ON w.map = s.map
 AND w.position_x BETWEEN s.x - 0.02 AND s.x + 0.02
 AND w.position_y BETWEEN s.y - 0.02 AND s.y + 0.02
WHERE w.entry = s.entry
  AND (CAST(ROUND((w.position_x+17100)*100) AS SIGNED) << 22)
    +  CAST(ROUND((w.position_y+17100)*100) AS SIGNED) = s.xy_key;

-- Gap clustering, not bucketing. Sort the Z values and start a new level wherever the step up
-- exceeds the threshold, so two floors separate and a snapped ground never does.
DROP TABLE IF EXISTS wp_level;
CREATE TABLE wp_level (
  entry INT UNSIGNED NOT NULL, map INT UNSIGNED NOT NULL, xy_key BIGINT NOT NULL,
  level SMALLINT UNSIGNED NOT NULL,
  z FLOAT NOT NULL, z_lo FLOAT NOT NULL, z_hi FLOAT NOT NULL,
  n_points INT NOT NULL, n_sniffs INT NOT NULL, n_guids INT NOT NULL,
  PRIMARY KEY (entry, map, xy_key, level)
) ENGINE=InnoDB
SELECT entry, map, xy_key, lvl AS level,
       AVG(z) z, MIN(z) z_lo, MAX(z) z_hi,
       COUNT(*) n_points, COUNT(DISTINCT sniff_id) n_sniffs, COUNT(DISTINCT guid) n_guids
FROM (
  SELECT entry, map, xy_key, z, sniff_id, guid, segment_id,
         SUM(brk) OVER (PARTITION BY entry, map, xy_key ORDER BY z) AS lvl
  FROM (
    SELECT entry, map, xy_key, z, sniff_id, guid, segment_id,
           COALESCE(z - LAG(z) OVER (PARTITION BY entry, map, xy_key ORDER BY z) > 2.0, 0) AS brk
    FROM wp_zobs
  ) a
) b
GROUP BY entry, map, xy_key, lvl
-- Same recurrence test as phase 1, so a level is confirmed on the same terms as a flat node.
HAVING COUNT(DISTINCT sniff_id, guid, segment_id) >= 2 AND lvl < 4096;

DELETE n FROM wp_node n JOIN wp_stack s
  ON s.entry = n.entry AND s.map = n.map AND s.xy_key = n.xy_key;

INSERT INTO wp_node (entry, map, xy_key, level, node_key,
                     x, y, z, z_lo, z_hi, n_points, n_sniffs, n_guids)
SELECT l.entry, l.map, l.xy_key, l.level, (l.xy_key << 12) + l.level,
       s.x, s.y, l.z, l.z_lo, l.z_hi, l.n_points, l.n_sniffs, l.n_guids
FROM wp_level l JOIN wp_stack s
  ON s.entry = l.entry AND s.map = l.map AND s.xy_key = l.xy_key;

SELECT NOW() AS t, COUNT(*) AS nodes_after_levelling FROM wp_node;

SELECT NOW() AS t, 'phase 2: edges between candidate nodes' AS step;

DROP TABLE IF EXISTS wp_edge_raw;
CREATE TABLE wp_edge_raw (
  entry    INT UNSIGNED NOT NULL,
  map      INT UNSIGNED NOT NULL,
  from_key BIGINT NOT NULL,
  to_key   BIGINT NOT NULL,
  sniff_id BIGINT UNSIGNED NOT NULL,
  guid     VARCHAR(40) NOT NULL,
  same_segment TINYINT NOT NULL,
  is_creation  TINYINT NOT NULL,
  dt_ms    INT NULL,
  KEY ix_edge (entry, map, from_key, to_key)
) ENGINE=InnoDB;

INSERT INTO wp_edge_raw (entry, map, from_key, to_key, sniff_id, guid, same_segment, is_creation, dt_ms)
SELECT x.entry, x.map, a.node_key, b.node_key, x.sniff_id, x.guid, x.same_segment, x.is_creation, x.dt_ms
FROM (
  SELECT w.entry, w.map, w.sniff_id, w.guid, w.creation_spline AS is_creation,
         (CAST(ROUND((w.position_x+17100)*100) AS SIGNED) << 22)
       +  CAST(ROUND((w.position_y+17100)*100) AS SIGNED)      AS k,
         w.position_z AS z,
         LEAD((CAST(ROUND((w.position_x+17100)*100) AS SIGNED) << 22)
            +  CAST(ROUND((w.position_y+17100)*100) AS SIGNED))
           OVER (PARTITION BY w.sniff_id, w.guid ORDER BY w.segment_id, w.point_index) AS nk,
         LEAD(w.position_z)
           OVER (PARTITION BY w.sniff_id, w.guid ORDER BY w.segment_id, w.point_index) AS nz,
         (LEAD(w.segment_id) OVER (PARTITION BY w.sniff_id, w.guid ORDER BY w.segment_id, w.point_index) = w.segment_id) AS same_segment,
         TIMESTAMPDIFF(MICROSECOND, w.seen_utc,
            LEAD(w.seen_utc) OVER (PARTITION BY w.sniff_id, w.guid ORDER BY w.segment_id, w.point_index)) DIV 1000 AS dt_ms
  FROM wp_point w
) x
JOIN wp_node a ON a.entry = x.entry AND a.map = x.map AND a.xy_key = x.k
              AND x.z  BETWEEN a.z_lo AND a.z_hi
JOIN wp_node b ON b.entry = x.entry AND b.map = x.map AND b.xy_key = x.nk
              AND x.nz BETWEEN b.z_lo AND b.z_hi
-- Not `x.nk <> x.k`: a step that only changed Z never left the node, and drawing it would put
-- a zero length beam on the map.
WHERE x.nk IS NOT NULL AND a.node_key <> b.node_key;

SELECT NOW() AS t, COUNT(*) AS raw_edges FROM wp_edge_raw;

SELECT NOW() AS t, 'phase 3: confirmed edges' AS step;

DROP TABLE IF EXISTS wp_edge;
CREATE TABLE wp_edge (
  entry    INT UNSIGNED NOT NULL,
  map      INT UNSIGNED NOT NULL,
  from_key BIGINT NOT NULL,
  to_key   BIGINT NOT NULL,
  n_obs    INT NOT NULL,
  n_sniffs INT NOT NULL,
  n_guids  INT NOT NULL,
  spline_obs   INT NOT NULL COMMENT 'times both points arrived in one packet; after phase 0 this can only be a flying CreateObject route, since every other interior point is gone',
  creation_obs INT NOT NULL COMMENT 'times it came from a CreateObject block, e.g. a taxi path',
  median_dt_ms INT NULL,
  PRIMARY KEY (entry, map, from_key, to_key),
  KEY ix_from (entry, map, from_key),
  KEY ix_to   (entry, map, to_key)
) ENGINE=InnoDB
AS
SELECT entry, map, from_key, to_key,
       COUNT(*) n_obs,
       COUNT(DISTINCT sniff_id) n_sniffs,
       COUNT(DISTINCT guid) n_guids,
       SUM(same_segment) spline_obs,
       SUM(is_creation) creation_obs,
       CAST(AVG(dt_ms) AS SIGNED) median_dt_ms
FROM wp_edge_raw
GROUP BY entry, map, from_key, to_key
-- Recurrence again, for the same reason as phase 1: one row here is one observed traversal, and
-- an ordered pair of authored waypoints walked twice is a route whoever watched it. n_sniffs is
-- kept as a column and still travels all the way to the published `edge_sniffs`, because a
-- second INDEPENDENT capture is better evidence than a second lap and the chainer prefers it
-- when both are available - it just no longer decides what exists.
HAVING COUNT(*) >= 2;

SELECT NOW() AS t, COUNT(*) AS confirmed_edges FROM wp_edge;
SELECT NOW() AS t, 'done' AS step;
