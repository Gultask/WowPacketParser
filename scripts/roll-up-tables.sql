-- =========================================================================================
-- roll-up-tables.sql - the digest for the tables that never had one.
--
-- Eleven parser-written tables were read by no script at all: collected on every ingest,
-- 4.2M rows and about 860 MB, and never turned into an answer. Most of them do not need
-- inference the way stand_state or faction do. They need the sniff dimension collapsed and
-- the observations counted: how many times was item X seen on vendor Y, how many sniffs agree
-- this creature wears that weapon, how many destinations does this areatrigger really have.
--
-- Every table here therefore carries `sniffs` - the number of distinct captures that saw the
-- row - because one sniff seeing a thing ten times is far weaker evidence than ten sniffs
-- seeing it once, and collapsing to a bare DISTINCT throws exactly that away.
--
-- Three of these are not simple rollups and are documented where they appear:
--   * at_teleport       - gated on pairing delay, because the collector's window is 30 s
--   * entry_spell_target - gated on the spell's implicit target, because 94.6% is noise
--   * npc_spellclick     - absent on purpose; see the note at the end
--
-- Cheap to rebuild, so every table is dropped and recreated. Run after entry-values.sql.
-- =========================================================================================

USE wpp_ingest2;

SET SESSION tmp_table_size       = 2147483648;
SET SESSION max_heap_table_size  = 2147483648;

-- -----------------------------------------------------------------------------------------
-- 1. Equipment. AC's creature_equip_template, one row per entry per distinct set.
--    Collapses 188:1. `sets` says whether the entry ever wore anything else.
-- -----------------------------------------------------------------------------------------
DROP TABLE IF EXISTS entry_equip;
CREATE TABLE entry_equip (
  entry     INT UNSIGNED NOT NULL,
  item_id1  INT UNSIGNED NOT NULL,
  item_id2  INT UNSIGNED NOT NULL,
  item_id3  INT UNSIGNED NOT NULL,
  sniffs    INT UNSIGNED NOT NULL,
  guids     INT UNSIGNED NOT NULL,
  sets      INT UNSIGNED NOT NULL COMMENT 'distinct equipment sets this entry was ever seen in',
  PRIMARY KEY (entry, item_id1, item_id2, item_id3)
) ENGINE=InnoDB;

INSERT INTO entry_equip
SELECT e.entry, e.item_id1, e.item_id2, e.item_id3,
       COUNT(DISTINCT e.sniff_id), COUNT(DISTINCT e.guid), s.sets
FROM   creature_equip e
JOIN  (SELECT entry, COUNT(DISTINCT item_id1, item_id2, item_id3) AS sets
       FROM creature_equip GROUP BY entry) s ON s.entry = e.entry
GROUP  BY e.entry, e.item_id1, e.item_id2, e.item_id3, s.sets;

-- -----------------------------------------------------------------------------------------
-- 2. Models. AC's creature_template_model. `probability` is the server's own weight, so it is
--    averaged rather than counted - two sniffs reporting 0.5 do not make 1.0.
-- -----------------------------------------------------------------------------------------
DROP TABLE IF EXISTS entry_model;
CREATE TABLE entry_model (
  entry        INT UNSIGNED NOT NULL,
  display_id   INT UNSIGNED NOT NULL,
  sniffs       INT UNSIGNED NOT NULL,
  observations INT UNSIGNED NOT NULL,
  scale        FLOAT        NOT NULL,
  probability  FLOAT        NOT NULL,
  models       INT UNSIGNED NOT NULL COMMENT 'distinct display ids this entry was ever seen with',
  PRIMARY KEY (entry, display_id)
) ENGINE=InnoDB;

INSERT INTO entry_model
SELECT m.entry, m.display_id, COUNT(DISTINCT m.sniff_id), COUNT(*),
       AVG(m.display_scale), AVG(m.probability), c.models
FROM   creature_template_model m
JOIN  (SELECT entry, COUNT(DISTINCT display_id) AS models
       FROM creature_template_model GROUP BY entry) c ON c.entry = m.entry
GROUP  BY m.entry, m.display_id, c.models;

-- -----------------------------------------------------------------------------------------
-- 3. Vendors. AC's npc_vendor. A vendor's stock changes with reputation and quest state, so
--    `sniffs` here is the point: an item seen by one capture out of twenty is conditional,
--    not standard stock, and this is the only column that can tell the difference.
-- -----------------------------------------------------------------------------------------
-- Column types mirror npc_vendor exactly rather than being tidied up. About eleven rows in the
-- corpus are misparsed - one from vendor 32413 reads slot 9203712, max_count 134217728,
-- type 2332033024, and two more carry item_id = -256 with every other field 0xFFFFFF00. A
-- narrower column turns those into ER_WARN_DATA_OUT_OF_RANGE and stops the script, and
-- filtering them out silently would hide a parser bug behind a clean-looking table. They are
-- carried through and counted in the report instead.
--
-- Note that a large `slot` is NOT one of those: vendor 12778 legitimately uses slots 1001-1008
-- with sensible items and costs, so slot has no plausible upper bound to test against.
DROP TABLE IF EXISTS entry_vendor;
CREATE TABLE entry_vendor (
  entry          INT UNSIGNED NOT NULL,
  item_id        INT          NOT NULL,
  slot           INT          NOT NULL,
  max_count      INT UNSIGNED NOT NULL,
  extended_cost  INT UNSIGNED NOT NULL,
  type           INT UNSIGNED NOT NULL,
  sniffs         INT UNSIGNED NOT NULL,
  vendor_sniffs  INT UNSIGNED NOT NULL COMMENT 'sniffs that saw this vendor at all',
  PRIMARY KEY (entry, item_id)
) ENGINE=InnoDB;

INSERT INTO entry_vendor
SELECT v.entry, v.item_id, MIN(v.slot), MIN(v.max_count), MIN(v.extended_cost), MIN(v.type),
       COUNT(DISTINCT v.sniff_id), t.vendor_sniffs
FROM   npc_vendor v
JOIN  (SELECT entry, COUNT(DISTINCT sniff_id) AS vendor_sniffs
       FROM npc_vendor GROUP BY entry) t ON t.entry = v.entry
GROUP  BY v.entry, v.item_id, t.vendor_sniffs;

-- -----------------------------------------------------------------------------------------
-- 4. Quest drops, action bars, gossip bindings, menus and texts. Straight rollups.
-- -----------------------------------------------------------------------------------------
DROP TABLE IF EXISTS entry_quest_item;
CREATE TABLE entry_quest_item (
  entry   INT UNSIGNED NOT NULL,
  item_id INT UNSIGNED NOT NULL,
  sniffs  INT UNSIGNED NOT NULL,
  PRIMARY KEY (entry, item_id)
) ENGINE=InnoDB;
INSERT INTO entry_quest_item
SELECT entry, item_id, COUNT(DISTINCT sniff_id) FROM creature_quest_item GROUP BY entry, item_id;

DROP TABLE IF EXISTS entry_action_spell;
CREATE TABLE entry_action_spell (
  entry    INT UNSIGNED NOT NULL,
  spell_id INT UNSIGNED NOT NULL,
  slots    VARCHAR(64)  NOT NULL COMMENT 'action bar indexes it was seen in',
  sources  VARCHAR(64)  NOT NULL COMMENT 'which of the three stores reported it',
  sniffs   INT UNSIGNED NOT NULL,
  PRIMARY KEY (entry, spell_id)
) ENGINE=InnoDB;
INSERT INTO entry_action_spell
SELECT entry, spell_id, GROUP_CONCAT(DISTINCT idx ORDER BY idx),
       GROUP_CONCAT(DISTINCT source ORDER BY source), COUNT(DISTINCT sniff_id)
FROM   creature_template_spell GROUP BY entry, spell_id;

DROP TABLE IF EXISTS entry_gossip_menu;
CREATE TABLE entry_gossip_menu (
  entry   INT UNSIGNED NOT NULL,
  menu_id INT UNSIGNED NOT NULL,
  sniffs  INT UNSIGNED NOT NULL,
  PRIMARY KEY (entry, menu_id)
) ENGINE=InnoDB;
INSERT INTO entry_gossip_menu
SELECT entry, menu_id, COUNT(DISTINCT sniff_id) FROM creature_gossip GROUP BY entry, menu_id;

DROP TABLE IF EXISTS menu_text;
CREATE TABLE menu_text (
  menu_id      INT UNSIGNED NOT NULL,
  text_id      INT UNSIGNED NOT NULL,
  sniffs       INT UNSIGNED NOT NULL,
  observations INT UNSIGNED NOT NULL,
  PRIMARY KEY (menu_id, text_id)
) ENGINE=InnoDB;
INSERT INTO menu_text
SELECT menu_id, text_id, COUNT(DISTINCT sniff_id), COALESCE(SUM(observations), COUNT(*))
FROM   gossip_menu GROUP BY menu_id, text_id;

DROP TABLE IF EXISTS menu_option;
CREATE TABLE menu_option (
  menu_id      INT UNSIGNED NOT NULL,
  option_index INT UNSIGNED NOT NULL,
  option_icon  INT UNSIGNED NOT NULL,
  option_text  TEXT,
  box_money    BIGINT UNSIGNED NOT NULL,
  box_coded    TINYINT UNSIGNED NOT NULL,
  box_text     TEXT,
  sniffs       INT UNSIGNED NOT NULL,
  PRIMARY KEY (menu_id, option_index)
) ENGINE=InnoDB;
INSERT INTO menu_option
SELECT menu_id, option_index, MIN(option_icon), MIN(option_text), MIN(box_money),
       MIN(box_coded), MIN(box_text), COUNT(DISTINCT sniff_id)
FROM   gossip_menu_option GROUP BY menu_id, option_index;

DROP TABLE IF EXISTS text_line;
CREATE TABLE text_line (
  text_id           INT UNSIGNED NOT NULL,
  slot              INT UNSIGNED NOT NULL,
  probability       FLOAT        NOT NULL,
  text0             TEXT,
  text1             TEXT,
  language          INT UNSIGNED NOT NULL,
  broadcast_text_id INT UNSIGNED NOT NULL,
  sniffs            INT UNSIGNED NOT NULL,
  PRIMARY KEY (text_id, slot)
) ENGINE=InnoDB;
INSERT INTO text_line
SELECT text_id, slot, AVG(probability), MIN(text0), MIN(text1), MIN(language),
       MIN(broadcast_text_id), COUNT(DISTINCT sniff_id)
FROM   npc_text GROUP BY text_id, slot;

-- -----------------------------------------------------------------------------------------
-- 5. Areatrigger teleports - the one that needed a real gate rather than a rollup.
--
-- The collector pairs an areatrigger with the next world change within 30 seconds, which is
-- generous enough for a loading screen and therefore generous enough to catch a hearthstone.
-- It shows: of 261 triggers, 66 came out with more than one destination, and the extras are
-- overwhelmingly Shattrath and Dalaran - where the player went next, not where the trigger
-- sent them. The tell is the delay. Triggers that resolved to a single destination average
-- 3.9 s; the multi-destination ones average 16.6 s.
--
-- So the gate is on delay, NOT on collapsing each trigger to one destination. That
-- distinction matters: a trigger genuinely can have more than one destination - five here
-- have several, each reached in under 3 s and seen repeatedly, which is what a conditional
-- destination looks like and not what a mis-pairing looks like. Forcing one row per trigger
-- would delete real data to tidy up an artefact of the pairing window.
--
-- Destinations are rounded to a yard before being counted distinct: arrival position varies
-- slightly between captures, and three points inside a 70-yard circle are one destination.
-- -----------------------------------------------------------------------------------------
DROP TABLE IF EXISTS at_teleport;
CREATE TABLE at_teleport (
  areatrigger_id INT UNSIGNED NOT NULL,
  to_map         INT UNSIGNED NOT NULL,
  to_x           FLOAT        NOT NULL,
  to_y           FLOAT        NOT NULL,
  to_z           FLOAT        NOT NULL,
  to_orientation FLOAT        NOT NULL,
  sniffs         INT UNSIGNED NOT NULL,
  observations   INT UNSIGNED NOT NULL,
  min_delay_ms   INT UNSIGNED NOT NULL,
  destinations   INT UNSIGNED NOT NULL COMMENT 'how many survived the gate for this trigger',
  PRIMARY KEY (areatrigger_id, to_map, to_x, to_y, to_z)
) ENGINE=InnoDB;

INSERT INTO at_teleport
SELECT g.areatrigger_id, g.to_map, g.rx, g.ry, g.rz, g.orientation,
       g.sniffs, g.observations, g.min_delay_ms, d.destinations
FROM (
  SELECT areatrigger_id, to_map, ROUND(to_x) AS rx, ROUND(to_y) AS ry, ROUND(to_z) AS rz,
         MIN(to_orientation) AS orientation, COUNT(DISTINCT sniff_id) AS sniffs,
         COUNT(*) AS observations, MIN(delay_ms) AS min_delay_ms
  FROM   areatrigger_teleport
  WHERE  delay_ms <= 3000
  GROUP  BY areatrigger_id, to_map, rx, ry, rz) g
JOIN (
  SELECT areatrigger_id, COUNT(*) AS destinations FROM (
    SELECT areatrigger_id, to_map, ROUND(to_x) AS rx, ROUND(to_y) AS ry, ROUND(to_z) AS rz
    FROM   areatrigger_teleport WHERE delay_ms <= 3000
    GROUP  BY areatrigger_id, to_map, rx, ry, rz) z
  GROUP BY areatrigger_id) d ON d.areatrigger_id = g.areatrigger_id;

-- -----------------------------------------------------------------------------------------
-- 6. Spell targets - gated on what the spell actually targets.
--
-- spell_target holds 2.05M rows, and 94.6% of them are for spells that have no entry-based
-- implicit target in any of their six effect slots. The table exists to answer "which creature
-- entry does this entry-targeted spell hit", and for a spell that targets whatever is in front
-- of the caster the answer is meaningless - it records the sniffer's questing, not the spell.
--
-- The gate is Spell.dbc's EffectImplicitTargetA/B, read from wotlkmangos.spell_template. The
-- entry-based values are 7 and 8 (area, by entry), 38 (nearby unit by entry), 40 (nearby
-- gameobject by entry), 46 (destination near an entry) and 60 (cone, by entry); 38 is the
-- canonical one and is flagged separately.
--
-- Spells absent from 3.3.5 Spell.dbc are KEPT and marked, not dropped. There are 1,401 of
-- them, all Cata and later, and this database cannot say what they target. Discarding them
-- would be asserting something the evidence does not support.
-- -----------------------------------------------------------------------------------------
DROP TABLE IF EXISTS entry_spell_target;
CREATE TABLE entry_spell_target (
  spell_id      INT UNSIGNED NOT NULL,
  caster_entry  INT UNSIGNED NOT NULL,
  target_entry  INT UNSIGNED NOT NULL,
  caster_type   VARCHAR(16)  NOT NULL,
  target_type   VARCHAR(16)  NOT NULL,
  hits          INT UNSIGNED NOT NULL,
  sniffs        INT UNSIGNED NOT NULL,
  target_rule   VARCHAR(16)  NOT NULL COMMENT 'nearby_entry, entry_based, or unknown_spell',
  PRIMARY KEY (spell_id, caster_entry, target_entry, caster_type, target_type)
) ENGINE=InnoDB;

INSERT INTO entry_spell_target
SELECT st.spell_id, st.caster_entry, st.target_entry, st.caster_type, st.target_type,
       SUM(st.hits), COUNT(DISTINCT st.sniff_id),
       CASE WHEN t.Id IS NULL     THEN 'unknown_spell'
            WHEN t.nearby_entry   THEN 'nearby_entry'
            ELSE 'entry_based' END
FROM   spell_target st
LEFT JOIN (
  SELECT Id,
    (EffectImplicitTargetA1 IN (7,8,38,40,46,60) OR EffectImplicitTargetB1 IN (7,8,38,40,46,60)
  OR EffectImplicitTargetA2 IN (7,8,38,40,46,60) OR EffectImplicitTargetB2 IN (7,8,38,40,46,60)
  OR EffectImplicitTargetA3 IN (7,8,38,40,46,60) OR EffectImplicitTargetB3 IN (7,8,38,40,46,60))
      AS entry_targeted,
    (EffectImplicitTargetA1 = 38 OR EffectImplicitTargetB1 = 38
  OR EffectImplicitTargetA2 = 38 OR EffectImplicitTargetB2 = 38
  OR EffectImplicitTargetA3 = 38 OR EffectImplicitTargetB3 = 38) AS nearby_entry
  FROM wotlkmangos.spell_template) t ON t.Id = st.spell_id
WHERE  t.Id IS NULL OR t.entry_targeted
GROUP  BY st.spell_id, st.caster_entry, st.target_entry, st.caster_type, st.target_type,
          t.Id, t.nearby_entry;

-- -----------------------------------------------------------------------------------------
-- 7. Report.
-- -----------------------------------------------------------------------------------------
SELECT 'entry_equip'        AS t, COUNT(*) AS rows_, COUNT(DISTINCT entry) AS keys_ FROM entry_equip
UNION ALL SELECT 'entry_model',        COUNT(*), COUNT(DISTINCT entry)   FROM entry_model
UNION ALL SELECT 'entry_vendor',       COUNT(*), COUNT(DISTINCT entry)   FROM entry_vendor
UNION ALL SELECT 'entry_quest_item',   COUNT(*), COUNT(DISTINCT entry)   FROM entry_quest_item
UNION ALL SELECT 'entry_action_spell', COUNT(*), COUNT(DISTINCT entry)   FROM entry_action_spell
UNION ALL SELECT 'entry_gossip_menu',  COUNT(*), COUNT(DISTINCT entry)   FROM entry_gossip_menu
UNION ALL SELECT 'menu_text',          COUNT(*), COUNT(DISTINCT menu_id) FROM menu_text
UNION ALL SELECT 'menu_option',        COUNT(*), COUNT(DISTINCT menu_id) FROM menu_option
UNION ALL SELECT 'text_line',          COUNT(*), COUNT(DISTINCT text_id) FROM text_line
UNION ALL SELECT 'at_teleport',        COUNT(*), COUNT(DISTINCT areatrigger_id) FROM at_teleport
UNION ALL SELECT 'entry_spell_target', COUNT(*), COUNT(DISTINCT spell_id) FROM entry_spell_target;

SELECT 'spell_target kept by rule' AS q, target_rule, COUNT(*) AS rows_, SUM(hits) AS hits
FROM   entry_spell_target GROUP BY target_rule ORDER BY rows_ DESC;

SELECT 'areatrigger destinations after the delay gate' AS q, destinations, COUNT(DISTINCT areatrigger_id) AS triggers
FROM   at_teleport GROUP BY destinations ORDER BY destinations;

SELECT 'misparsed vendor rows, kept and flagged' AS q, COUNT(*) AS rows_,
       COUNT(DISTINCT entry) AS vendors
FROM   entry_vendor
WHERE  item_id <= 0 OR max_count > 1000000 OR extended_cost > 1000000 OR type > 100;

-- npc_spellclick has no rollup here, deliberately. The source table is empty and always has
-- been: CollectNpcSpellClicks pairs Storage.NpcSpellClicks with Storage.SpellClicks, and the
-- modules this corpus actually uses - V3_4_0, V4_4_0, V5_5_0 - fill the first and not the
-- second. Zero `ok` sniffs out of 4,511. A rollup would just be an empty table implying the
-- data is missing from the sniffs rather than from the parser.
