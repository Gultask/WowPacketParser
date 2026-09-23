-- What entry-targeted and ground-targeted spells actually point at, from the packets.
--
-- Two spell target types in 3.3.5 name something the client is never told and Spell.dbc never
-- stores, because it lives in the server's own tables:
--
--   38  TARGET_SCRIPT              - "the nearby creature with entry X". The entry is in
--                                    spell_script_target on MaNGOS; on AzerothCore it is a
--                                    conditions row, SourceTypeOrReferenceId 13.
--   17  TARGET_TABLE_X_Y_Z_COORDS  - "the point in the table". That is spell_target_position.
--
-- The packets do name both. SMSG_SPELL_GO lists the guids a cast hit, and a guid carries its
-- creature entry; a ground-targeted cast carries its destination. So the corpus can fill in
-- rows a core is missing, and disagree with rows it has.
--
-- The numbering above is MaNGOS's, which is what wotlkmangos.spell_template uses. It is checked
-- rather than assumed: spell 3730 Initialize Image has EffectImplicitTargetA1 = 38, and
-- AzerothCore's conditions row for spell 3730 points at creature 15263. Query 0 re-runs that
-- check, and should be read before trusting anything below it.
--
-- Reads wpp_ingest (spell_target, spell_destination) against wotlkmangos (Spell.dbc as a
-- table) and acore_world (what the core already has).
--
-- BRANCH. Cataclysm and later rewrote spells as freely as they rewrote terrain, so a modern
-- capture of a spell is weaker evidence than a modern capture of a hillside. Every query here
-- filters to WotLK and below. Widening that is a decision, not a default.

-- Temporary tables need a default schema; every other reference is qualified.
USE wpp_ingest;

-- ---------------------------------------------------------------------------
-- 0. Sanity: does target type 38 really mean "entry from the server's table"?
--    Expect a high overlap. A low one means the numbering is wrong and nothing
--    below this line can be believed.
-- ---------------------------------------------------------------------------

SELECT 'spells with DBC target 38'                        AS check_name,
       COUNT(*)                                           AS n
FROM (SELECT DISTINCT id FROM wotlkmangos.spell_template
      WHERE (Effect1<>0 AND (EffectImplicitTargetA1=38 OR EffectImplicitTargetB1=38))
         OR (Effect2<>0 AND (EffectImplicitTargetA2=38 OR EffectImplicitTargetB2=38))
         OR (Effect3<>0 AND (EffectImplicitTargetA3=38 OR EffectImplicitTargetB3=38))) t
UNION ALL
SELECT 'of those, AzerothCore has a conditions row',
       COUNT(DISTINCT c.SourceEntry)
FROM acore_world.conditions c
WHERE c.SourceTypeOrReferenceId = 13
  AND c.SourceEntry IN (SELECT id FROM wotlkmangos.spell_template
                        WHERE (Effect1<>0 AND (EffectImplicitTargetA1=38 OR EffectImplicitTargetB1=38))
                           OR (Effect2<>0 AND (EffectImplicitTargetA2=38 OR EffectImplicitTargetB2=38))
                           OR (Effect3<>0 AND (EffectImplicitTargetA3=38 OR EffectImplicitTargetB3=38)));

-- ---------------------------------------------------------------------------
-- 1. Entry-targeted spells: what the sniffs saw them hit, next to what the core
--    believes. `agreement` is the column to read.
-- ---------------------------------------------------------------------------
-- Not TEMPORARY: MySQL refuses to reference a temporary table twice in one statement, and
-- the UNION in query 3 does exactly that.

DROP TABLE IF EXISTS t38;
CREATE TABLE t38 (spell_id INT UNSIGNED PRIMARY KEY) ENGINE=InnoDB;
INSERT INTO t38
SELECT DISTINCT id FROM wotlkmangos.spell_template
WHERE (Effect1<>0 AND (EffectImplicitTargetA1=38 OR EffectImplicitTargetB1=38))
   OR (Effect2<>0 AND (EffectImplicitTargetA2=38 OR EffectImplicitTargetB2=38))
   OR (Effect3<>0 AND (EffectImplicitTargetA3=38 OR EffectImplicitTargetB3=38));

SELECT st.spell_id,
       LEFT(sp.SpellName, 34)            AS spell_name,
       st.target_entry,
       LEFT(n.name, 28)                  AS target_name,
       SUM(st.hits)                      AS hits,
       COUNT(DISTINCT st.sniff_id)       AS sniffs,
       CASE WHEN ac.entries IS NULL                              THEN 'core has no row'
            WHEN FIND_IN_SET(st.target_entry, ac.entries) > 0    THEN 'agrees'
            ELSE CONCAT('core says ', ac.entries)
       END                               AS agreement
FROM wpp_ingest.spell_target st
JOIN wpp_ingest.sniff s          ON s.id = st.sniff_id
JOIN t38                          ON t38.spell_id = st.spell_id
LEFT JOIN wotlkmangos.spell_template sp ON sp.id = st.spell_id
LEFT JOIN wpp.object_names n      ON n.objecttype = 'Unit' AND n.id = st.target_entry
LEFT JOIN (
    SELECT SourceEntry AS spell_id, GROUP_CONCAT(DISTINCT ConditionValue2 ORDER BY ConditionValue2) AS entries
    FROM acore_world.conditions
    WHERE SourceTypeOrReferenceId = 13 AND ConditionTypeOrReference = 31 AND ConditionValue1 = 3
    GROUP BY SourceEntry
) ac ON ac.spell_id = st.spell_id
WHERE s.branch IN ('Classic', 'TBC', 'WotLK')
GROUP BY st.spell_id, st.target_entry, sp.SpellName, n.name, ac.entries
HAVING hits >= 2
ORDER BY (ac.entries IS NULL) DESC, hits DESC
LIMIT 200;

-- ---------------------------------------------------------------------------
-- 2. Ground-targeted spells: destinations the core has no row for.
--
-- One spell can legitimately have several destinations - a portal spell reused per faction, or
-- per effect index - so positions are grouped rather than reduced to one. `spread_yd` is how far
-- apart the observations are: a small number is one point seen repeatedly, a large one means the
-- spell genuinely lands in more than one place and needs more than one row.
-- ---------------------------------------------------------------------------

DROP TABLE IF EXISTS t17;
CREATE TABLE t17 (spell_id INT UNSIGNED PRIMARY KEY) ENGINE=InnoDB;
INSERT INTO t17
SELECT DISTINCT id FROM wotlkmangos.spell_template
WHERE (Effect1<>0 AND (EffectImplicitTargetA1=17 OR EffectImplicitTargetB1=17))
   OR (Effect2<>0 AND (EffectImplicitTargetA2=17 OR EffectImplicitTargetB2=17))
   OR (Effect3<>0 AND (EffectImplicitTargetA3=17 OR EffectImplicitTargetB3=17));

SELECT sd.spell_id,
       LEFT(sp.SpellName, 34)                      AS spell_name,
       sd.map,
       COUNT(*)                                    AS points,
       SUM(sd.casts)                               AS casts,
       ROUND(AVG(sd.position_x), 2)                AS avg_x,
       ROUND(AVG(sd.position_y), 2)                AS avg_y,
       ROUND(AVG(sd.position_z), 2)                AS avg_z,
       ROUND(GREATEST(MAX(sd.position_x) - MIN(sd.position_x),
                      MAX(sd.position_y) - MIN(sd.position_y)), 1) AS spread_yd,
       CASE WHEN p.id IS NULL THEN 'core has no row' ELSE 'core has one' END AS agreement
FROM wpp_ingest.spell_destination sd
JOIN wpp_ingest.sniff s ON s.id = sd.sniff_id
JOIN t17                 ON t17.spell_id = sd.spell_id
LEFT JOIN wotlkmangos.spell_template sp ON sp.id = sd.spell_id
LEFT JOIN (SELECT DISTINCT ID AS id FROM acore_world.spell_target_position) p ON p.id = sd.spell_id
WHERE s.branch IN ('Classic', 'TBC', 'WotLK')
GROUP BY sd.spell_id, sd.map, sp.SpellName, p.id
ORDER BY (p.id IS NULL) DESC, casts DESC
LIMIT 200;

-- ---------------------------------------------------------------------------
-- 3. The size of the prize, in one row each.
-- ---------------------------------------------------------------------------

SELECT 'target 38 spells observed hitting something' AS metric,
       COUNT(DISTINCT st.spell_id) AS n
FROM wpp_ingest.spell_target st JOIN t38 ON t38.spell_id = st.spell_id
JOIN wpp_ingest.sniff s ON s.id = st.sniff_id AND s.branch IN ('Classic','TBC','WotLK')
UNION ALL
SELECT 'of those, no conditions row in AzerothCore',
       COUNT(DISTINCT st.spell_id)
FROM wpp_ingest.spell_target st JOIN t38 ON t38.spell_id = st.spell_id
JOIN wpp_ingest.sniff s ON s.id = st.sniff_id AND s.branch IN ('Classic','TBC','WotLK')
WHERE NOT EXISTS (SELECT 1 FROM acore_world.conditions c
                  WHERE c.SourceTypeOrReferenceId = 13 AND c.SourceEntry = st.spell_id)
UNION ALL
SELECT 'target 17 spells observed with a destination',
       COUNT(DISTINCT sd.spell_id)
FROM wpp_ingest.spell_destination sd JOIN t17 ON t17.spell_id = sd.spell_id
JOIN wpp_ingest.sniff s ON s.id = sd.sniff_id AND s.branch IN ('Classic','TBC','WotLK')
UNION ALL
SELECT 'of those, no spell_target_position row',
       COUNT(DISTINCT sd.spell_id)
FROM wpp_ingest.spell_destination sd JOIN t17 ON t17.spell_id = sd.spell_id
JOIN wpp_ingest.sniff s ON s.id = sd.sniff_id AND s.branch IN ('Classic','TBC','WotLK')
WHERE NOT EXISTS (SELECT 1 FROM acore_world.spell_target_position p WHERE p.ID = sd.spell_id);
