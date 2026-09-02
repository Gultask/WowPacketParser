-- Shrink creature_waypoint without losing anything the digest or future path mining needs.
--
-- NOT a drop. A tight wanderer's 46 million points reduce exactly to what creature_movement
-- already stores - a centre and a radius - because its destinations are random and never
-- repeat. A patrol's points are authored, so they can still be confirmed by a sniff captured
-- next year, and must be kept.
--
-- Measured on the 2026-08-20 corpus:
--   60.1% of points come from creatures with no authored spline and a radius under 30 yd
--   29.0% come from patrol candidates (any authored spline, or a radius of 60 yd or more)
--   31.9M of the 46.2M points sit at a position no other sniff ever recorded
--
-- Run the SELECTs first and read them before letting the DELETE go.

SELECT 'before' AS phase, COUNT(*) AS waypoints,
       ROUND((SELECT (data_length+index_length)/1024/1024/1024 FROM information_schema.tables
              WHERE table_schema=DATABASE() AND table_name='creature_waypoint'),2) AS gb
FROM creature_waypoint;

-- What would go: points belonging to a (sniff, guid) that the movement summary already
-- describes completely - no authored spline, radius under 30 yd, and enough observations that
-- the centre and radius have converged (40 destinations, per FINDINGS-random-movement.md).
SELECT COUNT(*) AS deletable_waypoints
FROM creature_waypoint w
JOIN creature_movement m ON m.sniff_id = w.sniff_id AND m.guid = w.guid
WHERE m.multi_point_segments = 0 AND m.radius < 30 AND m.points >= 40;

-- Sniffs with no movement summary are the stale ones from before the coverage ledger existed.
-- Their segment_points is a DDL default rather than measured, so they cannot be judged here
-- and are left alone.
SELECT COUNT(*) AS stale_waypoints_left_alone
FROM creature_waypoint w
WHERE NOT EXISTS (SELECT 1 FROM creature_movement m
                  WHERE m.sniff_id = w.sniff_id AND m.guid = w.guid);

-- DELETE FROM w USING creature_waypoint w
-- JOIN creature_movement m ON m.sniff_id = w.sniff_id AND m.guid = w.guid
-- WHERE m.multi_point_segments = 0 AND m.radius < 30 AND m.points >= 40;
--
-- Then reclaim the file space, which the DELETE alone will not do:
-- ALTER TABLE creature_waypoint ENGINE=InnoDB;
