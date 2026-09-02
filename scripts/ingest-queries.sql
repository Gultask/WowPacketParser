-- Ready made queries against the ingest database (default: wpp_ingest).
-- Everything is stored unfiltered; deciding what counts for a given core happens here.

-- ---------------------------------------------------------------------------
-- 1. What is in the pile
-- ---------------------------------------------------------------------------
SELECT branch, client_version, COUNT(*) sniffs,
       MIN(first_packet_utc) earliest, MAX(last_packet_utc) latest,
       SUM(packet_count) packets
FROM sniff
GROUP BY branch, client_version
ORDER BY branch, client_version;


-- ---------------------------------------------------------------------------
-- 2. Sniffs whose two clocks disagree
-- A few seconds is normal. Anything near a whole hour means that sniffer wrote local
-- time where UTC was expected, so its timestamps cannot be lined up with anyone else's.
-- ---------------------------------------------------------------------------
SELECT id, file_name, branch, client_version,
       clock_skew_seconds, ROUND(clock_skew_seconds / 3600, 2) AS skew_hours,
       utc_offset_seconds, utc_offset_source
FROM sniff
WHERE ABS(COALESCE(clock_skew_seconds, 0)) > 1800
ORDER BY ABS(clock_skew_seconds) DESC;


-- ---------------------------------------------------------------------------
-- 3. Gathering node odds: what a spawn point rolls, and how often
--
-- Counts distinct guids, not create packets - a node keeps its guid while it stands and
-- gets a new one when it respawns, so this measures respawns rather than how many times
-- the player walked back into range.
--
-- Positions are byte identical between respawns and between sniffs, so rounding to one
-- decimal is only insurance.
-- ---------------------------------------------------------------------------
SELECT   point.map,
         point.x, point.y,
         point.entry,
         point.instances,
         ROUND(100 * point.instances / SUM(point.instances) OVER (PARTITION BY point.map, point.x, point.y), 1) AS pct,
         point.pool_size
FROM (
    SELECT   g.map,
             ROUND(g.position_x, 1) AS x,
             ROUND(g.position_y, 1) AS y,
             g.entry,
             COUNT(DISTINCT g.guid) AS instances,
             COUNT(DISTINCT g.entry) OVER (PARTITION BY g.map, ROUND(g.position_x, 1), ROUND(g.position_y, 1)) AS pool_size
    FROM     gameobject_spawn g
    GROUP BY g.map, x, y, g.entry
) point
WHERE point.pool_size > 1          -- only spawn points that roll more than one thing
ORDER BY point.map, point.instances DESC, point.x, point.y;


-- ---------------------------------------------------------------------------
-- 4. The same, but pooled per entry set rather than per point
--
-- Individual nodes rarely have enough respawns to be significant on their own. Grouping
-- every point that shares the same set of possible entries gives one big sample for the
-- whole pool, which is what you actually want the odds of.
-- ---------------------------------------------------------------------------
WITH pools AS (
    SELECT   g.map,
             ROUND(g.position_x, 1) AS x,
             ROUND(g.position_y, 1) AS y,
             GROUP_CONCAT(DISTINCT g.entry ORDER BY g.entry) AS pool
    FROM     gameobject_spawn g
    GROUP BY g.map, x, y
)
SELECT   p.map, p.pool,
         g.entry,
         COUNT(DISTINCT g.guid) AS instances,
         ROUND(100 * COUNT(DISTINCT g.guid) /
               SUM(COUNT(DISTINCT g.guid)) OVER (PARTITION BY p.map, p.pool), 1) AS pct,
         COUNT(DISTINCT CONCAT(p.x, ':', p.y)) AS points
FROM     pools p
JOIN     gameobject_spawn g
      ON g.map = p.map AND ROUND(g.position_x, 1) = p.x AND ROUND(g.position_y, 1) = p.y
WHERE    p.pool LIKE '%,%'         -- more than one entry in the pool
GROUP BY p.map, p.pool, g.entry
ORDER BY p.map, instances DESC;


-- ---------------------------------------------------------------------------
-- 5. Query 4, restricted to sniffs whose terrain matches a 3.3.5 world
--
-- map_validity says which content branches match a target for a given map: Cataclysm
-- reshaped maps 0 and 1, so retail sniffs of those are useless here, while Outland and
-- Northrend are unchanged and stay usable from any later client.
--
-- It only speaks about geometry. A Northrend sniff from a modern client has coordinates
-- you can trust and a creature list you cannot - gathering nodes are far more stable than
-- creatures, but check before leaning on this for spawn work.
--
-- Maps with no row are unconstrained. Add rows as you work out more instances; the
-- seeded set covers the continents plus Scarlet Monastery.
-- ---------------------------------------------------------------------------
SELECT   g.map, g.entry,
         COUNT(DISTINCT g.guid) AS instances,
         GROUP_CONCAT(DISTINCT s.branch ORDER BY s.branch) AS from_branches
FROM     gameobject_spawn g
JOIN     sniff s ON s.id = g.sniff_id
LEFT JOIN map_validity v ON v.map = g.map AND v.target = '3.3.5'
WHERE    v.map IS NULL
      OR FIND_IN_SET(s.branch, v.usable_branches) > 0
GROUP BY g.map, g.entry
ORDER BY instances DESC
LIMIT 50;


-- ---------------------------------------------------------------------------
-- 6. What got thrown away by rule 5, so the filter can be sanity checked
-- ---------------------------------------------------------------------------
SELECT   g.map, v.note, s.branch, COUNT(*) AS rows_excluded
FROM     gameobject_spawn g
JOIN     sniff s ON s.id = g.sniff_id
JOIN     map_validity v ON v.map = g.map AND v.target = '3.3.5'
WHERE    FIND_IN_SET(s.branch, v.usable_branches) = 0
GROUP BY g.map, v.note, s.branch
ORDER BY rows_excluded DESC;

-- ---------------------------------------------------------------------------
-- Coverage ledger: what still needs parsing, and what never will
-- ---------------------------------------------------------------------------

-- Backfill sniff_map for sniffs ingested before the ledger existed. Derived from
-- rows already in the database, so it costs one pass and no re-parsing.
INSERT INTO sniff_map (sniff_id, map, creature_spawns, gameobject_spawns, waypoints, loot_instances)
SELECT sniff_id, map, SUM(c), SUM(g), SUM(w), SUM(l) FROM (
    SELECT sniff_id, map, COUNT(*) c, 0 g, 0 w, 0 l FROM creature_spawn    GROUP BY sniff_id, map
    UNION ALL
    SELECT sniff_id, map, 0, COUNT(*),  0, 0        FROM gameobject_spawn  GROUP BY sniff_id, map
    UNION ALL
    SELECT sniff_id, map, 0, 0, COUNT(*), 0         FROM creature_waypoint GROUP BY sniff_id, map
    UNION ALL
    SELECT sniff_id, map, 0, 0, 0, COUNT(*)         FROM loot_instance
    WHERE map IS NOT NULL                           GROUP BY sniff_id, map
) t GROUP BY sniff_id, map
ON DUPLICATE KEY UPDATE creature_spawns   = VALUES(creature_spawns),
                        gameobject_spawns = VALUES(gameobject_spawns),
                        waypoints         = VALUES(waypoints),
                        loot_instances    = VALUES(loot_instances);

-- The permanent gaps, grouped by build. This is the list that says which builds are
-- worth mapping an opcode for, ordered by how much of the corpus it would unlock.
SELECT s.client_build, s.client_version, c.capability, COUNT(*) sniffs,
       ROUND(SUM(s.file_size)/1073741824, 1) gb
FROM sniff_coverage c JOIN sniff s ON s.id = c.sniff_id
WHERE c.status = 'unsupported'
GROUP BY s.client_build, s.client_version, c.capability
ORDER BY sniffs DESC;

-- The re-parse work list: sniffs whose data predates the current collector. Bump the
-- version in CollectorVersion, rebuild, and this query is the exact set of files to run.
SELECT s.file_name, s.file_size, c.capability, c.collector_version
FROM sniff s
LEFT JOIN sniff_coverage c ON c.sniff_id = s.id AND c.capability = 'creature_spawn'
WHERE c.sniff_id IS NULL OR c.collector_version < 1
ORDER BY s.file_size;

-- Sniffs that are mostly instance content. These are the expensive ones - a handful of
-- raid logs carry a third of the corpus's packets - and skipping them is the cheapest
-- way to shorten a run that only cares about the overworld.
SELECT s.id, s.file_name, ROUND(s.file_size/1048576) mb,
       ROUND(100 * SUM(CASE WHEN it.map IS NOT NULL THEN m.creature_spawns ELSE 0 END)
                 / NULLIF(SUM(m.creature_spawns), 0), 1) pct_instance
FROM sniff_map m
JOIN sniff s ON s.id = m.sniff_id
LEFT JOIN acore_world.instance_template it ON it.map = m.map
GROUP BY s.id, s.file_name, s.file_size
HAVING pct_instance > 50
ORDER BY s.file_size DESC;

-- Writes the exclude list for -ExcludeListFile: sniffs whose creature spawns are mostly on
-- instance maps. Exact rather than guessed from the file name, which matters - a name pattern
-- built from raid words also catches 138 sniffs that are majority overworld, throwing away
-- 209M packets of real data to save 194M of raid.
--   mysql -uroot -proot wpp_ingest -N -B < this-query > G:\skip-raids.txt
SELECT s.file_name
FROM sniff_map m
JOIN sniff s ON s.id = m.sniff_id
LEFT JOIN acore_world.instance_template it ON it.map = m.map
GROUP BY s.id, s.file_name
HAVING SUM(CASE WHEN it.map IS NOT NULL THEN m.creature_spawns ELSE 0 END)
     > 0.5 * NULLIF(SUM(m.creature_spawns), 0);
