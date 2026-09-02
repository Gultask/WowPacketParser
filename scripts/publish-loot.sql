-- The server defaults - a 256 KB sort buffer and a 109 MB temp table limit - push the grouped
-- pivots below onto disk. build-digest.sql lost two hours and fifty minutes to exactly that
-- before these three lines were added to it.
SET SESSION tmp_table_size = 2147483648;
SET SESSION max_heap_table_size = 2147483648;
SET SESSION sort_buffer_size = 67108864;

-- ===========================================================================================
-- publish-loot.sql - build the publishable loot and gameobject tables.
--
-- Reads wpp_ingest, writes sniff_* into acore_world beside the creature digest, so the whole
-- published dataset lives in one schema and publishing is one command:
--   mysqldump acore_world sniff_names sniff_loot_set sniff_loot_item sniff_gameobject_point > loot.sql
-- (sniff_loot is a view over sniff_loot_set and comes along with --routines off; dump it too if the
--  consumer wants names without joining.)
--
-- Nothing identifying crosses into a sniff_ table:
--   - sniff.file_name carries character names, races and classes. Only file_hash is published.
--   - loot_instance.owner_guid is a creature or gameobject guid EXCEPT where owner_type is
--     Player or ActivePlayer, which are character guids. Phase 0 destroys those.
--   - wpp.object_names has no Player rows at all - the types present are Spell, Unit, Sound,
--     Item, GameObject, Quest, Achievement, Area, Map, LFGDungeon, Zone and Battleground. Only
--     the three this data needs are copied anyway.
--
-- READ THIS BEFORE USING THE DATA. Every sniff in this corpus is from a CLASSIC-ERA client -
-- builds 40011 to 68101, not 3.3.5a build 12340. `branch` says which: of 231,166 loots, 176,729
-- are WotLK, 54,412 are TBC and 25 are Classic. Anyone porting to AzerothCore 3.3.5 wants
-- `WHERE branch = 'WotLK'` and should still expect Classic-era retuning. It is the closest
-- available evidence, not a recording of the original client.
-- ===========================================================================================

USE wpp_ingest;

-- -------------------------------------------------------------------------------------------
-- Phase 0: destroy the character guids.
--
-- 111 rows of 231,377 - a player's own loot window, not a creature's. The guid is the only part
-- that identifies anybody, so it is nulled at the source rather than merely filtered on the way
-- out: a column that is already empty cannot be published by accident later.
-- -------------------------------------------------------------------------------------------
UPDATE loot_instance SET owner_guid = NULL
WHERE owner_type IN ('Player', 'ActivePlayer');

-- -------------------------------------------------------------------------------------------
-- Phase 1: the loot instances that are not worth publishing.
--
-- Two kinds, both misparses rather than data:
--   - a loot with the same slot filled twice (39 instances). One example carries quantity
--     223,019,009 in slot 0, which is a misread packet, not a stack of cloth.
--   - a loot holding an item with a quantity of 0 or over 1000. The largest stack in the game
--     is 1000; the largest here is 1,045,039,105.
-- The whole instance goes, not the offending row: a drop SET with one item quietly removed is
-- worse than no drop set, because it looks complete.
-- -------------------------------------------------------------------------------------------
DROP TABLE IF EXISTS acore_world.sniff_reject;
CREATE TABLE acore_world.sniff_reject (
  sniff_id   BIGINT UNSIGNED NOT NULL,
  loot_index INT NOT NULL,
  reason     VARCHAR(32) NOT NULL,
  PRIMARY KEY (sniff_id, loot_index)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO acore_world.sniff_reject
SELECT sniff_id, loot_index, 'duplicate slot' FROM loot_instance_item
GROUP BY sniff_id, loot_index, slot HAVING COUNT(*) > 1
ON DUPLICATE KEY UPDATE reason = reason;

INSERT INTO acore_world.sniff_reject
SELECT sniff_id, loot_index, 'impossible quantity' FROM loot_instance_item
WHERE quantity = 0 OR quantity > 1000
GROUP BY sniff_id, loot_index
ON DUPLICATE KEY UPDATE reason = reason;

-- -------------------------------------------------------------------------------------------
-- Phase 2: names. Three types, which is all loot needs.
-- -------------------------------------------------------------------------------------------
DROP TABLE IF EXISTS acore_world.sniff_names;
CREATE TABLE acore_world.sniff_names (
  object_type ENUM('Item','Unit','GameObject') NOT NULL,
  id          INT UNSIGNED NOT NULL,
  name        VARCHAR(255) NOT NULL,
  PRIMARY KEY (object_type, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO acore_world.sniff_names
SELECT ObjectType, Id, LEFT(Name, 255) FROM wpp.object_names
WHERE ObjectType IN ('Item', 'Unit', 'GameObject');

-- -------------------------------------------------------------------------------------------
-- Phase 3: one row per loot instance, one item per column.
--
-- Items are ordered by item_id, NOT by the slot the packet used. That is the whole point: two
-- kills that dropped the same set produce identical rows, so a GROUP BY over the columns counts
-- drop patterns and identical rows sort together where you can see them. Packet slot order and
-- quantity are preserved in sniff_loot_item for anyone who wants them.
--
-- Sixteen columns because sixteen is the client's own hard limit on loot slots in this era.
-- The widest loot in the corpus is thirteen (an Onyxia kill), so the last three stay empty here
-- and the table still cannot be outgrown by a future ingest. The sanity check at the bottom
-- fails loudly rather than letting a seventeenth item disappear.
--
-- q1..q16 carry the stack size of the item in the matching i column. They are deliberately NOT
-- part of the grouping key: two kills that dropped the same items in different stack sizes are
-- the same drop pattern, and GROUP BY i1..i16 must still count them together.
-- -------------------------------------------------------------------------------------------
DROP TABLE IF EXISTS acore_world.sniff_loot_set;
CREATE TABLE acore_world.sniff_loot_set (
  loot_id     BIGINT UNSIGNED NOT NULL,
  branch      VARCHAR(16) NULL,
  build       INT NULL,
  sniff_hash  CHAR(64) NOT NULL,
  owner_type  VARCHAR(16) NULL,
  owner_entry INT UNSIGNED NULL,
  owner_level INT NULL,
  map         INT UNSIGNED NULL,
  coins       INT UNSIGNED NOT NULL,
  items       INT NOT NULL,
  i1 INT UNSIGNED NULL, i2 INT UNSIGNED NULL, i3 INT UNSIGNED NULL, i4 INT UNSIGNED NULL,
  i5 INT UNSIGNED NULL, i6 INT UNSIGNED NULL, i7 INT UNSIGNED NULL, i8 INT UNSIGNED NULL,
  i9 INT UNSIGNED NULL, i10 INT UNSIGNED NULL, i11 INT UNSIGNED NULL, i12 INT UNSIGNED NULL,
  i13 INT UNSIGNED NULL, i14 INT UNSIGNED NULL, i15 INT UNSIGNED NULL, i16 INT UNSIGNED NULL,
  q1 INT UNSIGNED NULL, q2 INT UNSIGNED NULL, q3 INT UNSIGNED NULL, q4 INT UNSIGNED NULL,
  q5 INT UNSIGNED NULL, q6 INT UNSIGNED NULL, q7 INT UNSIGNED NULL, q8 INT UNSIGNED NULL,
  q9 INT UNSIGNED NULL, q10 INT UNSIGNED NULL, q11 INT UNSIGNED NULL, q12 INT UNSIGNED NULL,
  q13 INT UNSIGNED NULL, q14 INT UNSIGNED NULL, q15 INT UNSIGNED NULL, q16 INT UNSIGNED NULL,
  PRIMARY KEY (loot_id),
  KEY ix_owner (owner_type, owner_entry, branch),
  KEY ix_items (items)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO acore_world.sniff_loot_set
SELECT l.id, s.branch, s.client_build, s.file_hash, l.owner_type, l.owner_entry, l.owner_level,
       l.map, l.coins, l.item_count,
       MAX(CASE WHEN r.rn = 1  THEN r.item_id END), MAX(CASE WHEN r.rn = 2  THEN r.item_id END),
       MAX(CASE WHEN r.rn = 3  THEN r.item_id END), MAX(CASE WHEN r.rn = 4  THEN r.item_id END),
       MAX(CASE WHEN r.rn = 5  THEN r.item_id END), MAX(CASE WHEN r.rn = 6  THEN r.item_id END),
       MAX(CASE WHEN r.rn = 7  THEN r.item_id END), MAX(CASE WHEN r.rn = 8  THEN r.item_id END),
       MAX(CASE WHEN r.rn = 9  THEN r.item_id END), MAX(CASE WHEN r.rn = 10 THEN r.item_id END),
       MAX(CASE WHEN r.rn = 11 THEN r.item_id END), MAX(CASE WHEN r.rn = 12 THEN r.item_id END),
       MAX(CASE WHEN r.rn = 13 THEN r.item_id END), MAX(CASE WHEN r.rn = 14 THEN r.item_id END),
       MAX(CASE WHEN r.rn = 15 THEN r.item_id END), MAX(CASE WHEN r.rn = 16 THEN r.item_id END),
       MAX(CASE WHEN r.rn = 1  THEN r.quantity END), MAX(CASE WHEN r.rn = 2  THEN r.quantity END),
       MAX(CASE WHEN r.rn = 3  THEN r.quantity END), MAX(CASE WHEN r.rn = 4  THEN r.quantity END),
       MAX(CASE WHEN r.rn = 5  THEN r.quantity END), MAX(CASE WHEN r.rn = 6  THEN r.quantity END),
       MAX(CASE WHEN r.rn = 7  THEN r.quantity END), MAX(CASE WHEN r.rn = 8  THEN r.quantity END),
       MAX(CASE WHEN r.rn = 9  THEN r.quantity END), MAX(CASE WHEN r.rn = 10 THEN r.quantity END),
       MAX(CASE WHEN r.rn = 11 THEN r.quantity END), MAX(CASE WHEN r.rn = 12 THEN r.quantity END),
       MAX(CASE WHEN r.rn = 13 THEN r.quantity END), MAX(CASE WHEN r.rn = 14 THEN r.quantity END),
       MAX(CASE WHEN r.rn = 15 THEN r.quantity END), MAX(CASE WHEN r.rn = 16 THEN r.quantity END)
FROM loot_instance l
JOIN sniff s ON s.id = l.sniff_id
LEFT JOIN acore_world.sniff_reject bad ON bad.sniff_id = l.sniff_id AND bad.loot_index = l.loot_index
LEFT JOIN (
    SELECT sniff_id, loot_index, item_id, quantity,
           ROW_NUMBER() OVER (PARTITION BY sniff_id, loot_index ORDER BY item_id, slot) rn
    FROM loot_instance_item
) r ON r.sniff_id = l.sniff_id AND r.loot_index = l.loot_index
WHERE bad.sniff_id IS NULL
  AND (l.owner_type NOT IN ('Player', 'ActivePlayer') OR l.owner_type IS NULL)
GROUP BY l.id, s.branch, s.client_build, s.file_hash, l.owner_type, l.owner_entry, l.owner_level,
         l.map, l.coins, l.item_count;

-- -------------------------------------------------------------------------------------------
-- Phase 4: the same rows with names in them, for reading rather than grouping. A stack bigger
-- than one is written onto the name - "Linen Cloth x3" - because a separate quantity column per
-- item would double the width of a table whose whole point is that you can read across it. The
-- raw numbers are in sniff_loot_set.q1..q16 and in sniff_loot_item.
-- -------------------------------------------------------------------------------------------
DROP VIEW IF EXISTS acore_world.sniff_loot;
CREATE VIEW acore_world.sniff_loot AS
SELECT s.loot_id, s.branch, s.owner_type, s.owner_entry,
       COALESCE(o.name, CONCAT('#', s.owner_entry)) AS owner_name,
       s.owner_level, s.map, s.coins, s.items,
       CONCAT(n1.name, IF(s.q1 > 1, CONCAT(' x', s.q1), '')) AS i1,
       CONCAT(n2.name, IF(s.q2 > 1, CONCAT(' x', s.q2), '')) AS i2,
       CONCAT(n3.name, IF(s.q3 > 1, CONCAT(' x', s.q3), '')) AS i3,
       CONCAT(n4.name, IF(s.q4 > 1, CONCAT(' x', s.q4), '')) AS i4,
       CONCAT(n5.name, IF(s.q5 > 1, CONCAT(' x', s.q5), '')) AS i5,
       CONCAT(n6.name, IF(s.q6 > 1, CONCAT(' x', s.q6), '')) AS i6,
       CONCAT(n7.name, IF(s.q7 > 1, CONCAT(' x', s.q7), '')) AS i7,
       CONCAT(n8.name, IF(s.q8 > 1, CONCAT(' x', s.q8), '')) AS i8,
       CONCAT(n9.name, IF(s.q9 > 1, CONCAT(' x', s.q9), '')) AS i9,
       CONCAT(n10.name, IF(s.q10 > 1, CONCAT(' x', s.q10), '')) AS i10,
       CONCAT(n11.name, IF(s.q11 > 1, CONCAT(' x', s.q11), '')) AS i11,
       CONCAT(n12.name, IF(s.q12 > 1, CONCAT(' x', s.q12), '')) AS i12,
       CONCAT(n13.name, IF(s.q13 > 1, CONCAT(' x', s.q13), '')) AS i13,
       CONCAT(n14.name, IF(s.q14 > 1, CONCAT(' x', s.q14), '')) AS i14,
       CONCAT(n15.name, IF(s.q15 > 1, CONCAT(' x', s.q15), '')) AS i15,
       CONCAT(n16.name, IF(s.q16 > 1, CONCAT(' x', s.q16), '')) AS i16,
       s.sniff_hash
FROM acore_world.sniff_loot_set s
LEFT JOIN acore_world.sniff_names o   ON o.object_type  = s.owner_type AND o.id = s.owner_entry
LEFT JOIN acore_world.sniff_names n1  ON n1.object_type = 'Item' AND n1.id = s.i1
LEFT JOIN acore_world.sniff_names n2  ON n2.object_type = 'Item' AND n2.id = s.i2
LEFT JOIN acore_world.sniff_names n3  ON n3.object_type = 'Item' AND n3.id = s.i3
LEFT JOIN acore_world.sniff_names n4  ON n4.object_type = 'Item' AND n4.id = s.i4
LEFT JOIN acore_world.sniff_names n5  ON n5.object_type = 'Item' AND n5.id = s.i5
LEFT JOIN acore_world.sniff_names n6  ON n6.object_type = 'Item' AND n6.id = s.i6
LEFT JOIN acore_world.sniff_names n7  ON n7.object_type = 'Item' AND n7.id = s.i7
LEFT JOIN acore_world.sniff_names n8  ON n8.object_type = 'Item' AND n8.id = s.i8
LEFT JOIN acore_world.sniff_names n9  ON n9.object_type = 'Item' AND n9.id = s.i9
LEFT JOIN acore_world.sniff_names n10 ON n10.object_type = 'Item' AND n10.id = s.i10
LEFT JOIN acore_world.sniff_names n11 ON n11.object_type = 'Item' AND n11.id = s.i11
LEFT JOIN acore_world.sniff_names n12 ON n12.object_type = 'Item' AND n12.id = s.i12
LEFT JOIN acore_world.sniff_names n13 ON n13.object_type = 'Item' AND n13.id = s.i13
LEFT JOIN acore_world.sniff_names n14 ON n14.object_type = 'Item' AND n14.id = s.i14
LEFT JOIN acore_world.sniff_names n15 ON n15.object_type = 'Item' AND n15.id = s.i15
LEFT JOIN acore_world.sniff_names n16 ON n16.object_type = 'Item' AND n16.id = s.i16;

-- -------------------------------------------------------------------------------------------
-- Phase 5: the long form, with quantity and the packet's own slot. The wide table drops both so
-- that identical drop sets stay identical; anything that needs them reads this.
-- -------------------------------------------------------------------------------------------
DROP TABLE IF EXISTS acore_world.sniff_loot_item;
CREATE TABLE acore_world.sniff_loot_item (
  loot_id  BIGINT UNSIGNED NOT NULL,
  slot     INT NOT NULL,
  item_id  INT UNSIGNED NOT NULL,
  quantity INT UNSIGNED NOT NULL,
  PRIMARY KEY (loot_id, slot, item_id),
  KEY ix_item (item_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO acore_world.sniff_loot_item
SELECT l.id, i.slot, i.item_id, i.quantity
FROM loot_instance l
JOIN acore_world.sniff_loot_set p ON p.loot_id = l.id
JOIN loot_instance_item i ON i.sniff_id = l.sniff_id AND i.loot_index = l.loot_index;

-- Phase 6: gameobject spawns, in two shapes.
--
-- Grouped on the position rounded to a millimetre. A gameobject never moves, so every capture of
-- one reads the same server-side row; anything differing past three decimals is float
-- serialisation, not a second spawn.
--
--   sniff_gameobject_spawn  one row per position AND entry - the import-shaped one. Carries orientation
--                 and the rotation quaternion, which AzerothCore's `gameobject` table wants and
--                 which the first cut of this dropped on the floor.
--   sniff_gameobject_point  one row per POSITION, whatever stood on it. Its reason to exist is that one
--                 spawn point often hosts several different gameobjects - herb and mining nodes,
--                 chests, the same slot reused - and that is a pool, which cannot be seen from a
--                 per-entry table. e1..e8 are the entries seen there, commonest first, with the
--                 share of observations each took.
--
-- Eight columns because eight is where the data stops, not where the table does. 105,510 of the
-- 105,526 positions hold eight entries or fewer, and the real eights are real pools: Charred
-- Wreckage across 8 entries over 35 sniffs, Un'Goro's power crystals as 4 colours x 2 entries
-- over 33. Exactly 16 positions go higher, none seen by more than 7 sniffs, and every one of
-- them is a firework launch point - Stormwind's show fires 22 different rockets out of one
-- coordinate. That is the same transient-reuse effect rot_variants flags, not pool membership.
--
-- Rotation is the COMMONEST quaternion captured, never an average - a component-wise mean of
-- four rotations is not a rotation. `rot_variants` says how many distinct ones were seen at that
-- spot: 1 for 131,944 of the 132,101 spawns, because every capture of a fixed gameobject reads
-- the same server-side row. The 157 that disagree are all transient - Blaze, Noblegarden eggs,
-- summoner visuals - genuinely different objects landing on one coordinate, not a grouping
-- error. Treat rot_variants > 1 as "this spot is reused", not as a rotation worth importing.
--
-- `pct` is the share of observations, NOT a spawn chance. One sniffer parked next to a node for
-- an hour weights it. Read `obs` and `sniffs` beside it: a split measured across many sniffs is
-- worth something, a split from a single sniff is worth nothing.
--
-- Branch counts are split because the corpus is not one game. A point with tbc_sniffs and no
-- wotlk_sniffs is evidence about The Burning Crusade and must not be spawned on a WotLK realm.
-- -------------------------------------------------------------------------------------------
DROP TABLE IF EXISTS acore_world.sniff_gameobject_spawn;
CREATE TABLE acore_world.sniff_gameobject_spawn (
  entry INT UNSIGNED NOT NULL,
  map   INT UNSIGNED NOT NULL,
  x DOUBLE NOT NULL, y DOUBLE NOT NULL, z DOUBLE NOT NULL,
  o DOUBLE NULL,
  rot0 DOUBLE NULL, rot1 DOUBLE NULL, rot2 DOUBLE NULL, rot3 DOUBLE NULL,
  rot_variants   INT UNSIGNED NOT NULL,
  obs            BIGINT UNSIGNED NOT NULL,
  sniffs         BIGINT UNSIGNED NOT NULL,
  wotlk_sniffs   BIGINT UNSIGNED NOT NULL,
  tbc_sniffs     BIGINT UNSIGNED NOT NULL,
  classic_sniffs BIGINT UNSIGNED NOT NULL,
  first_build INT NULL,
  last_build  INT NULL,
  first_seen_utc DATETIME(3) NULL,
  PRIMARY KEY (entry, map, x, y, z),
  KEY ix_pos (map, x, y)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO acore_world.sniff_gameobject_spawn
SELECT p.entry, p.map, p.x, p.y, p.z,
       r.o, r.rot0, r.rot1, r.rot2, r.rot3, p.rot_variants,
       p.obs, p.sniffs, p.wotlk_sniffs, p.tbc_sniffs, p.classic_sniffs,
       p.first_build, p.last_build, p.first_seen_utc
FROM (
    SELECT g.entry, g.map,
           ROUND(g.position_x, 3) x, ROUND(g.position_y, 3) y, ROUND(g.position_z, 3) z,
           COUNT(*) obs, COUNT(DISTINCT g.sniff_id) sniffs,
           COUNT(DISTINCT CASE WHEN s.branch = 'WotLK'   THEN g.sniff_id END) wotlk_sniffs,
           COUNT(DISTINCT CASE WHEN s.branch = 'TBC'     THEN g.sniff_id END) tbc_sniffs,
           COUNT(DISTINCT CASE WHEN s.branch = 'Classic' THEN g.sniff_id END) classic_sniffs,
           MIN(s.client_build) first_build, MAX(s.client_build) last_build,
           MIN(g.first_seen_utc) first_seen_utc,
           COUNT(DISTINCT ROUND(g.rotation0, 4), ROUND(g.rotation1, 4),
                          ROUND(g.rotation2, 4), ROUND(g.rotation3, 4)) rot_variants
    FROM gameobject_spawn g
    JOIN sniff s ON s.id = g.sniff_id
    GROUP BY g.entry, g.map, x, y, z
) p
JOIN (
    SELECT entry, map, x, y, z, o, rot0, rot1, rot2, rot3,
           ROW_NUMBER() OVER (PARTITION BY entry, map, x, y, z ORDER BY seen DESC, rot0) rn
    FROM (
        SELECT entry, map,
               ROUND(position_x, 3) x, ROUND(position_y, 3) y, ROUND(position_z, 3) z,
               ROUND(rotation0, 4) rot0, ROUND(rotation1, 4) rot1,
               ROUND(rotation2, 4) rot2, ROUND(rotation3, 4) rot3,
               MIN(orientation) o, COUNT(*) seen
        FROM gameobject_spawn
        GROUP BY entry, map, x, y, z, rot0, rot1, rot2, rot3
    ) v
) r ON r.entry = p.entry AND r.map = p.map AND r.x = p.x AND r.y = p.y AND r.z = p.z
   AND r.rn = 1;

-- -------------------------------------------------------------------------------------------
-- The pool view. This was a 105,526 row table until 2026-08-28; it is now a view over
-- sniff_gameobject_spawn plus a 15,960 row correction, which is 617 KiB where the table was
-- 14.1 MiB. Nothing about its shape changed - same name, same columns, same values.
--
-- Everything about a position except its CAPTURE COUNTS is a GROUP BY over
-- sniff_gameobject_spawn, and that was verified row for row before the table was removed: `obs`,
-- `entries`, `first_build` and `last_build` reproduce exactly on all 105,526 positions, and
-- `e1`..`e8` / `pct1`..`pct8` are a rank over the same rows.
--
-- `sniffs` is the one thing that does not reproduce. A capture that saw a book AND a candle on
-- one Dalaran shelf is ONE sniff of that position but TWO rows in sniff_gameobject_spawn, so
-- SUM(sniffs) counts it twice. The error lands exactly where this view earns its keep: of the
-- 15,960 positions holding more than one entry, 6,129 come out wrong that way - 124% over on
-- average, 2,100% over at worst. A single entry position cannot double count and needs no
-- correction, which is why the table below holds only the multi entry ones and the view falls
-- back to the plain SUM everywhere else.
--
-- So do not "simplify" this into a bare GROUP BY. The 85% it would get right are the positions
-- nobody needs a pool view for.
-- -------------------------------------------------------------------------------------------
DROP VIEW  IF EXISTS acore_world.sniff_gameobject_point;
DROP TABLE IF EXISTS acore_world.sniff_gameobject_point;
DROP TABLE IF EXISTS acore_world.sniff_gameobject_point_sniffs;

CREATE TABLE acore_world.sniff_gameobject_point_sniffs (
  map   INT UNSIGNED NOT NULL,
  x DOUBLE NOT NULL, y DOUBLE NOT NULL, z DOUBLE NOT NULL,
  sniffs         BIGINT UNSIGNED NOT NULL,
  wotlk_sniffs   BIGINT UNSIGNED NOT NULL,
  tbc_sniffs     BIGINT UNSIGNED NOT NULL,
  classic_sniffs BIGINT UNSIGNED NOT NULL,
  PRIMARY KEY (map, x, y, z)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Counted from the ingest rows, not from sniff_gameobject_spawn - counting DISTINCT sniff_id
-- once per POSITION is the whole point, and no per-entry table can express it.
INSERT INTO acore_world.sniff_gameobject_point_sniffs
SELECT g.map,
       ROUND(g.position_x, 3), ROUND(g.position_y, 3), ROUND(g.position_z, 3),
       COUNT(DISTINCT g.sniff_id),
       COUNT(DISTINCT CASE WHEN s.branch = 'WotLK'   THEN g.sniff_id END),
       COUNT(DISTINCT CASE WHEN s.branch = 'TBC'     THEN g.sniff_id END),
       COUNT(DISTINCT CASE WHEN s.branch = 'Classic' THEN g.sniff_id END)
FROM gameobject_spawn g
JOIN sniff s ON s.id = g.sniff_id
GROUP BY g.map, ROUND(g.position_x, 3), ROUND(g.position_y, 3), ROUND(g.position_z, 3)
HAVING COUNT(DISTINCT g.entry) > 1;

CREATE VIEW acore_world.sniff_gameobject_point AS
SELECT r.map, r.x, r.y, r.z,
       SUM(r.obs) AS obs,
       COALESCE(c.sniffs,         SUM(r.sniffs))         AS sniffs,
       COALESCE(c.wotlk_sniffs,   SUM(r.wotlk_sniffs))   AS wotlk_sniffs,
       COALESCE(c.tbc_sniffs,     SUM(r.tbc_sniffs))     AS tbc_sniffs,
       COALESCE(c.classic_sniffs, SUM(r.classic_sniffs)) AS classic_sniffs,
       MIN(r.first_build) AS first_build,
       MAX(r.last_build)  AS last_build,
       COUNT(*)           AS entries,
       MAX(CASE WHEN r.rn = 1 THEN r.entry END) AS e1,  MAX(CASE WHEN r.rn = 1 THEN r.pct END) AS pct1,
       MAX(CASE WHEN r.rn = 2 THEN r.entry END) AS e2,  MAX(CASE WHEN r.rn = 2 THEN r.pct END) AS pct2,
       MAX(CASE WHEN r.rn = 3 THEN r.entry END) AS e3,  MAX(CASE WHEN r.rn = 3 THEN r.pct END) AS pct3,
       MAX(CASE WHEN r.rn = 4 THEN r.entry END) AS e4,  MAX(CASE WHEN r.rn = 4 THEN r.pct END) AS pct4,
       MAX(CASE WHEN r.rn = 5 THEN r.entry END) AS e5,  MAX(CASE WHEN r.rn = 5 THEN r.pct END) AS pct5,
       MAX(CASE WHEN r.rn = 6 THEN r.entry END) AS e6,  MAX(CASE WHEN r.rn = 6 THEN r.pct END) AS pct6,
       MAX(CASE WHEN r.rn = 7 THEN r.entry END) AS e7,  MAX(CASE WHEN r.rn = 7 THEN r.pct END) AS pct7,
       MAX(CASE WHEN r.rn = 8 THEN r.entry END) AS e8,  MAX(CASE WHEN r.rn = 8 THEN r.pct END) AS pct8
FROM (
    SELECT map, x, y, z, entry, obs, sniffs, wotlk_sniffs, tbc_sniffs, classic_sniffs,
           first_build, last_build,
           ROUND(100.0 * obs / SUM(obs) OVER (PARTITION BY map, x, y, z), 2) AS pct,
           ROW_NUMBER() OVER (PARTITION BY map, x, y, z ORDER BY obs DESC, entry) AS rn
    FROM acore_world.sniff_gameobject_spawn
) r
LEFT JOIN acore_world.sniff_gameobject_point_sniffs c
       ON c.map = r.map AND c.x = r.x AND c.y = r.y AND c.z = r.z
GROUP BY r.map, r.x, r.y, r.z,
         c.sniffs, c.wotlk_sniffs, c.tbc_sniffs, c.classic_sniffs;

-- -------------------------------------------------------------------------------------------
-- Sanity. Read these; they are the only thing between a mistake here and a published mistake.
-- -------------------------------------------------------------------------------------------
SELECT 'character guids left in loot_instance' AS check_name, COUNT(*) AS must_be_zero
FROM loot_instance WHERE owner_type IN ('Player', 'ActivePlayer') AND owner_guid IS NOT NULL;

SELECT 'player-owned rows published' AS check_name, COUNT(*) AS must_be_zero
FROM acore_world.sniff_loot_set WHERE owner_type IN ('Player', 'ActivePlayer');

SELECT 'loot with more items than columns' AS check_name, COUNT(*) AS must_be_zero
FROM loot_instance WHERE item_count > 16;

SELECT 'items lost by the pivot' AS check_name,
       (SELECT COUNT(*) FROM acore_world.sniff_loot_item) - (
         SELECT SUM((i1 IS NOT NULL) + (i2 IS NOT NULL) + (i3 IS NOT NULL) + (i4 IS NOT NULL)
                  + (i5 IS NOT NULL) + (i6 IS NOT NULL) + (i7 IS NOT NULL) + (i8 IS NOT NULL)
                  + (i9 IS NOT NULL) + (i10 IS NOT NULL) + (i11 IS NOT NULL) + (i12 IS NOT NULL)
                  + (i13 IS NOT NULL) + (i14 IS NOT NULL) + (i15 IS NOT NULL) + (i16 IS NOT NULL))
         FROM acore_world.sniff_loot_set) AS must_be_zero;

SELECT 'quantities lost by the pivot' AS check_name,
       (SELECT SUM(quantity) FROM acore_world.sniff_loot_item) - (
         SELECT SUM(COALESCE(q1,0) + COALESCE(q2,0) + COALESCE(q3,0) + COALESCE(q4,0)
                  + COALESCE(q5,0) + COALESCE(q6,0) + COALESCE(q7,0) + COALESCE(q8,0)
                  + COALESCE(q9,0) + COALESCE(q10,0) + COALESCE(q11,0) + COALESCE(q12,0)
                  + COALESCE(q13,0) + COALESCE(q14,0) + COALESCE(q15,0) + COALESCE(q16,0))
         FROM acore_world.sniff_loot_set) AS must_be_zero;

-- Every gameobject observation must land in exactly one sniff_gameobject_spawn row.
SELECT 'gameobject observations lost' AS check_name,
       (SELECT COUNT(*) FROM gameobject_spawn)
     - (SELECT SUM(obs) FROM acore_world.sniff_gameobject_spawn) AS must_be_zero;

-- Informational, not a failure: spawns whose rotation was not the same every time. All known
-- cases are transient objects reusing a coordinate. A sudden jump here means something else.
SELECT 'spawns with more than one rotation' AS check_name, COUNT(*) AS flagged_rot_variants
FROM acore_world.sniff_gameobject_spawn WHERE rot_variants > 1;

-- e1..e8 truncate anything busier. Expect 16, all firework launch points. A jump here means
-- either a new ingest found a bigger pool or something transient is being read as one.
SELECT 'points with more than 8 entries' AS check_name, COUNT(*) AS truncated_in_gameobject_point
FROM acore_world.sniff_gameobject_point WHERE entries > 8;

SELECT 'gameobject rows' AS check_name,
       (SELECT COUNT(*) FROM acore_world.sniff_gameobject_spawn) AS per_entry_and_position,
       (SELECT COUNT(*) FROM acore_world.sniff_gameobject_point) AS positions;

SELECT 'rejected' AS check_name, reason, COUNT(*) AS n FROM acore_world.sniff_reject GROUP BY reason;

SELECT 'rows' AS check_name,
       (SELECT COUNT(*) FROM acore_world.sniff_loot_set) AS loot,
       (SELECT COUNT(*) FROM acore_world.sniff_loot_item) AS loot_items,
       (SELECT COUNT(*) FROM acore_world.sniff_names) AS names,
       (SELECT COUNT(*) FROM acore_world.sniff_gameobject_point) AS go_points;

SELECT 'loot by branch' AS check_name, branch, COUNT(*) AS n FROM acore_world.sniff_loot_set GROUP BY branch;
