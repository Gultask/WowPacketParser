-- =========================================================================================
-- recover-co2.sql - rebuild the CreateObject2 flag for clients that stopped sending it.
--
-- WHAT BROKE
--
-- The Anniversary client line (TBC 2.5.5 / 2.5.6, builds 65417 and up) does not send
-- UpdateType 2 at all. Every spawn arrives as CreateObject1, so the corpus recorded
-- 258,230 spawn rows and 149,528 gameobject rows across 182 sniffs with not one CO2 in
-- them. Whole zones - Duskwood, Elwynn, Tirisfal - captured deliberately for their spawn
-- points, published as accuracy 0 or 1.
--
-- This is NOT a parse failure. WowPacketParser routes those builds to
-- WowPacketParserModule.V5_5_0_61735 (ClientVersion.cs, GetVersionDefiningBuild), which is
-- the same module that parses MoP Classic - and MoP Classic sniffs at build 64857 still
-- produce CO2 through that identical code path. Same code, different packet stream.
--
-- HOW IT IS RECOVERED
--
-- Modern creature and gameobject GUIDs carry the spawn timestamp in the low 23 bits of the
-- GUID's low half. If that timestamp is within a couple of seconds of the packet that
-- created the object, the object had just spawned - which is exactly what CO2 means.
-- Upstream already does this in V11_0_0_55666 and V12_0_0_65390 as TreatAsCreateObject2;
-- V5_5_0_61735 never got it. This script is that same test, applied after the fact.
--
-- WHY IT IS TRUSTED
--
-- The formula was checked against ground truth three ways before being used here:
--
--   * Retail builds >= 65390, where V12 already applies it - 29,330 spawn rows, perfect
--     separation. Every one of the 4,111 CO2 rows scores <= 3; not one of the 25,219 CO1
--     rows does. So this SQL reproduces upstream's C# exactly.
--   * Retail gameobjects on the same builds - 12,188 rows, zero false positives at
--     tolerance 2.
--   * TBC Classic builds <= 44833, where the client DID send the real flag, as an
--     independent benchmark. At tolerance 2: 91.0% recall, 93.2% precision.
--
-- Tolerance is 2, not upstream's default of 3, on purpose. Measured on that TBC benchmark:
--
--     tol   recall   precision
--       0    31.2%       97.1%
--       1    68.6%       94.9%
--       2    91.0%       93.2%     <- chosen
--       3    95.5%       90.9%
--       5    96.9%       86.8%
--
-- A CO2 becomes accuracy 2 in the digest, and the convention there is "2 if provable".
-- Buying 4.5 points of recall with 2.3 points of precision is the wrong trade for a column
-- that is meant to mean proven, so this stops at 2.
--
-- WHICH SNIFFS ARE TOUCHED
--
-- By client build, never by individual sniff. A single sniff having no CO2 proves nothing -
-- 244 sniffs in this corpus have 200+ spawn rows and no CO2 simply because nothing
-- respawned in view during the session, and most of them are WotLK, where CO2 capture works
-- fine. A whole BUILD having thousands of rows and no CO2 cannot happen by chance: at the
-- corpus base rate of ~3.5%, a build with 1,000 rows and zero CO2 has probability 1.6e-16.
--
-- So a build qualifies on evidence - at least 1000 spawn rows and exactly zero CO2 - and is
-- then remembered in co2_recovered_build, so that re-running this after adding more sniffs
-- on an already-known-broken build still treats them, even though the build now has CO2
-- rows and would no longer qualify on evidence alone.
--
-- REVERSIBILITY
--
-- Every changed row is listed in co2_recovered before it is changed. To undo:
--
--     UPDATE creature_spawn c JOIN co2_recovered r
--        ON r.tbl = 'creature_spawn' AND r.sniff_id = c.sniff_id AND r.guid = c.guid
--        SET c.create_type = 1;
--
-- AFTER RUNNING THIS: re-run build-digest.sql. Nothing else reads create_type, but the
-- digest reads it to decide both the published position and the accuracy, so until it is
-- rebuilt none of this reaches the module.
-- =========================================================================================

USE wpp_ingest;

SET @tolerance = 2;

-- -----------------------------------------------------------------------------------------
-- Provenance. Both tables are additive and survive re-runs.
-- -----------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS co2_recovered_build (
  branch         VARCHAR(16)  NOT NULL,
  client_build   INT UNSIGNED NOT NULL,
  spawn_rows     INT UNSIGNED NOT NULL,
  found_at_utc   DATETIME     NOT NULL,
  PRIMARY KEY (branch, client_build)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS co2_recovered (
  tbl            VARCHAR(20)      NOT NULL,
  sniff_id       BIGINT UNSIGNED  NOT NULL,
  guid           VARCHAR(40)      NOT NULL,
  entry          INT UNSIGNED     NOT NULL,
  diff_seconds   INT              NOT NULL,
  tolerance      TINYINT UNSIGNED NOT NULL,
  changed_at_utc DATETIME         NOT NULL,
  PRIMARY KEY (tbl, sniff_id, guid)
) ENGINE=InnoDB;

-- -----------------------------------------------------------------------------------------
-- Step 1: which client builds never send CO2.
-- -----------------------------------------------------------------------------------------
INSERT IGNORE INTO co2_recovered_build (branch, client_build, spawn_rows, found_at_utc)
SELECT s.branch, s.client_build, COUNT(*), UTC_TIMESTAMP()
FROM   sniff s
JOIN   creature_spawn cs ON cs.sniff_id = s.id
GROUP  BY s.branch, s.client_build
HAVING COUNT(*) >= 1000 AND SUM(cs.create_type = 2) = 0;

SELECT 'affected builds' AS step, branch, client_build, spawn_rows
FROM   co2_recovered_build ORDER BY branch, client_build;

-- -----------------------------------------------------------------------------------------
-- Step 2: record what is about to change, then change it.
--
-- The distance is circular over the 2^23-second window. Upstream subtracts without wrapping,
-- which is right except for a capture that straddles a window boundary - there the true gap
-- is small but the plain difference is nearly 2^23, and a whole sniff's CO2 would be lost at
-- once because every packet in it shares roughly the same timestamp. LEAST(d, 2^23 - d) is
-- identical to upstream everywhere else; it changed none of the 29,330 retail rows the
-- formula was validated against.
-- -----------------------------------------------------------------------------------------
DROP TEMPORARY TABLE IF EXISTS co2_candidate;
CREATE TEMPORARY TABLE co2_candidate (
  tbl          VARCHAR(20)     NOT NULL,
  sniff_id     BIGINT UNSIGNED NOT NULL,
  guid         VARCHAR(40)     NOT NULL,
  entry        INT UNSIGNED    NOT NULL,
  diff_seconds INT             NOT NULL,
  PRIMARY KEY (tbl, sniff_id, guid)
) ENGINE=InnoDB;

INSERT INTO co2_candidate (tbl, sniff_id, guid, entry, diff_seconds)
SELECT 'creature_spawn', cs.sniff_id, cs.guid, cs.entry,
       LEAST(ABS( CAST(CONV(RIGHT(cs.guid, 6), 16, 10) & 8388607 AS SIGNED)
                - CAST(MOD(TIMESTAMPDIFF(SECOND, '1970-01-01 00:00:00', cs.first_seen_utc),
                           8388608) AS SIGNED) ),
             8388608 - ABS( CAST(CONV(RIGHT(cs.guid, 6), 16, 10) & 8388607 AS SIGNED)
                          - CAST(MOD(TIMESTAMPDIFF(SECOND, '1970-01-01 00:00:00', cs.first_seen_utc),
                                     8388608) AS SIGNED) ))
FROM   creature_spawn cs
JOIN   sniff s              ON s.id = cs.sniff_id
JOIN   co2_recovered_build b ON b.branch = s.branch AND b.client_build = s.client_build
WHERE  cs.create_type = 1
  AND  cs.first_seen_utc IS NOT NULL
  AND  LEAST(ABS( CAST(CONV(RIGHT(cs.guid, 6), 16, 10) & 8388607 AS SIGNED)
                - CAST(MOD(TIMESTAMPDIFF(SECOND, '1970-01-01 00:00:00', cs.first_seen_utc),
                           8388608) AS SIGNED) ),
             8388608 - ABS( CAST(CONV(RIGHT(cs.guid, 6), 16, 10) & 8388607 AS SIGNED)
                          - CAST(MOD(TIMESTAMPDIFF(SECOND, '1970-01-01 00:00:00', cs.first_seen_utc),
                                     8388608) AS SIGNED) )) <= @tolerance;

INSERT INTO co2_candidate (tbl, sniff_id, guid, entry, diff_seconds)
SELECT 'gameobject_spawn', g.sniff_id, g.guid, g.entry,
       LEAST(ABS( CAST(CONV(RIGHT(g.guid, 6), 16, 10) & 8388607 AS SIGNED)
                - CAST(MOD(TIMESTAMPDIFF(SECOND, '1970-01-01 00:00:00', g.first_seen_utc),
                           8388608) AS SIGNED) ),
             8388608 - ABS( CAST(CONV(RIGHT(g.guid, 6), 16, 10) & 8388607 AS SIGNED)
                          - CAST(MOD(TIMESTAMPDIFF(SECOND, '1970-01-01 00:00:00', g.first_seen_utc),
                                     8388608) AS SIGNED) ))
FROM   gameobject_spawn g
JOIN   sniff s              ON s.id = g.sniff_id
JOIN   co2_recovered_build b ON b.branch = s.branch AND b.client_build = s.client_build
WHERE  g.create_type = 1
  AND  g.first_seen_utc IS NOT NULL
  AND  LEAST(ABS( CAST(CONV(RIGHT(g.guid, 6), 16, 10) & 8388607 AS SIGNED)
                - CAST(MOD(TIMESTAMPDIFF(SECOND, '1970-01-01 00:00:00', g.first_seen_utc),
                           8388608) AS SIGNED) ),
             8388608 - ABS( CAST(CONV(RIGHT(g.guid, 6), 16, 10) & 8388607 AS SIGNED)
                          - CAST(MOD(TIMESTAMPDIFF(SECOND, '1970-01-01 00:00:00', g.first_seen_utc),
                                     8388608) AS SIGNED) )) <= @tolerance;

INSERT IGNORE INTO co2_recovered
       (tbl, sniff_id, guid, entry, diff_seconds, tolerance, changed_at_utc)
SELECT tbl, sniff_id, guid, entry, diff_seconds, @tolerance, UTC_TIMESTAMP()
FROM   co2_candidate;

UPDATE creature_spawn cs
JOIN   co2_candidate c ON c.tbl = 'creature_spawn'
                      AND c.sniff_id = cs.sniff_id AND c.guid = cs.guid
SET    cs.create_type = 2;

UPDATE gameobject_spawn g
JOIN   co2_candidate c ON c.tbl = 'gameobject_spawn'
                      AND c.sniff_id = g.sniff_id AND c.guid = g.guid
SET    g.create_type = 2;

-- -----------------------------------------------------------------------------------------
-- Step 3: report.
-- -----------------------------------------------------------------------------------------
SELECT 'rows recovered' AS step, tbl, COUNT(*) AS rows_,
       COUNT(DISTINCT entry) AS entries, COUNT(DISTINCT sniff_id) AS sniffs
FROM   co2_candidate GROUP BY tbl;

SELECT 'creature CO2 rate by build, after' AS step, s.branch, s.client_build,
       COUNT(*) AS rows_, SUM(cs.create_type = 2) AS co2,
       ROUND(100 * SUM(cs.create_type = 2) / COUNT(*), 1) AS pct
FROM   sniff s
JOIN   creature_spawn cs     ON cs.sniff_id = s.id
JOIN   co2_recovered_build b ON b.branch = s.branch AND b.client_build = s.client_build
GROUP  BY s.branch, s.client_build ORDER BY s.client_build;

SELECT 'untouched builds still hold their own CO2' AS step,
       COUNT(*) AS builds, SUM(co2) AS co2_rows
FROM ( SELECT s.branch, s.client_build, SUM(cs.create_type = 2) AS co2
       FROM   sniff s
       JOIN   creature_spawn cs ON cs.sniff_id = s.id
       LEFT   JOIN co2_recovered_build b
              ON b.branch = s.branch AND b.client_build = s.client_build
       WHERE  b.branch IS NULL
       GROUP  BY s.branch, s.client_build ) x;

DROP TEMPORARY TABLE co2_candidate;

SELECT 'NEXT: re-run build-digest.sql, nothing published changes until then' AS reminder;
