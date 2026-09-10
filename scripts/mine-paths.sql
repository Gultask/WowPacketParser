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
--
-- Recurrence is not the only way in. Phase 4 admits the rest of a walk that has already proved
-- itself twice over, because a scripted walk is one object rather than a bag of independent
-- positions: once two of its steps match another capture the creature is demonstrably on an
-- authored path, and the part only one person stayed to watch is the rest of that same path.
-- Everything before phase 4 is per point and unchanged.

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
FROM creature_waypoint w
WHERE position_x BETWEEN -17000 AND 17000
  AND position_y BETWEEN -17000 AND 17000
  AND position_z BETWEEN -2000 AND 4400
  AND (point_index = segment_points - 1
       OR (creation_spline = 1 AND (spline_flags & 0x400) > 0))
  -- OWNED CREATURES ARE NOT WORLD CONTENT, and the collector that wrote this table did not know
  -- it. IsTemporarySpawn - SummonedBy, CreatedBy, CreatedBySpell, DemonCreator, four update
  -- fields the server sent - gates CollectCreatureSpawns and the gameobject collector and was
  -- never called by CollectCreatureWaypoints. That is fixed in the parser, but only for sniffs
  -- ingested after it, so this stands in for it on the corpus that exists.
  --
  -- An entry with no creature_spawn row ANYWHERE was refused by that gate every single time it
  -- was seen, which makes this the same rule read back out of its own output. Entry level and
  -- not guid level on purpose: creature_spawn also drops corpses, so a guid-level test would
  -- take the legitimate movement of any creature the sniffer only ever saw dead.
  --
  -- 4,279,860 rows go, 8.5% of the table, across 1,630 entries and 340,521 guids: Snake Trap
  -- snakes, shaman totems, mage mirror images, Army of the Dead ghouls, warlock pets, Wild
  -- Flower, herbalism spawns and Dark Portal beam stalkers. A pet follows its player, so its
  -- route is the PLAYER route, and it clears every geometric test a real one-way route clears
  -- because the player covers ground.
  --
  -- This loses the summons that genuinely do have an authored path. They are event content and
  -- the sniff can be read directly for those; the corpus is here for the overworld spawn.
  AND EXISTS (SELECT 1 FROM creature_spawn s WHERE s.entry = w.entry);

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

SELECT NOW() AS t, 'phase 2: every consecutive pair of points' AS step;

-- This LEAD pass used to sit inside the derived table of the INSERT below, and a second copy of
-- it inside phase 4. It killed two mines in one night, both at this point, both with
--
--   ERROR 1114 (HY000): The table 'C:\...\NETWOR~1\AppData\Local\Temp\#sql...' is full
--
-- The first death was the derived table: MySQL cannot merge a windowed one into its parent, so it
-- materialised all 43.9M rows into an internal temp table. Writing into a named InnoDB table
-- instead did NOT fix it - the second death was the CREATE TABLE ... AS SELECT itself, because
-- the windowing step materialises its own output no matter where the rows are going afterwards.
--
-- The ceiling is not the disk. 56 GB were free both times, and the temp file was cleaned up on
-- the way out. It is an internal one nobody set for this job: temptable_max_ram is GLOBAL-only,
-- so a script that sets SESSION variables cannot reach it, and the overflow lands in the service
-- account's Temp rather than on the data drive. Rather than hunt for which limit it is, the pass
-- is made small enough that no limit is in reach.
--
-- The window partitions by (sniff_id, guid), so a split on sniff_id can never cut a partition in
-- half: each batch is a whole number of captures and its result is byte-identical to the same
-- rows out of one big pass. ix_walk is (sniff_id, guid, segment_id, point_index), which is the
-- partition and the order, so a batch is an index range scan with no sort at all.
--
-- Batches are built to about a million rows rather than a fixed count of captures, because
-- captures differ by three orders of magnitude in size and a fixed stride would put half the
-- corpus in one batch.
--
-- The pass also runs ONCE now, by name, and phase 4 reads the result instead of computing its own
-- second copy over the same 43.9M rows.
--
-- No secondary index on wp_step, deliberately. Both readers scan it whole and probe the small
-- side by its own primary key - wp_node's is (entry, map, xy_key, level), wp_walk's is
-- (sniff_id, guid) - so an index here would buy nothing and cost 43.9M random insertions.
DROP TABLE IF EXISTS wp_step;
CREATE TABLE wp_step (
  sniff_id     BIGINT UNSIGNED NOT NULL,
  guid         VARCHAR(40) NOT NULL,
  entry        INT UNSIGNED NOT NULL,
  map          INT UNSIGNED NOT NULL,
  is_creation  TINYINT NOT NULL,
  k            BIGINT NOT NULL COMMENT 'this point as an xy_key',
  z            FLOAT NOT NULL,
  nk           BIGINT NULL COMMENT 'the next point the server sent this guid to; NULL ends its walk',
  nz           FLOAT NULL,
  same_segment TINYINT NULL,
  dt_ms        INT NULL
) ENGINE=InnoDB COMMENT='wp_point with the next point alongside; the input to phase 2b and phase 4';

-- Which captures go in which batch. Both derived tables here are one row per capture, a few
-- thousand of them, so the running total is the one window function in this file that cannot
-- grow with the corpus.
DROP TABLE IF EXISTS wp_step_batch;
CREATE TABLE wp_step_batch (
  sniff_id BIGINT UNSIGNED NOT NULL PRIMARY KEY,
  batch    INT NOT NULL,
  KEY ix_batch (batch)
) ENGINE=InnoDB
SELECT d.sniff_id, CAST(FLOOR((d.run - 1) / 1000000) AS SIGNED) AS batch
FROM (
  SELECT c.sniff_id, SUM(c.pts) OVER (ORDER BY c.sniff_id) AS run
  FROM (SELECT sniff_id, COUNT(*) AS pts FROM wp_point GROUP BY sniff_id) c
) d;

SELECT NOW() AS t, COUNT(DISTINCT batch) AS batches, COUNT(*) AS captures FROM wp_step_batch;

-- Batches are contiguous ranges of sniff_id by construction, so the loop hands each one to a
-- BETWEEN and the optimiser gets a plain range scan. Handing it the batch table to join against
-- would work too and would throw the index ordering away.
DROP PROCEDURE IF EXISTS wp_fill_step;
DELIMITER $$
CREATE PROCEDURE wp_fill_step()
BEGIN
  DECLARE done INT DEFAULT 0;
  DECLARE n INT DEFAULT 0;
  DECLARE r INT DEFAULT 0;
  DECLARE lo BIGINT UNSIGNED;
  DECLARE hi BIGINT UNSIGNED;
  DECLARE cur CURSOR FOR
    SELECT MIN(sniff_id), MAX(sniff_id) FROM wp_step_batch GROUP BY batch ORDER BY batch;
  DECLARE CONTINUE HANDLER FOR NOT FOUND SET done = 1;

  OPEN cur;
  fill: LOOP
    FETCH cur INTO lo, hi;
    IF done THEN LEAVE fill; END IF;

    INSERT INTO wp_step (sniff_id, guid, entry, map, is_creation, k, z, nk, nz, same_segment, dt_ms)
    SELECT w.sniff_id, w.guid, w.entry, w.map, w.creation_spline,
           (CAST(ROUND((w.position_x+17100)*100) AS SIGNED) << 22)
         +  CAST(ROUND((w.position_y+17100)*100) AS SIGNED),
           w.position_z,
           LEAD((CAST(ROUND((w.position_x+17100)*100) AS SIGNED) << 22)
              +  CAST(ROUND((w.position_y+17100)*100) AS SIGNED)) OVER wnd,
           LEAD(w.position_z) OVER wnd,
           (LEAD(w.segment_id) OVER wnd = w.segment_id),
           TIMESTAMPDIFF(MICROSECOND, w.seen_utc, LEAD(w.seen_utc) OVER wnd) DIV 1000
    FROM wp_point w
    WHERE w.sniff_id BETWEEN lo AND hi
    WINDOW wnd AS (PARTITION BY w.sniff_id, w.guid ORDER BY w.segment_id, w.point_index);

    -- Immediately after the INSERT: ROW_COUNT() answers for the statement before it, and the
    -- SET below would be that statement.
    SET r = ROW_COUNT();
    SET n = n + 1;
    IF n % 5 = 0 THEN
      SELECT NOW() AS t, n AS batches_done, r AS rows_in_the_last_one;
    END IF;
  END LOOP;
  CLOSE cur;
END $$
DELIMITER ;

CALL wp_fill_step();
DROP PROCEDURE wp_fill_step;

SELECT NOW() AS t, COUNT(*) AS steps, SUM(nk IS NULL) AS ends_of_a_walk FROM wp_step;

SELECT NOW() AS t, 'phase 2b: edges between candidate nodes' AS step;

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
FROM wp_step x
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

SELECT NOW() AS t, 'phase 4: walks that proved themselves' AS step;

-- WHY A WALK CAN VOUCH FOR ITS OWN POINTS.
--
-- Phases 1 and 3 ask the same question of every point and every step in isolation: was this one
-- seen twice? That is the right question to ask of a creature picking destinations at random,
-- and the wrong one to ask of a creature running a script. A scripted walk is a single object.
-- If two of its steps are byte-identical to another capture, the creature is on an authored
-- path, and the rest of THAT walk is the rest of THAT path - not noise standing beside it.
--
-- Malcolm Moore (27891) is the specimen. Seven captures, 40 distinct positions, 33 of them seen
-- exactly once and all 33 exclusive to the one capture that followed him the whole way; the
-- other six turn back between points 2 and 7. Per point, he published 5 points of a 39 point
-- walk. His opening steps are confirmed by four to six independent captures, so the walk is
-- proven, and the remaining 34 steps of that same continuous walk come with it.
--
-- TWO CONDITIONS, AND EACH ONE COVERS THE OTHER BLIND SPOT. Neither is a safety margin; without
-- both, this phase turned 607,873 edges into 8,405,393, of which 7,797,520 - 93% - were admitted
-- on a walk rather than earned.
--
-- 1. THE CONFIRMED STEPS HAVE TO MEET. Two steps anywhere in a walk is not evidence, because
--    coincidences are independent events and a long walk collects them. Two steps that meet
--    end to start is three consecutive positions matching another capture, which squares the
--    improbability and does not care how long anyone watched.
--
-- 2. THE WALK HAS TO HAVE GONE SOMEWHERE, measured as radius_robust >= points - at least a yard
--    of spread per move order issued. Below that the creature is issuing more orders than it has
--    ground to show for them, which is only possible by re-treading.
--
-- Spider (14881) is why condition 2 exists. One capture of one spider holds 4,789 move orders
-- over two hours and forty-one minutes inside a radius of 5.3 yards. It is pacing. Eighteen of
-- its 4,788 steps match another capture, and they are not flukes either - 85% of the confirmed
-- edges on that entry are backed by two or more independent captures - but eighteen coincidences
-- inside a five yard circle say nothing about the other 4,770 steps. Rat, Cockroach, Rabbit and
-- Sewer Frog behave the same way and were the bulk of that 7.8 million.
--
-- Condition 1 exists because condition 2 only bites on a creature somebody watched for a LONG
-- time: radius_robust saturates at the wander radius while points keeps growing, so a wanderer
-- caught in a short window has few orders and a wide radius and sails through. Worse, the
-- confirmations for such a creature come from OTHER GUIDS of the same entry, since the node test
-- counts distinct (sniff_id, guid, segment_id) and two creatures confirm each other - so a
-- densely spawned entry manufactures them. Army of the Dead Ghoul, Bloodworm and Sprite Darter
-- Hatchling clear condition 2 outright and score HIGHER on it than Malcolm does, because a pet
-- follows the player and the player covers ground. There is no authored route anywhere in that.
--
--   Malcolm Moore  191.6 yd over 39 orders    = 4.9   run: yes
--   Locheed         14.4 yd over  3 orders    = 4.8   run: yes
--   Spider           4.4 yd over 4,789 orders = 0.0009
--
-- Measured over the 433,237 walks holding at least one confirmed step, the two conditions split
-- them like this, by the points each group would promote:
--
--   run + went somewhere    193,360 walks    3,033,622 points   avg  16 orders, radius 63.8
--   run + retreads           84,898 walks   13,060,055 points   avg 154 orders, radius 26.9
--   scattered + retreads      5,796 walks    1,189,303 points   avg 205 orders, radius 17.9
--   scattered + went somewhere 4,676 walks      72,963 points   avg  16 orders, radius 60.4
--
-- It also states plainly what this phase is FOR: the one-way route walked once - escorts, flees,
-- quest paths. A patrol loop needs none of it, because every lap re-confirms it per point.
--
-- What this does NOT relax: a walk with no confirmed step at all stays out entirely. The
-- 19,014,299 candidate positions seen exactly once, in walks that never touched a confirmed
-- edge, are the wander noise phase 1 exists to remove and they still go.
-- The confirmed steps of every walk, kept as steps rather than counted, so that whether two of
-- them MEET can be asked. wp_edge_raw rows are already consecutive point pairs, so a.to_key
-- matching b.from_key inside one walk is A -> B -> C walked in that order.
DROP TABLE IF EXISTS wp_step_ok;
CREATE TABLE wp_step_ok (
  sniff_id BIGINT UNSIGNED NOT NULL, guid VARCHAR(40) NOT NULL,
  entry INT UNSIGNED NOT NULL, map INT UNSIGNED NOT NULL,
  from_key BIGINT NOT NULL, to_key BIGINT NOT NULL,
  KEY ix_from (sniff_id, guid, from_key)
) ENGINE=InnoDB
AS
SELECT DISTINCT r.sniff_id, r.guid, r.entry, r.map, r.from_key, r.to_key
FROM wp_edge_raw r
JOIN wp_edge e ON e.entry = r.entry AND e.map = r.map
              AND e.from_key = r.from_key AND e.to_key = r.to_key;

DROP TABLE IF EXISTS wp_walk;
CREATE TABLE wp_walk (
  sniff_id BIGINT UNSIGNED NOT NULL,
  guid     VARCHAR(40) NOT NULL,
  entry    INT UNSIGNED NOT NULL,
  map      INT UNSIGNED NOT NULL,
  confirmed_steps INT NOT NULL,
  PRIMARY KEY (sniff_id, guid),
  KEY ix_entry (entry, map)
) ENGINE=InnoDB
AS
SELECT s.sniff_id, s.guid, s.entry, s.map, COUNT(*) AS confirmed_steps
FROM wp_step_ok s
JOIN creature_movement m ON m.sniff_id = s.sniff_id AND m.guid = s.guid
WHERE m.points > 0
  AND m.radius_robust >= m.points
  AND EXISTS (SELECT 1 FROM wp_step_ok n
              WHERE n.sniff_id = s.sniff_id AND n.guid = s.guid AND n.from_key = s.to_key)
GROUP BY s.sniff_id, s.guid, s.entry, s.map;

SELECT NOW() AS t, COUNT(*) AS proven_walks, COUNT(DISTINCT entry) AS entries FROM wp_walk;

-- Every X,Y a proven walk stood on, with the Z spread it was seen at. Same packing and the same
-- aggregates as phase 1, so a promoted node is shaped exactly like an earned one.
DROP TABLE IF EXISTS wp_walk_xy;
CREATE TABLE wp_walk_xy (
  entry INT UNSIGNED NOT NULL, map INT UNSIGNED NOT NULL, xy_key BIGINT NOT NULL,
  x FLOAT NOT NULL, y FLOAT NOT NULL, z FLOAT NOT NULL,
  z_lo FLOAT NOT NULL, z_hi FLOAT NOT NULL,
  n_points INT NOT NULL, n_sniffs INT NOT NULL, n_guids INT NOT NULL,
  PRIMARY KEY (entry, map, xy_key)
) ENGINE=InnoDB
AS
SELECT p.entry, p.map,
       (CAST(ROUND((p.position_x+17100)*100) AS SIGNED) << 22)
     +  CAST(ROUND((p.position_y+17100)*100) AS SIGNED) AS xy_key,
       MIN(p.position_x) AS x, MIN(p.position_y) AS y, AVG(p.position_z) AS z,
       MIN(p.position_z) AS z_lo, MAX(p.position_z) AS z_hi,
       COUNT(*) AS n_points, COUNT(DISTINCT p.sniff_id) AS n_sniffs,
       COUNT(DISTINCT p.guid) AS n_guids
FROM wp_point p
JOIN wp_walk w ON w.sniff_id = p.sniff_id AND w.guid = p.guid
GROUP BY p.entry, p.map, xy_key;

-- Stacked X,Y keep the levels phase 1b clustered for them. A flat level 0 dropped in beside
-- those would answer for a Z band belonging to another floor, which is the whole thing phase 1b
-- exists to prevent. A proven walk crossing one of those simply breaks its chain there.
DELETE k FROM wp_walk_xy k
JOIN wp_stack s ON s.entry = k.entry AND s.map = k.map AND s.xy_key = k.xy_key;

INSERT IGNORE INTO wp_node (entry, map, xy_key, level, node_key,
                            x, y, z, z_lo, z_hi, n_points, n_sniffs, n_guids)
SELECT entry, map, xy_key, 0, xy_key << 12, x, y, z, z_lo, z_hi,
       n_points, n_sniffs, n_guids
FROM wp_walk_xy;

-- A node that already existed keeps its own counts, but its Z band has to answer for the
-- observations just added to it or the edge build below silently fails to match them.
UPDATE wp_node n
JOIN wp_walk_xy k ON k.entry = n.entry AND k.map = n.map AND k.xy_key = n.xy_key
SET n.z_lo = LEAST(n.z_lo, k.z_lo), n.z_hi = GREATEST(n.z_hi, k.z_hi)
WHERE n.level = 0;

SELECT NOW() AS t, COUNT(*) AS nodes_after_promotion FROM wp_node;

-- The steps of a proven walk, rebuilt against the enlarged node set.
--
-- This is also what repairs a defect the per-point rule had on its own. Phase 2 runs its LEAD
-- over every point and only then joins both ends to wp_node, so a point that failed the test
-- did not merely vanish - it SEVERED the step spanning it. Malcolm again: his 1623.53 ->
-- 1630.20 step is confirmed by sniff 1792 alone, because the capture that also walked it
-- recorded an intermediate destination at 1628.01, 806.94 that nobody else ever saw, and that
-- traversal became two dead steps instead of the second observation. Promotion repairs it the
-- right way round - the intermediate point is KEPT, not jumped over. Bridging the two ends
-- would have invented a leg, since phase 0 already reduced every move order to its destination
-- and each surviving point is somewhere the server actually sent the creature.
DROP TABLE IF EXISTS wp_edge_walk;
CREATE TABLE wp_edge_walk (
  entry INT UNSIGNED NOT NULL, map INT UNSIGNED NOT NULL,
  from_key BIGINT NOT NULL, to_key BIGINT NOT NULL,
  n_obs INT NOT NULL, n_sniffs INT NOT NULL, n_guids INT NOT NULL,
  spline_obs INT NOT NULL, creation_obs INT NOT NULL, median_dt_ms INT NULL,
  PRIMARY KEY (entry, map, from_key, to_key)
) ENGINE=InnoDB
AS
SELECT x.entry, x.map, a.node_key AS from_key, b.node_key AS to_key,
       COUNT(*) AS n_obs, COUNT(DISTINCT x.sniff_id) AS n_sniffs,
       COUNT(DISTINCT x.guid) AS n_guids,
       SUM(x.same_segment) AS spline_obs, SUM(x.is_creation) AS creation_obs,
       CAST(AVG(x.dt_ms) AS SIGNED) AS median_dt_ms
FROM wp_step x
JOIN wp_walk v ON v.sniff_id = x.sniff_id AND v.guid = x.guid
JOIN wp_node a ON a.entry = x.entry AND a.map = x.map AND a.xy_key = x.k
              AND x.z  BETWEEN a.z_lo AND a.z_hi
JOIN wp_node b ON b.entry = x.entry AND b.map = x.map AND b.xy_key = x.nk
              AND x.nz BETWEEN b.z_lo AND b.z_hi
WHERE x.nk IS NOT NULL AND a.node_key <> b.node_key
GROUP BY x.entry, x.map, a.node_key, b.node_key;

SELECT NOW() AS t, COUNT(*) AS steps_of_proven_walks FROM wp_edge_walk;

-- Merge. An edge that already earned its place keeps the counts it earned, which are counted
-- over every walk rather than only the proven ones; INSERT IGNORE adds the rest. n_obs = 1 is
-- impossible under the phase 3 rule, so it is the marker for an edge admitted on the authority
-- of its walk, and it travels all the way to the published edge_obs.
INSERT IGNORE INTO wp_edge (entry, map, from_key, to_key, n_obs, n_sniffs, n_guids,
                            spline_obs, creation_obs, median_dt_ms)
SELECT entry, map, from_key, to_key, n_obs, n_sniffs, n_guids,
       spline_obs, creation_obs, median_dt_ms
FROM wp_edge_walk;

SELECT NOW() AS t, COUNT(*) AS edges_after_promotion,
       SUM(n_obs = 1) AS admitted_by_their_walk FROM wp_edge;

SELECT NOW() AS t, 'done' AS step;
