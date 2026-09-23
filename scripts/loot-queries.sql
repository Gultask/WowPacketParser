-- ===========================================================================================
-- loot-queries.sql - the lookups worth having against the published loot tables.
--
-- Run publish-loot.sql first. Nothing here writes anything.
--
-- Every query filters `branch = 'WotLK'`. The corpus also holds TBC and Classic sniffs, and a
-- TBC drop table is not a WotLK drop table. Take the filter out only on purpose.
--
-- What the data can and cannot tell you:
--   It CAN tell you which items were seen together in one loot. That is the whole basis for
--   working out loot groups, because a group drops at most one of its members - so two items
--   seen together are in different groups, and two items never seen together across many
--   observations are probably in the same one.
--   It CANNOT tell you a drop chance from a small sample, and it cannot see an item that never
--   dropped in front of a sniffer at all.
-- ===========================================================================================

USE acore_world;

-- -------------------------------------------------------------------------------------------
-- 1. The biggest loots seen, readable.
--
-- The most items ever seen in one loot is the floor on how many groups that creature has: a
-- group yields at most one item, so seven items at once means at least seven groups.
-- -------------------------------------------------------------------------------------------
SELECT owner_name, owner_entry, items, coins, map,
       i1, i2, i3, i4, i5, i6, i7, i8, i9, i10, i11, i12, i13
FROM sniff_loot
WHERE branch = 'WotLK'
ORDER BY items DESC, owner_entry
LIMIT 50;

-- -------------------------------------------------------------------------------------------
-- 2. Every creature by how much it can drop at once - the group-count floor, per owner.
--
-- `observations` is how many times a sniffer opened its loot. Read `max_items` next to it: a
-- max of 4 over 300 observations is a real four-group table, a max of 4 over 3 observations is
-- three lucky kills and says almost nothing.
-- -------------------------------------------------------------------------------------------
SELECT s.owner_type, s.owner_entry,
       COALESCE(n.name, CONCAT('#', s.owner_entry)) AS owner_name,
       COUNT(*)             AS observations,
       MAX(s.items)         AS max_items,
       ROUND(AVG(s.items), 2) AS avg_items,
       SUM(s.items = 0)     AS empty_loots,
       COUNT(DISTINCT s.map) AS maps
FROM sniff_loot_set s
LEFT JOIN sniff_names n ON n.object_type = s.owner_type AND n.id = s.owner_entry
WHERE s.branch = 'WotLK'
GROUP BY s.owner_type, s.owner_entry, owner_name
HAVING observations >= 20
ORDER BY max_items DESC, observations DESC
LIMIT 100;

-- -------------------------------------------------------------------------------------------
-- 3. One creature's drop patterns, most common first.
--
-- Because the wide columns are sorted by item id, an identical drop set is an identical row, so
-- this counts patterns directly. Set @entry and read down: the patterns ARE the loot table.
-- -------------------------------------------------------------------------------------------
SET @entry := 448;   -- Hogger

SELECT COUNT(*) AS seen, items,
       COALESCE(n1.name, '') AS i1, COALESCE(n2.name, '') AS i2, COALESCE(n3.name, '') AS i3,
       COALESCE(n4.name, '') AS i4, COALESCE(n5.name, '') AS i5, COALESCE(n6.name, '') AS i6
FROM sniff_loot_set s
LEFT JOIN sniff_names n1 ON n1.object_type = 'Item' AND n1.id = s.i1
LEFT JOIN sniff_names n2 ON n2.object_type = 'Item' AND n2.id = s.i2
LEFT JOIN sniff_names n3 ON n3.object_type = 'Item' AND n3.id = s.i3
LEFT JOIN sniff_names n4 ON n4.object_type = 'Item' AND n4.id = s.i4
LEFT JOIN sniff_names n5 ON n5.object_type = 'Item' AND n5.id = s.i5
LEFT JOIN sniff_names n6 ON n6.object_type = 'Item' AND n6.id = s.i6
WHERE s.branch = 'WotLK' AND s.owner_entry = @entry
GROUP BY s.items, s.i1, s.i2, s.i3, s.i4, s.i5, s.i6,
         n1.name, n2.name, n3.name, n4.name, n5.name, n6.name
ORDER BY seen DESC;

-- -------------------------------------------------------------------------------------------
-- 4. One creature's items, by how often each dropped.
--
-- `pct` is a drop rate only if `loots` is large. It is the same number either way, which is why
-- the denominator is printed next to it.
-- -------------------------------------------------------------------------------------------
SELECT COALESCE(n.name, CONCAT('#', li.item_id)) AS item, li.item_id,
       COUNT(DISTINCT li.loot_id) AS dropped_in,
       (SELECT COUNT(*) FROM sniff_loot_set WHERE branch = 'WotLK' AND owner_entry = @entry) AS loots,
       ROUND(100.0 * COUNT(DISTINCT li.loot_id) /
             NULLIF((SELECT COUNT(*) FROM sniff_loot_set
                     WHERE branch = 'WotLK' AND owner_entry = @entry), 0), 2) AS pct,
       MIN(li.quantity) AS min_stack, MAX(li.quantity) AS max_stack
FROM sniff_loot_item li
JOIN sniff_loot_set s ON s.loot_id = li.loot_id
LEFT JOIN sniff_names n ON n.object_type = 'Item' AND n.id = li.item_id
WHERE s.branch = 'WotLK' AND s.owner_entry = @entry
GROUP BY li.item_id, item
ORDER BY dropped_in DESC;

-- -------------------------------------------------------------------------------------------
-- 5. Which of one creature's items were never seen together.
--
-- This is the group inference, made explicit. A loot group yields at most one of its members,
-- so a pair that co-occurs is definitely in DIFFERENT groups, and a pair that never co-occurs
-- across many chances is probably in the SAME one.
--
-- `both_chances` is how many loots contained either item - the number of opportunities the pair
-- had to appear together. A zero co-occurrence over 4 chances is noise; over 200 it is a group.
-- -------------------------------------------------------------------------------------------
SELECT COALESCE(na.name, CONCAT('#', a.item_id)) AS item_a,
       COALESCE(nb.name, CONCAT('#', b.item_id)) AS item_b,
       COUNT(DISTINCT CASE WHEN a.loot_id = b.loot_id THEN a.loot_id END) AS together,
       (SELECT COUNT(DISTINCT li.loot_id) FROM sniff_loot_item li
        JOIN sniff_loot_set s2 ON s2.loot_id = li.loot_id
        WHERE s2.branch = 'WotLK' AND s2.owner_entry = @entry
          AND li.item_id IN (a.item_id, b.item_id)) AS both_chances
FROM sniff_loot_item a
JOIN sniff_loot_set sa ON sa.loot_id = a.loot_id
JOIN sniff_loot_item b ON b.item_id > a.item_id
JOIN sniff_loot_set sb ON sb.loot_id = b.loot_id
WHERE sa.branch = 'WotLK' AND sa.owner_entry = @entry
  AND sb.branch = 'WotLK' AND sb.owner_entry = @entry
GROUP BY a.item_id, b.item_id, item_a, item_b
HAVING together = 0
ORDER BY both_chances DESC
LIMIT 50;

-- -------------------------------------------------------------------------------------------
-- 6. Gameobject spawns AzerothCore has no row for, WotLK evidence only.
--
-- `sniff_gameobject_spawn` is the import-shaped table: one row per entry and position, with the rotation
-- quaternion AzerothCore's `gameobject` table wants, under AzerothCore's own column names.
-- `wotlk_sniffs` is the number of independent WotLK captures that saw it - one is one person
-- standing somewhere, five is a spawn.
-- -------------------------------------------------------------------------------------------
SELECT g.id, COALESCE(n.name, CONCAT('#', g.id)) AS name,
       g.map, ROUND(g.position_x, 2) AS position_x, ROUND(g.position_y, 2) AS position_y,
       ROUND(g.position_z, 2) AS position_z,
       ROUND(g.orientation, 4) AS orientation,
       g.rotation0, g.rotation1, g.rotation2, g.rotation3,
       g.obs, g.wotlk_sniffs, g.VerifiedBuild
FROM sniff_gameobject_spawn g
LEFT JOIN sniff_names n ON n.object_type = 'GameObject' AND n.id = g.id
WHERE g.wotlk_sniffs >= 2
  AND g.rot_variants = 1
  AND NOT EXISTS (
      SELECT 1 FROM acore_world.gameobject ac
      WHERE ac.id = g.id AND ac.map = g.map
        AND POW(ac.position_x - g.position_x, 2) + POW(ac.position_y - g.position_y, 2)
          + POW(ac.position_z - g.position_z, 2) < 25)
ORDER BY g.wotlk_sniffs DESC, g.id
LIMIT 100;

-- -------------------------------------------------------------------------------------------
-- 7. Spawn points shared by several gameobjects - pools, read straight off the data.
--
-- One position, several entries, none of them ever there at the same time: that is a pool with
-- one slot. `pct` is each entry's share of the observations at that point, which approximates
-- its weight in the pool. Dalaran's bookshelves come out at four books near 25% each; a
-- Northrend mining node comes out Saronite 81 / Titanium 10 / Rich Saronite 9.
--
-- Read `sniffs` before believing a percentage. A three-way split seen by 200 sniffers is a
-- pool; the same split seen by two is two people walking past.
-- -------------------------------------------------------------------------------------------
SELECT p.map, ROUND(p.x, 2) AS x, ROUND(p.y, 2) AS y, ROUND(p.z, 2) AS z,
       p.entries, p.obs, p.sniffs, p.wotlk_sniffs,
       COALESCE(n1.name, CONCAT('#', p.e1)) AS e1, p.pct1,
       COALESCE(n2.name, CONCAT('#', p.e2)) AS e2, p.pct2,
       COALESCE(n3.name, CONCAT('#', p.e3)) AS e3, p.pct3,
       COALESCE(n4.name, CONCAT('#', p.e4)) AS e4, p.pct4,
       COALESCE(n5.name, CONCAT('#', p.e5)) AS e5, p.pct5,
       COALESCE(n6.name, CONCAT('#', p.e6)) AS e6, p.pct6,
       COALESCE(n7.name, CONCAT('#', p.e7)) AS e7, p.pct7,
       COALESCE(n8.name, CONCAT('#', p.e8)) AS e8, p.pct8
FROM sniff_gameobject_point p
LEFT JOIN sniff_names n1 ON n1.object_type = 'GameObject' AND n1.id = p.e1
LEFT JOIN sniff_names n2 ON n2.object_type = 'GameObject' AND n2.id = p.e2
LEFT JOIN sniff_names n3 ON n3.object_type = 'GameObject' AND n3.id = p.e3
LEFT JOIN sniff_names n4 ON n4.object_type = 'GameObject' AND n4.id = p.e4
LEFT JOIN sniff_names n5 ON n5.object_type = 'GameObject' AND n5.id = p.e5
LEFT JOIN sniff_names n6 ON n6.object_type = 'GameObject' AND n6.id = p.e6
LEFT JOIN sniff_names n7 ON n7.object_type = 'GameObject' AND n7.id = p.e7
LEFT JOIN sniff_names n8 ON n8.object_type = 'GameObject' AND n8.id = p.e8
WHERE p.entries >= 2 AND p.wotlk_sniffs >= 5
ORDER BY p.sniffs DESC
LIMIT 100;

-- -------------------------------------------------------------------------------------------
-- 8. Every entry that ever shared a point with a given one - the pool's full membership.
--
-- e1..e8 truncate at eight, which only 28 positions in the whole corpus exceed - firework
-- launch points, Stratholme supply crates and one banner aura, none of them pools. This reaches
-- past the columns anyway by going back to sniff_gameobject_spawn, which keeps every entry.
-- -------------------------------------------------------------------------------------------
SET @go := 189978;   -- Cobalt Deposit

SELECT COALESCE(n.name, CONCAT('#', b.id)) AS shares_with, b.id,
       COUNT(*) AS shared_points, SUM(b.obs) AS obs
FROM sniff_gameobject_spawn a
JOIN sniff_gameobject_spawn b ON b.map = a.map AND b.position_x = a.position_x
                             AND b.position_y = a.position_y AND b.position_z = a.position_z
                             AND b.id <> a.id
LEFT JOIN sniff_names n ON n.object_type = 'GameObject' AND n.id = b.id
WHERE a.id = @go AND a.wotlk_sniffs >= 1
GROUP BY b.id, shares_with
ORDER BY shared_points DESC
LIMIT 30;
