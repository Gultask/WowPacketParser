-- Phase 3 groups 4.1M estimates into 1.6M groups while computing four COUNT(DISTINCT)s over
-- them. On the server defaults - a 256 KB sort buffer and a 109 MB temp table limit - every one
-- of those distinct sets spills to an on-disk InnoDB temp table, and the statement ran for two
-- hours and fifty minutes without finishing, reading two billion rows that were almost all temp
-- table traffic. The query plan was never the problem: it uses ix_approx and fetches one row per
-- lookup. mine-paths.sql has carried these three settings from the start.
SET SESSION tmp_table_size = 2147483648;
SET SESSION max_heap_table_size = 2147483648;
SET SESSION sort_buffer_size = 67108864;

-- Build the publishable creature-spawn digest from the raw ingest corpus.
--
-- The raw ingest database stays local: sniff.file_name carries character names, races and
-- classes.
-- Only sniff.file_hash and aggregate counts cross into the digest, which lands in acore_world
-- as sniff_* tables so the module can read it through WorldDatabase with no extra connection.
--
--   mysql -uroot -proot wpp_ingest2 < scripts/build-digest.sql
--
-- Accuracy is the whole point of the digest, so it is a first-class column, and every level
-- of it is something the corpus PROVES rather than something inferred from absence:
--
--   2  CreateObject2 - the packet that spawns the creature. Exact to the centimetre.
--      Measured, not assumed: of 617 spawns seen with CO2 in two or more separate sniffs,
--      617 agree to within 5 cm. The same test on CO1 gives 61.3% - the static creatures -
--      with 36.3% scattered past a yard, which is the wanderers.
--
--   1  CreateObject1 at the IDENTICAL position in two or more independent sniffs. Two
--      separate captures, potentially months apart, cannot land on the same centimetre by
--      accident: the creature spawns there and does not move.
--
--      Note what this is NOT. An earlier cut of this file called a creature static whenever
--      it was never SEEN to move, which a ten-second observation satisfies trivially. That
--      turned 569k unproven points into claimed-exact ones. Absence of observed movement is
--      not evidence of a static spawn; agreement across independent captures is.
--
--   !  A caveat that no accuracy level fixes: CreateObject2 is the packet that CREATES a
--      creature for the client, and an encounter script summoning one emits the identical
--      packet. Ulduar's Mechanolift 304-A has five exact CO2 points in a tidy line on the
--      Flame Leviathan conveyor, 26-59 yards from where AC spawns it - and both are right.
--      Instances are the weak case; maps 0, 1, 530 and 571 are the strong one.
--
--   0  Everything else. This is the CENTRE of where a creature was seen, not a spawn point,
--      and the module refuses to write it into position_x/y/z.

SET SESSION tmp_table_size = 2147483648;
SET SESSION max_heap_table_size = 2147483648;

-- ---------------------------------------------------------------------------------------
-- Phase 1: one spawn estimate per observed creature instance.
--
-- A CO2 observation is the spawn point by definition, whether or not the creature then
-- wandered off. A CO1 observation is wherever the creature happened to be standing when it
-- entered view - which is the spawn point only if it does not move, so movers fall back to
-- the median of everywhere they were seen.
-- ---------------------------------------------------------------------------------------
DROP TABLE IF EXISTS dg_est;
CREATE TABLE dg_est (
  sniff_id  BIGINT UNSIGNED NOT NULL,
  -- Needed to reach creature_waypoint, which is keyed on the instance rather than the entry.
  guid      VARCHAR(40) NOT NULL,
  entry     INT UNSIGNED NOT NULL,
  map       INT UNSIGNED NOT NULL,
  x FLOAT NOT NULL, y FLOAT NOT NULL, z FLOAT NOT NULL, o FLOAT NOT NULL,
  kind      TINYINT NOT NULL,   -- 2 CO2, 1 CO1 not seen to move, 0 CO1 seen to move
  radius    FLOAT NOT NULL,
  build     INT UNSIGNED NOT NULL,
  -- The corpus is not one game. Builds run 40011 to 55002 and the branch says which client
  -- they came from: of 3,101 sniffs, 2,324 are WotLK, 717 TBC and 60 Classic. A TBC spawn
  -- written into a WotLK world is a bug, and `build` alone cannot catch it - TBC sniffs reach
  -- build 68101, HIGHER than WotLK's 54261, so MAX(build) mislabels a mixed point as TBC.
  branch    VARCHAR(16) NULL,
  -- Unit state as the packet reported it, for `creature_addon`. Kept per observation so the
  -- aggregate below can tell "always emoting" from "emoting when somebody happened to look".
  emote_state   INT UNSIGNED NOT NULL,
  stand_state   TINYINT UNSIGNED NOT NULL,
  sheathe_state TINYINT UNSIGNED NOT NULL,
  -- The 1 yard grid phase 3 groups approximate points on, stored rather than recomputed.
  -- MySQL cannot use an index for a join predicate spelled ROUND(x), so leaving these as
  -- expressions turned phase 3 into a nested-loop scan that ran forty minutes without
  -- finishing. Every place that groups or joins on the grid uses these columns.
  gx INT NOT NULL, gy INT NOT NULL, gz INT NOT NULL,
  pos_key   BIGINT NOT NULL,
  KEY ix_grp (entry, map, kind),
  KEY ix_inst (sniff_id, guid),
  KEY ix_grid (entry, map, gx, gy, gz)
) ENGINE=InnoDB;

-- The three appearance fields, per entry, and only where every observation of the entry
-- agreed. creature_spawn carried them per guid until they were moved to creature_value, which
-- aggregates within the sniff - so per-spawn-point agreement is no longer askable and per-entry
-- agreement stands in. It is the stronger claim of the two.
--
-- Sentinels are the ones the digest already used: 0 for emote, which also means "no emote to
-- report", and 255 for stand and sheathe, which means the observations disagreed.
DROP TABLE IF EXISTS dg_state;
CREATE TABLE dg_state (
  entry         INT UNSIGNED     NOT NULL,
  emote_state   INT UNSIGNED     NOT NULL,
  stand_state   TINYINT UNSIGNED NOT NULL,
  sheathe_state TINYINT UNSIGNED NOT NULL,
  PRIMARY KEY (entry)
) ENGINE=InnoDB;

INSERT INTO dg_state (entry, emote_state, stand_state, sheathe_state)
SELECT entry,
       COALESCE(MAX(CASE WHEN field = 'emote_state'   THEN v END),   0),
       COALESCE(MAX(CASE WHEN field = 'stand_state'   THEN v END), 255),
       COALESCE(MAX(CASE WHEN field = 'sheathe_state' THEN v END), 255)
FROM (
  SELECT entry, field,
         CASE WHEN COUNT(DISTINCT value) = 1 THEN CAST(MIN(value) AS UNSIGNED) END AS v
  FROM   creature_value
  WHERE  field IN ('emote_state', 'stand_state', 'sheathe_state')
  GROUP  BY entry, field
) a
GROUP  BY entry;

INSERT INTO dg_est
SELECT s.sniff_id, s.guid, s.entry, s.map,
       CASE WHEN s.create_type = 2 THEN s.position_x
            WHEN m.radius_robust > 2 THEN m.median_x ELSE s.position_x END,
       CASE WHEN s.create_type = 2 THEN s.position_y
            WHEN m.radius_robust > 2 THEN m.median_y ELSE s.position_y END,
       CASE WHEN s.create_type = 2 THEN s.position_z
            WHEN m.radius_robust > 2 THEN m.median_z ELSE s.position_z END,
       s.orientation,
       CASE WHEN s.create_type = 2 THEN 2
            WHEN m.radius_robust > 2 THEN 0 ELSE 1 END,
       COALESCE(m.radius_robust, 0),
       COALESCE(f.client_build, 0),
       f.branch,
       COALESCE(st.emote_state, 0), COALESCE(st.stand_state, 255), COALESCE(st.sheathe_state, 255),
       0, 0, 0,   -- grid key, filled in below from the STORED position
       -- centimetre-resolution packing, so exact positions group without float equality.
       -- Built from the RAW position, which equals the stored position for kinds 1 and 2;
       -- kind 0 stores a median instead and is grouped on a grid below, never on this key.
       ((CAST(ROUND((s.position_x + 17100) * 100) AS SIGNED) & 4194303) << 38)
     | ((CAST(ROUND((s.position_y + 17100) * 100) AS SIGNED) & 4194303) << 16)
     |  (CAST(ROUND((s.position_z + 2100) * 10) AS SIGNED) & 65535)
FROM creature_spawn s
LEFT JOIN creature_movement m ON m.sniff_id = s.sniff_id AND m.guid = s.guid
LEFT JOIN sniff f ON f.id = s.sniff_id
LEFT JOIN dg_state st ON st.entry = s.entry;
-- Rounded from the STORED float, not from the expression above: rounding a double and then
-- narrowing it to FLOAT can land the other side of a .5 boundary from rounding the FLOAT
-- itself, and a point in the wrong grid cell silently loses its radius to a missed join.
UPDATE dg_est SET gx = ROUND(x), gy = ROUND(y), gz = ROUND(z / 4);

-- No coverage filter here, deliberately. 139 sniffs predate the coverage ledger and hold 23%
-- of all CO2 observations. They were checked rather than assumed: 55.6% of their CO2 points
-- land on a ledgered CO2 point to the centimetre, and they scatter LESS than ledgered data,
-- not more, which is the opposite of what a mislabelling collector would produce.

-- ---------------------------------------------------------------------------------------
-- Phase 1b: the radius, measured from the spawn point.
--
-- What creature_movement gives is `radius_robust`: the p99 distance from the MEDIAN of where
-- a creature was seen. That is the wrong centre. AzerothCore's wander_distance is a radius
-- around the spawn row's own position_x/y/z, so the distance that matters is the distance
-- from the estimate this digest is about to publish - which for a CO2 point is the spawn
-- packet itself, not a centroid of wandering.
--
-- So: for every instance, the furthest waypoint from ITS OWN estimate. An instance with no
-- waypoint rows never moved anywhere the parser recorded, and keeps radius_robust rather than
-- being forced to zero.
--
-- This reads raw creature_waypoint ON PURPOSE, not the wp_point that mine-paths.sql filters
-- down to move-order destinations. Do not "make it consistent". Those two want opposite things:
-- a spline interior is useless as a WAYPOINT, because the server computed it from the terrain
-- and its own pathfinding rather than reading it out of a table, but it is a perfectly real
-- OBSERVATION of somewhere the creature stood. Radius is a MAX over observations, so dropping
-- them could only shrink it - and shrinking a measured distance is the one thing this pipeline
-- is not allowed to do.
-- ---------------------------------------------------------------------------------------
DROP TABLE IF EXISTS dg_inst_rad;
CREATE TABLE dg_inst_rad (
  sniff_id BIGINT UNSIGNED NOT NULL,
  guid     VARCHAR(40) NOT NULL,
  far      FLOAT NOT NULL,
  points   INT UNSIGNED NOT NULL,
  PRIMARY KEY (sniff_id, guid)
) ENGINE=InnoDB;

INSERT INTO dg_inst_rad
SELECT w.sniff_id, w.guid,
       SQRT(MAX(POW(w.position_x - e.x, 2) + POW(w.position_y - e.y, 2)
              + POW(w.position_z - e.z, 2))),
       COUNT(*)
FROM creature_waypoint w
JOIN dg_est e ON e.sniff_id = w.sniff_id AND e.guid = w.guid
GROUP BY w.sniff_id, w.guid;

UPDATE dg_est e
JOIN dg_inst_rad r ON r.sniff_id = e.sniff_id AND r.guid = e.guid
SET e.radius = r.far;

-- ---------------------------------------------------------------------------------------
-- Phase 1c: one radius per POINT, at the 90th percentile across captures rather than MAX.
--
-- MAX let a single capture define the answer, and that is how the published digest ended up
-- claiming a 7,369 yard wander radius for an Ice Steppe Rhino seen four times. One capture
-- where the creature was charmed, feared, or stitched onto another guid is enough.
--
-- The percentile degrades the right way when data is thin: at n=1 it is that one value, at
-- n=2 the larger of two, and only once there are ten captures does it start discarding the
-- worst. It never invents a number the corpus did not contain.
--
-- Three grouping keys because the three point tables group three different ways, and the
-- radius has to be computed on the same grouping the position was.
-- ---------------------------------------------------------------------------------------
DROP TABLE IF EXISTS dg_rad_key;
CREATE TABLE dg_rad_key (
  kind TINYINT NOT NULL,
  entry INT UNSIGNED NOT NULL, map INT UNSIGNED NOT NULL,
  gx INT NOT NULL, gy INT NOT NULL, gz INT NOT NULL, pos_key BIGINT NOT NULL,
  radius FLOAT NOT NULL,
  KEY ix_fixed (kind, entry, map, pos_key),
  KEY ix_approx (kind, entry, map, gx, gy, gz)
) ENGINE=InnoDB;

-- exact groupings, one row per (entry, map, pos_key) for kind 2 and kind 1
INSERT INTO dg_rad_key (kind, entry, map, gx, gy, gz, pos_key, radius)
SELECT kind, entry, map, 0, 0, 0, pos_key, MAX(radius)
FROM (
    SELECT kind, entry, map, pos_key, radius,
           ROW_NUMBER() OVER (PARTITION BY kind, entry, map, pos_key ORDER BY radius) rn,
           COUNT(*)     OVER (PARTITION BY kind, entry, map, pos_key) n
    FROM dg_est WHERE kind >= 1
) t
WHERE rn <= CEIL(0.9 * n)
GROUP BY kind, entry, map, pos_key;

-- the approximate grouping, on the 1 yard grid phase 3 uses
INSERT INTO dg_rad_key (kind, entry, map, gx, gy, gz, pos_key, radius)
SELECT 0, entry, map, gx, gy, gz, 0, MAX(radius)
FROM (
    SELECT entry, map, gx, gy, gz, radius,
           ROW_NUMBER() OVER (PARTITION BY entry, map, gx, gy, gz ORDER BY radius) rn,
           COUNT(*)     OVER (PARTITION BY entry, map, gx, gy, gz) n
    FROM dg_est WHERE kind < 2
) t
WHERE rn <= CEIL(0.9 * n)
GROUP BY entry, map, gx, gy, gz;

-- ---------------------------------------------------------------------------------------
-- Phase 2: the two kinds of proven-exact point, both grouped on the exact position key so
-- there is no clustering tolerance to argue with.
-- ---------------------------------------------------------------------------------------
DROP TABLE IF EXISTS dg_fixed;
CREATE TABLE dg_fixed (
  entry INT UNSIGNED NOT NULL, map INT UNSIGNED NOT NULL,
  x FLOAT NOT NULL, y FLOAT NOT NULL, z FLOAT NOT NULL, o FLOAT NOT NULL,
  accuracy TINYINT NOT NULL, radius FLOAT NOT NULL,
  observations INT UNSIGNED NOT NULL, sniffs INT UNSIGNED NOT NULL, build INT UNSIGNED NOT NULL,
  wotlk_sniffs   INT UNSIGNED NOT NULL,
  tbc_sniffs     INT UNSIGNED NOT NULL,
  classic_sniffs INT UNSIGNED NOT NULL,
  emote_state    INT UNSIGNED NOT NULL,
  stand_state    TINYINT UNSIGNED NOT NULL,
  sheathe_state  TINYINT UNSIGNED NOT NULL,
  KEY ix (entry, map), KEY ix_pos (map, x, y)
) ENGINE=InnoDB;

-- 2a: CO2. One sniff is enough - the spawn packet is not an estimate.
INSERT INTO dg_fixed
SELECT e.entry, e.map, AVG(e.x), AVG(e.y), AVG(e.z), AVG(e.o), 2,
       COALESCE(MAX(k.radius), 0), COUNT(*), COUNT(DISTINCT e.sniff_id), MAX(e.build),
       COUNT(DISTINCT CASE WHEN branch = 'WotLK'   THEN sniff_id END),
       COUNT(DISTINCT CASE WHEN branch = 'TBC'     THEN sniff_id END),
       COUNT(DISTINCT CASE WHEN branch = 'Classic' THEN sniff_id END),
       CASE WHEN MIN(emote_state)   = MAX(emote_state)   THEN MIN(emote_state)   ELSE 0 END,
       CASE WHEN MIN(stand_state)   = MAX(stand_state)   THEN MIN(stand_state)   ELSE 255 END,
       CASE WHEN MIN(sheathe_state) = MAX(sheathe_state) THEN MIN(sheathe_state) ELSE 255 END
FROM dg_est e
LEFT JOIN dg_rad_key k ON k.kind = 2 AND k.entry = e.entry AND k.map = e.map
                      AND k.pos_key = e.pos_key
WHERE e.kind = 2
GROUP BY e.entry, e.map, e.pos_key;

-- 2b: CO1 landing on the same centimetre in two or more independent captures.
INSERT INTO dg_fixed
SELECT e.entry, e.map, AVG(e.x), AVG(e.y), AVG(e.z), AVG(e.o), 1,
       COALESCE(MAX(k.radius), 0), COUNT(*), COUNT(DISTINCT e.sniff_id), MAX(e.build),
       COUNT(DISTINCT CASE WHEN branch = 'WotLK'   THEN sniff_id END),
       COUNT(DISTINCT CASE WHEN branch = 'TBC'     THEN sniff_id END),
       COUNT(DISTINCT CASE WHEN branch = 'Classic' THEN sniff_id END),
       CASE WHEN MIN(emote_state)   = MAX(emote_state)   THEN MIN(emote_state)   ELSE 0 END,
       CASE WHEN MIN(stand_state)   = MAX(stand_state)   THEN MIN(stand_state)   ELSE 255 END,
       CASE WHEN MIN(sheathe_state) = MAX(sheathe_state) THEN MIN(sheathe_state) ELSE 255 END
FROM dg_est e
LEFT JOIN dg_rad_key k ON k.kind = 1 AND k.entry = e.entry AND k.map = e.map
                      AND k.pos_key = e.pos_key
WHERE e.kind = 1
GROUP BY e.entry, e.map, e.pos_key
HAVING COUNT(DISTINCT e.sniff_id) >= 2;

-- ---------------------------------------------------------------------------------------
-- Phase 3: approximate points, from everything CO1. Snapped to a 1 yard grid, which is
-- coarser than the estimate deserves but keeps the grouping honest about its own resolution.
-- A cluster that straddles a grid boundary splits in two; the module shows every nearby
-- digest point rather than picking one, so a split shows up instead of hiding.
-- ---------------------------------------------------------------------------------------
DROP TABLE IF EXISTS dg_approx;
CREATE TABLE dg_approx (
  entry INT UNSIGNED NOT NULL, map INT UNSIGNED NOT NULL,
  x FLOAT NOT NULL, y FLOAT NOT NULL, z FLOAT NOT NULL, o FLOAT NOT NULL,
  radius FLOAT NOT NULL,
  observations INT UNSIGNED NOT NULL, sniffs INT UNSIGNED NOT NULL, build INT UNSIGNED NOT NULL,
  wotlk_sniffs   INT UNSIGNED NOT NULL,
  tbc_sniffs     INT UNSIGNED NOT NULL,
  classic_sniffs INT UNSIGNED NOT NULL,
  emote_state    INT UNSIGNED NOT NULL,
  stand_state    TINYINT UNSIGNED NOT NULL,
  sheathe_state  TINYINT UNSIGNED NOT NULL,
  KEY ix (entry, map)
) ENGINE=InnoDB;

INSERT INTO dg_approx
SELECT e.entry, e.map, AVG(e.x), AVG(e.y), AVG(e.z), AVG(e.o),
       COALESCE(MAX(k.radius), 0), COUNT(*), COUNT(DISTINCT e.sniff_id), MAX(e.build),
       COUNT(DISTINCT CASE WHEN branch = 'WotLK'   THEN sniff_id END),
       COUNT(DISTINCT CASE WHEN branch = 'TBC'     THEN sniff_id END),
       COUNT(DISTINCT CASE WHEN branch = 'Classic' THEN sniff_id END),
       CASE WHEN MIN(emote_state)   = MAX(emote_state)   THEN MIN(emote_state)   ELSE 0 END,
       CASE WHEN MIN(stand_state)   = MAX(stand_state)   THEN MIN(stand_state)   ELSE 255 END,
       CASE WHEN MIN(sheathe_state) = MAX(sheathe_state) THEN MIN(sheathe_state) ELSE 255 END
FROM dg_est e
LEFT JOIN dg_rad_key k ON k.kind = 0 AND k.entry = e.entry AND k.map = e.map
                      AND k.gx = e.gx AND k.gy = e.gy AND k.gz = e.gz
WHERE e.kind < 2
GROUP BY e.entry, e.map, e.gx, e.gy, e.gz
-- A point supported by a single capture is not worth publishing: it cannot be cross-checked,
-- and an unverifiable approximate position is exactly the kind of row that gets trusted anyway.
HAVING COUNT(DISTINCT e.sniff_id) >= 2;

-- ---------------------------------------------------------------------------------------
-- Phase 4: merge. An approximate point within 5 yards of a proven-exact one is the same
-- spawn seen the worse way, so the exact point wins and the approximate row is dropped.
-- ---------------------------------------------------------------------------------------
DROP TABLE IF EXISTS dg_spawn;
CREATE TABLE dg_spawn (
  entry INT UNSIGNED NOT NULL, map INT UNSIGNED NOT NULL,
  x FLOAT NOT NULL, y FLOAT NOT NULL, z FLOAT NOT NULL, o FLOAT NOT NULL,
  accuracy TINYINT NOT NULL, radius FLOAT NOT NULL,
  observations INT UNSIGNED NOT NULL, sniffs INT UNSIGNED NOT NULL, build INT UNSIGNED NOT NULL,
  wotlk_sniffs   INT UNSIGNED NOT NULL,
  tbc_sniffs     INT UNSIGNED NOT NULL,
  classic_sniffs INT UNSIGNED NOT NULL,
  emote_state    INT UNSIGNED NOT NULL,
  stand_state    TINYINT UNSIGNED NOT NULL,
  sheathe_state  TINYINT UNSIGNED NOT NULL,
  KEY ix (entry, map), KEY ix_pos (map, x, y)
) ENGINE=InnoDB;

INSERT INTO dg_spawn
SELECT entry, map, x, y, z, o, accuracy, radius, observations, sniffs, build,
       wotlk_sniffs, tbc_sniffs, classic_sniffs,
       emote_state, stand_state, sheathe_state FROM dg_fixed;

INSERT INTO dg_spawn
SELECT a.entry, a.map, a.x, a.y, a.z, a.o, 0, a.radius, a.observations, a.sniffs, a.build,
       a.wotlk_sniffs, a.tbc_sniffs, a.classic_sniffs,
       a.emote_state, a.stand_state, a.sheathe_state
FROM dg_approx a
WHERE NOT EXISTS (
  SELECT 1 FROM dg_fixed e
  WHERE e.entry = a.entry AND e.map = a.map
    AND POW(e.x - a.x, 2) + POW(e.y - a.y, 2) + POW(e.z - a.z, 2) < 25
);

-- An absorbed approximate point is the same spawn seen the worse way, so the wander it
-- measured belongs to the exact point that replaced it. Dropping the row used to drop that
-- evidence with it, which is how an exact CO2 point could end up claiming radius 0 while the
-- approximate row five yards away had measured the creature roaming twenty.
UPDATE dg_spawn e
JOIN (
    SELECT f.entry, f.map, f.x, f.y, f.z, MAX(a.radius) AS absorbed
    FROM dg_fixed f
    JOIN dg_approx a ON a.entry = f.entry AND a.map = f.map
                    AND POW(f.x - a.x, 2) + POW(f.y - a.y, 2) + POW(f.z - a.z, 2) < 25
    GROUP BY f.entry, f.map, f.x, f.y, f.z
) g ON g.entry = e.entry AND g.map = e.map AND g.x = e.x AND g.y = e.y AND g.z = e.z
SET e.radius = GREATEST(e.radius, g.absorbed)
WHERE e.accuracy > 0;

-- ---------------------------------------------------------------------------------------
-- Phase 4b: the sanity ceiling.
--
-- A wander radius of three hundred yards is not a wander radius. It is a creature that was
-- charmed, feared, taxied, or stitched onto another guid by the parser, and no percentile
-- removes it when the point has only four captures to begin with. AzerothCore's own data
-- agrees on the scale: of 78,450 rows with a wander_distance, 38 exceed 50 and 2 exceed 100.
--
-- Above the ceiling the radius is set to 0 and `radius_unmeasured` is set instead. That is a
-- different claim from "this creature does not move", and the module has to be able to tell
-- them apart - otherwise a rhino that roams becomes a rhino published as standing still.
-- ---------------------------------------------------------------------------------------
ALTER TABLE dg_spawn ADD COLUMN radius_unmeasured TINYINT UNSIGNED NOT NULL DEFAULT 0;

UPDATE dg_spawn SET radius_unmeasured = 1, radius = 0 WHERE radius > 50;

-- ---------------------------------------------------------------------------------------
-- Phase 4c: patrol detection.
--
-- `radius` is the spread of where a creature was seen. For a WANDERER that is the wander
-- circle. For a PATROLLER it is the extent of the route, which is a completely different
-- thing, and publishing it as wander_distance would be a regression: AzerothCore already has
-- Booty Bay Bruiser and Sage Korolusk as MovementType 2 with wander_distance 0, correctly.
--
-- The separator is the reconstructed routes. A spawn point within 20 yards of a route of the
-- same entry and map patrols. Routes shorter than 4 points do not count - a 3 point chain is
-- one turn, and random movement throws off plenty of those by chance.
--
-- The measurement backs it up: points sitting on a route average 24.2 yards of "radius",
-- points with no route at all average 7.5.
-- ---------------------------------------------------------------------------------------
DROP TABLE IF EXISTS dg_route_pt;
CREATE TABLE dg_route_pt (entry INT UNSIGNED, map INT UNSIGNED, x FLOAT, y FLOAT, z FLOAT,
  KEY ix (entry, map, x, y)) ENGINE=InnoDB
SELECT pp.entry, pp.map, pp.x, pp.y, pp.z
FROM path_point pp JOIN path_summary ps ON ps.path_id = pp.path_id
WHERE ps.n_points >= 4;

ALTER TABLE dg_spawn ADD COLUMN path_dist FLOAT NOT NULL DEFAULT -1,
                     ADD COLUMN patrols TINYINT NOT NULL DEFAULT 0;

UPDATE dg_spawn s
SET path_dist = COALESCE((
  SELECT MIN(SQRT(POW(r.x - s.x, 2) + POW(r.y - s.y, 2)))
  FROM dg_route_pt r
  WHERE r.entry = s.entry AND r.map = s.map
    AND r.x BETWEEN s.x - 60 AND s.x + 60 AND r.y BETWEEN s.y - 60 AND s.y + 60), -1);

UPDATE dg_spawn SET patrols = (path_dist >= 0 AND path_dist <= 20);

-- ---------------------------------------------------------------------------------------
-- Phase 5: publish into acore_world for the module. Nothing identifying crosses over -
-- only positions, counts and the client build.
-- ---------------------------------------------------------------------------------------
DROP TABLE IF EXISTS acore_world.sniff_creature_spawn;
CREATE TABLE acore_world.sniff_creature_spawn (
  entry        INT UNSIGNED NOT NULL,
  map          SMALLINT UNSIGNED NOT NULL,
  x FLOAT NOT NULL, y FLOAT NOT NULL, z FLOAT NOT NULL, o FLOAT NOT NULL,
  accuracy     TINYINT UNSIGNED NOT NULL COMMENT '2 exact CO2 spawn packet, 1 exact CO1 never moved, 0 approximate centre of a wanderer',
  -- The 90th percentile, ACROSS captures, of how far this creature got from this point in any
  -- one capture - measured against the point itself, not against a centroid of wandering,
  -- because that is what AzerothCore's wander_distance is a radius around.
  radius       FLOAT NOT NULL COMMENT '90th pct roam from THIS point, a floor not a ceiling; NOT a wander_distance when patrols=1',
  -- Set when the measurement was thrown out for being impossible rather than being zero. A
  -- creature that reads as roaming 7,000 yards was charmed, feared, taxied or stitched onto
  -- another guid; the radius goes to 0, but that 0 must not be read as "stands still", or a
  -- roaming spawn gets published as MovementType IDLE. See the sanity ceiling in phase 4b.
  radius_unmeasured TINYINT UNSIGNED NOT NULL COMMENT '1 = measurement discarded as impossible, not zero; do not read the 0 as "stands still"',
  patrols      TINYINT UNSIGNED NOT NULL COMMENT '1 when a reconstructed route passes within 20 yd; zero the radius before using it as wander_distance',
  observations INT UNSIGNED NOT NULL COMMENT 'times this point was seen, across all captures',
  sniffs       INT UNSIGNED NOT NULL COMMENT 'independent captures supporting this point; the sum of the three per-client counts',
  build        INT UNSIGNED NOT NULL COMMENT 'client build, for VerifiedBuild',
  -- Trigger/invisible, taken from the SNIFFED unit_flags rather than AzerothCore's
  -- creature_template. flags_extra is not sniffed data - it is AzerothCore's own annotation -
  -- and the template's unit_flags is a copy that has drifted: the packets call 503 entries
  -- NOT_SELECTABLE that the template does not. The flag matters because a trigger is invisible
  -- in play, so its appearance in a capture is itself the evidence that it is placed there,
  -- which is what lets its CO1 average survive a filter that discards everyone else's.
  trigger_npc  TINYINT UNSIGNED NOT NULL COMMENT 'NOT_SELECTABLE in every sniffed unit_flags; from the packets, not creature_template',
  -- How many independent captures OF EACH CLIENT saw this point. `sniffs` is their sum and is
  -- not enough on its own: a point with 6 sniffs and wotlk_sniffs = 0 is a Burning Crusade
  -- spawn, and writing it into a WotLK world is how a creature appears somewhere it never was.
  -- 7,891 of the 45,603 CO2 points have no WotLK evidence at all.
  wotlk_sniffs   INT UNSIGNED NOT NULL COMMENT 'independent WotLK captures; 0 here means no WotLK evidence whatever `sniffs` says',
  tbc_sniffs     INT UNSIGNED NOT NULL COMMENT 'independent Burning Crusade captures',
  classic_sniffs INT UNSIGNED NOT NULL COMMENT 'independent Classic captures',
  -- What the creature was doing, for `creature_addon`: emote, stand state, sheathe state.
  -- Written only when EVERY observation of the point agreed. A creature caught sitting once
  -- and standing twice was on a timer, and picking one of those would be a guess with a
  -- database row's worth of authority.
  --
  -- 255 in stand_state or sheathe_state means the observations disagreed - 329 and 2,272 of the
  -- 45,603 CO2 points. It is a separate value from 0 on purpose: 0 is a real state (standing,
  -- unarmed) and writing it over a disagreement would change behaviour rather than record it.
  -- emote_state has no sentinel because 0 already means "no emote to report".
  emote_state    INT UNSIGNED NOT NULL COMMENT 'only when every observation agreed; 0 means no emote to report',
  stand_state    TINYINT UNSIGNED NOT NULL COMMENT 'only when every observation agreed; 255 = they disagreed, which is not 0',
  sheathe_state  TINYINT UNSIGNED NOT NULL COMMENT 'only when every observation agreed; 255 = they disagreed, which is not 0',
  KEY ix_entry (entry, map),
  KEY ix_pos (map, x, y)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Set only when EVERY observation of the entry had the flag. 1,746 entries qualify; another
-- 227 carry it in some captures and not others, averaging 47% - those are ordinary creatures
-- that hide sometimes, and an average of where one of those was seen is worth no more than
-- anyone else's.
INSERT INTO acore_world.sniff_creature_spawn
  (entry, map, x, y, z, o, accuracy, radius, patrols, observations, sniffs, build, trigger_npc,
   wotlk_sniffs, tbc_sniffs, classic_sniffs, emote_state, stand_state, sheathe_state,
   radius_unmeasured)
SELECT d.entry, d.map, d.x, d.y, d.z, d.o, d.accuracy, d.radius, d.patrols,
       d.observations, d.sniffs, d.build, (tr.entry IS NOT NULL),
       d.wotlk_sniffs, d.tbc_sniffs, d.classic_sniffs,
       d.emote_state, d.stand_state, d.sheathe_state, d.radius_unmeasured
FROM dg_spawn d
-- From creature_value, and from the RAW value: NOT_SELECTABLE is one of the runtime bits
-- entry-values.sql masks away, so entry_value would answer this question with a zero.
LEFT JOIN (SELECT entry FROM creature_value
           WHERE field = 'unit_flags'
           GROUP BY entry
           HAVING MIN((CAST(value AS UNSIGNED) & 0x02000000) > 0) = 1) tr ON tr.entry = d.entry;

-- ---------------------------------------------------------------------------------------
-- Phase 6: publish the routes. Same filter phase 4c uses to call a spawn a patroller - a
-- three point chain is one turn, and random movement throws off plenty of those by chance.
-- Rebuilt by mine-paths.sh; this only copies it across, so re-run that first if the routes
-- themselves are stale.
-- ---------------------------------------------------------------------------------------
SELECT NOW() AS t, 'phase 6: publish routes' AS step;

DROP TABLE IF EXISTS acore_world.sniff_creature_path;
CREATE TABLE acore_world.sniff_creature_path (
  path_id     INT UNSIGNED NOT NULL,
  entry       INT UNSIGNED NOT NULL,
  map         SMALLINT UNSIGNED NOT NULL,
  seq         SMALLINT UNSIGNED NOT NULL,
  x FLOAT NOT NULL, y FLOAT NOT NULL, z FLOAT NOT NULL,
  -- Independent captures that confirmed the edge leaving this point. The last point of an open
  -- route has no outgoing edge and carries its incoming one instead.
  edge_sniffs INT UNSIGNED NOT NULL COMMENT 'independent captures confirming the edge LEAVING this point; the last point of an open route carries its incoming one',
  -- Total traversals of that same edge, which is what admits it into the corpus at all. An edge
  -- exists once it has been walked TWICE, whoever was watching: random movement never picks the
  -- same next destination twice, so a repeated ordered pair is authored data and a second lap
  -- proves it as well as a second capture does.
  --
  -- Read the two together. `edge_sniffs` >= 2 is the stronger evidence and the chainer still
  -- prefers it when building routes. `edge_sniffs` = 1 now means one person recorded it - which
  -- is common and often the ONLY record, because a route nobody else walked past has no second
  -- capture to wait for. `edge_obs` is what says whether that one person saw it twice or forty
  -- times. Filtering on `edge_sniffs` alone throws away every route a single player captured
  -- completely; the earlier corpus did exactly that and lost 929,752 already-proven nodes.
  edge_obs    INT UNSIGNED NOT NULL COMMENT 'total traversals of that edge; with edge_sniffs = 1 this is one capture watching that many laps',
  -- The seq the last point leads back to, or -1 for a route that never returns to itself. 0 is
  -- a plain ring. Anything higher is a route that walks in and then circles, so the approach is
  -- real and the loop starts part way along.
  --
  -- Not a flag, because a flag cannot say WHERE it closes and assuming the first point invents
  -- a step: of 16,100 routes that returned to themselves, 10,220 returned to an interior point,
  -- a median of 19.3 yards from the one a flag would have drawn to.
  close_seq   SMALLINT NOT NULL COMMENT 'seq the last point leads back to, -1 if it never closes, 0 a plain ring, higher = walks in then circles',
  -- A longer published route that walks every one of this route's points, within a quarter yard.
  -- One authored walk comes out as several routes: the chainer takes the strongest edges first,
  -- so a spurious A->C edge left by a capture that missed B fits no chain and starts a fresh
  -- route from the leftovers. 10,439 of 36,756 routes carry this, and the covering route's entry
  -- says which of the two shapes it is:
  --
  --   same entry      9,082 - a gap artifact. The same walk, seen with holes in it. Drop it.
  --   different entry 1,357 - several creatures on one authored circuit, which is ordinary. Three
  --                           Bloodfury harpy entries share one Stonetalon loop and only 4027's
  --                           capture of it closes. Fold them and keep every name, or "who
  --                           patrols this" loses two thirds of its answer.
  --
  -- Nothing is filtered on the way in. This states a geometric fact; the two readings above want
  -- opposite handling, so the policy belongs to whoever is asking.
  covered_by  INT UNSIGNED NOT NULL DEFAULT 0 COMMENT 'a longer route walking all of this one''s ground, 0 if none; same entry = gap artifact, different entry = shared circuit',
  PRIMARY KEY (path_id, seq),
  KEY ix_entry (entry, map),
  KEY ix_pos (map, x, y)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- The LEFT JOIN is what keeps covered_by pointing at something real: the chainer computes it
-- over every route it built, and this publish step drops the ones under four points. A reference
-- to a route nobody can look up is worse than no reference. It is not a formality: 20 rows land
-- there on the current corpus, because n_points is the chain length while coverage is judged on
-- DISTINCT nodes, so a three-point chain that stands on one node twice can still cover a
-- four-point route.
INSERT INTO acore_world.sniff_creature_path
  (path_id, entry, map, seq, x, y, z, edge_sniffs, edge_obs, close_seq, covered_by)
SELECT pp.path_id, pp.entry, pp.map, pp.seq, pp.x, pp.y, pp.z, pp.edge_sniffs, pp.edge_obs,
       ps.close_seq, IFNULL(cov.path_id, 0)
FROM path_point pp
JOIN path_summary ps ON ps.path_id = pp.path_id
LEFT JOIN path_summary cov ON cov.path_id = ps.covered_by AND cov.n_points >= 4
WHERE ps.n_points >= 4;

SELECT NOW() AS t, COUNT(DISTINCT path_id) AS published_paths, COUNT(*) AS published_points
FROM acore_world.sniff_creature_path;

SELECT NOW() AS t, 'routes covered by a longer one' AS step,
       COUNT(DISTINCT CASE WHEN c.entry =  p.entry THEN p.path_id END) AS gap_artifacts,
       COUNT(DISTINCT CASE WHEN c.entry <> p.entry THEN p.path_id END) AS shared_circuits
FROM acore_world.sniff_creature_path p
JOIN acore_world.sniff_creature_path c ON c.path_id = p.covered_by AND c.seq = 0
WHERE p.covered_by > 0;
