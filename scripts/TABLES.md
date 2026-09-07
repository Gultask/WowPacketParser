# What is in the ingest database

Written 2026-09-03, when `wpp_ingest` had grown to 53 tables and nobody could say what half of
them were for any more. Every table below was attributed by looking for the statement that
creates it; anything with no such statement is named as an orphan rather than guessed at.

Three kinds of table live here, and only the first is precious.

## 1. Parser-written — the ingest itself

Created by `WowPacketParser/SQL/IngestDatabase.cs` and filled by `--DumpFormat 17`. These are
the only tables that cannot be rebuilt without re-reading the sniffs, which takes about a day.

| table | one row per | notes |
|---|---|---|
| `sniff` | file | build, branch, packet counts, clock. Everything else hangs off `sniff_id`. |
| `sniff_map` | sniff × map | yield per map, plus the packet census and how many were gated |
| `sniff_coverage` | sniff × capability | what this sniff *could* give up; see below |
| `map_validity` | target × map | which branches' terrain matches 3.3.5, and what rebuilt the rest |
| `creature_spawn` | sniff × creature | position, level, faction, flags. Dead creatures excluded. |
| `gameobject_spawn` | sniff × gameobject | position and rotation quaternion |
| `creature_waypoint` | move order point | the big one — destinations, not pathfinding filler |
| `creature_movement` | sniff × creature | movement reduced to a centre and a radius |
| `loot_instance` / `loot_instance_item` | loot opened | empty loots kept on purpose: they are the denominator |
| `creature_spell_cast` | SMSG_SPELL_START | raw; the gap between two is the cooldown observation |
| `spell_target` | sniff × spell × target entry | what an entry-targeted spell actually hit |
| `spell_destination` | sniff × spell × point | where a ground-targeted spell was aimed |
| `creature_equip` | sniff × creature | the three virtual item slots |
| `creature_aura` | sniff × creature × spell | with a flag for auras present at creation |
| `gossip_menu` / `gossip_menu_option` / `npc_text` | menu, option, text | as the server sent them |
| `areatrigger_teleport` | trigger paired to a world change | `delay_ms` says how much to trust the pairing |
| `npc_vendor` | sniff × vendor × slot | the list as the player was shown it |
| `npc_spellclick` | sniff × creature × spell | click paired to the cast it produced |
| `creature_template_spell` | sniff × creature × slot | the action bar of a controlled creature |
| `creature_quest_item` | sniff × creature × index | quest drops from the creature query response |
| `creature_gossip` | sniff × creature × menu | which menu a creature opened with |
| `creature_value` | sniff × entry × map × field × value | faction, speeds, resistances, combat reach, bounding radius, attack times, mount, npc flags, level, model, unit flags, emote and stand state |
| `creature_template` | sniff × entry | the static half, stated by the query response |
| `creature_template_model` | sniff × entry × index | display ids, variable in number |
| `creature_aggro` | hostile AI reaction | one row per pull, not per creature |

### These belong to the entry, but they are recorded per guid

The question worth asking is what values an *entry* accepts, and `entry-values.sql` answers it
by rolling `creature_value` up into `entry_value` and `entry_value_best`. The rollup counts
DISTINCT guid, never rows: a creature standing in view for an hour resends its faction on every
update block while another sends it once, and counting rows would let the first outvote the
second.

The per-guid rows are what make the rollup honest, which is why storage stays at that grain.
Two guids of one entry disagreeing is the signal that a field is conditional, and it is invisible
the moment guids are merged. `entry_value_best.verdict` reports it: **settled** (every guid
agreed), **dominant** (at least 80%), **split** (less). On one 3.4.0 capture, 968 entries:

| field | settled | dominant | split |
|---|---:|---:|---:|
| `speed_walk` | 966 | 0 | 2 |
| `speed_run` | 951 | 10 | 7 |
| `faction_template` | 288 | 0 | 4 |
| `level` | 251 | 2 | 37 |

**Speeds are already AzerothCore multipliers.** Every handler divides by the 2.5 and 7.0
baselines before storing, so a text dump printing `RunSpeed: 8` becomes `1.142857` here - which
is what `creature_template.speed_run` wants. Do not divide again.

`creature_spawn` no longer carries `faction`, `level`, `unit_flags`, `emote_state`,
`stand_state` or `sheathe_state`. Those were never the spawn's values in the first place: the
collector read them from the merged `UnitData` at the end of parsing, so a creature that changed
faction mid-sniff reported the changed one. `health` stays, being genuinely instantaneous and
what tells a corpse from a spawn.

### Why `creature_value` is long format, and aggregated

None of these are constants, so one row per value rather than one column per field. `on_create`
separates what a spawn started with from what the world did to it afterwards.

The rows are **aggregated within each sniff** to entry, map, field and value, carrying a distinct
guid count. Per-guid rows cost 94 MB on a single capture - 4.7x `creature_waypoint`, previously
the big table - and were almost all repetition, since guids of one entry overwhelmingly agree.
Aggregated it is 8.5 MB, an 11x cut, and `entry_value` sums the counts to the same answer:
`speed_walk` and `speed_run` verdicts came out identical either way.

What that costs is per-guid identity. `creature_addon` style work at the spawn level cannot be
done from this table any more. One signal was worth keeping and is kept explicitly:
`changed_guids` says how many creatures held **more than one** value for a field, which
separates *one creature that changed* from *two that always disagreed* - a bare count cannot.

Two collection paths feed it, and both are needed. Folding update blocks gives the history and
the `on_create` flag, but a field that never appears in a block the collector sees is simply
absent: that alone covered **1,167 of 8,046 spawns** for faction. A sweep of the merged
`UnitData` at the end - where the old `creature_spawn.faction` column read from - brings it to
**8,047 of 8,047**.

Resistances stay the extreme case. `UNIT_FIELD_RESISTANCES` is `PRIVATE | OWNER | SPECIAL_INFO`,
so it arrives only for a unit the player owns or controls: **6 guids out of 2,585**, none of them
on create. Absence is not zero.

**Speeds are create-time only.** Nothing writes `SMSG_MOVE_SPLINE_SET_RUN_SPEED` back to the
stored object, so an aura that changes speed mid-sniff does not appear here. That is the right
value for `creature_template.speed_run` - the base before buffs - but it is not every speed the
creature had.

### `creature_spell_cast` and `creature_aura` are not the same table

They overlap, and the overlap is the useful part. On that capture:

| | count |
|---|---:|
| distinct guid+spell as an aura | 5,844 |
| distinct guid+spell as a cast | 2,086 |
| in both | 888 |
| auras that guid was never seen casting | 4,956 |
| on-create permanent auras | 2,486 |

An aura the creature was seen casting is a combat buff it applies to itself, so it does **not**
belong in `creature_template_addon.auras`; the 4,956 it was never seen casting are the
candidates, and the 2,486 permanent on-create ones are the strongest of those. The join between
the two tables is what tells them apart, which is exactly why both are kept.

The cast table also holds 4,642 raw events behind those 2,086 distinct pairs. That surplus is
the timing - the gaps a cooldown is read from - and the aura table cannot supply it at all.

### Initial timers need `creature_aggro`

The gap between two casts of a spell is the repeat timer. The gap from the *pull* to the first
cast is a different number, and AzerothCore stores both. A creature that opens with a bolt and
then repeats it every 8s has an initial timer near zero and a repeat near 8000; deriving only
the repeat would make it silent on the pull.

`creature_aggro` is every hostile `SMSG_AI_REACTION` with its timestamp - one row per pull, not
per creature, because a reset and re-pull restarts the AI timers. The waypoint collector was
already reading these packets to tell combat movement from patrol and discarding the times.
`entry-values.sql` joins them into `spell_initial_gap` and `spell_initial_timer`.

Trust the median, not the max: `max_ms` runs to the 60s bound because it is the first cast of
*that* spell after the pull, and a spell the creature only reaches late in a fight will sit near
the ceiling. `pulls` is the sample size - one pull proves nothing.

`sniff_coverage` is the one that is easy to underrate. A sniff that produced no loot because its
build has no `SMSG_LOOT_RESPONSE` and a sniff that produced no loot because the player looted
nothing both look like zero rows afterwards. The difference is only knowable at parse time, so
it is recorded then: `status` is `ok`, `empty` or `unsupported`, and `collector_version` makes
the sniffs that predate a collector improvement selectable as a re-parse work list.

## 2. Script-derived — rebuildable, and dropped by their own script

Re-running the script drops and recreates these, so losing them costs only time.

| tables | built by | cost |
|---|---|---|
| `wp_point`, `wp_node`, `wp_edge`, `wp_edge_raw`, `wp_level`, `wp_stack`, `wp_zobs` | `mine-paths.sql` | ~1 h 35 m |
| `path_summary`, `path_point` | `mine-paths.sh` (via `chain-paths.py`) | 11 s once the graph exists |
| `dg_spawn`, `dg_est`, `dg_approx`, `dg_fixed`, `dg_inst_rad`, `dg_rad_key`, `dg_route_pt` | `build-digest.sql` | ~4 h |
| `st_gap`, `st_timer` | `spell-timers.sql` | minutes |

## 3. Orphans — no script creates them

Twenty-seven tables, and this is the part of the question that was worth asking. None of them
has a statement anywhere in the repository that would recreate it, which means each was typed at
a prompt during one investigation and then left behind. `PIPELINE.md` already warns about
exactly this; these are the debt it was warning about.

| tables | what they were |
|---|---|
| `pub_loot_set`, `pub_loot`, `pub_loot_item`, `pub_names`, `pub_go_spawn`, `pub_go_point`, `pub_reject` | the first loot release layout. **Superseded**: `publish-loot.sql` now writes `acore_world.sniff_*` directly, and nothing in the repository reads a `pub_` table any more. |
| `qgo`, `qgo_ac`, `qgo_match`, `qgo_obs`, `qgo_persniff`, `qgo_quest`, `qgo_report`, `qgo_sn`, `qgo_win` | a quest-gameobject investigation |
| `acwp`, `acgo` | AzerothCore's own waypoints and gameobjects, imported to compare against |
| `go_point`, `go_diff` | gameobject point comparison, an ancestor of `pub_go_point` |
| `zone_cov`, `zone_target` | coverage per zone, for deciding where to sniff next |
| `multispawn_evidence`, `rt_end`, `ta`, `wp_deg`, `loot_readable` | scratch from the spawn and route work |

**Recommendation: let all twenty-seven go.** They are 205 MB and no live path reads any of them.
The rebuild happening now writes into a new database and simply does not carry them across, so
nothing has to be deleted — the old `wpp_ingest` stays where it is until it is not wanted.

The rule that stops this recurring is already in `PIPELINE.md`: anything typed at a prompt
belongs in a script before the session ends. `spell-timers.sql` exists because of that rule.

## Rebuilding

The current run writes to **`wpp_ingest2`**, not `wpp_ingest`. That is deliberate:

- `sniff_map` gained columns (`packets`, `gated_packets`, `creature_spells`) and the DDL is
  `CREATE TABLE IF NOT EXISTS`, so an existing table would silently keep the old shape.
- The old database stays queryable, and the published digest keeps working, while the new one
  is checked.

One thing the rebuild gets back for free: `distill-waypoints.sql` permanently deleted 12.8M
waypoints belonging to 106,437 creatures, and `build-digest.sql` measures its spawn radius from
waypoints where it has them. Those come back in `wpp_ingest2`. Per `PIPELINE.md`, run
`build-digest.sql` **before** any re-distillation this time, and keep its radius output.
