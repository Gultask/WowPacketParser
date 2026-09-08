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
| `creature_aura` | sniff × creature × spell | mostly the player's own debuffs; `entry-auras.sql` sorts them |
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

### `create_type` is reconstructed for clients that stopped sending it

`creature_spawn.create_type` is the whole basis of the digest's accuracy column: 2 means the
sniff caught the creature in the act of spawning, and only those points are published as
accuracy 2. So a client that stops reporting it costs the corpus its best evidence silently.

The Anniversary line does exactly that. TBC 2.5.5 / 2.5.6 from build 65417 never sends
`UpdateType 2` - 258,230 spawn rows and 149,528 gameobject rows across 182 sniffs, not one CO2
among them, including zones captured specifically to record spawn points. Nothing was
mis-parsed: `GetVersionDefiningBuild` sends those builds to the same module as MoP Classic, and
MoP at build 64857 still produces CO2 through that identical code. The packets simply no longer
carry it.

It is recoverable because modern GUIDs carry the object's spawn timestamp in the low 23 bits of
their low half. If that is within a couple of seconds of the packet that created the object,
the object had just spawned. Upstream does this on retail as `TreatAsCreateObject2`; the fork
now does it in `V5_5_0_61735` for new ingests, and `scripts/recover-co2.sql` does it after the
fact for sniffs already stored.

**What this means when reading the table.** Some CO2 rows are now inferred rather than reported,
at a measured 93.2% precision and 91.0% recall. `co2_recovered` lists every one of them, so a
query that must have only client-reported CO2 can exclude them by joining it. Nothing else
should: the digest treats both alike on purpose.

**Do not widen the recovery to "any sniff with no CO2".** 244 sniffs here have 200+ spawn rows
and no CO2 for the ordinary reason that nothing respawned while the sniffer was watching, and
most of them are WotLK, where capture works fine. The gate is per client build, where a
thousand-row sample with zero CO2 is not something chance produces.

### `unit_flags` is a third runtime state

`unit_flags` is the most unstable field in `creature_value` - 1.293 values per entry per sniff,
against 1.024 for faction - because the server sets bits there as the world runs. In combat,
stunned, fleeing, looting, skinnable, mounted: all true when the packet was sent, none of them a
property of the entry. Across the corpus the raw field holds **1,127 distinct values**.

`entry-values.sql` strips them before the vote, using WowPacketParser's own
`UnitFlags.Disallowed` - the same list upstream strips before writing `creature_template`, so a
row here and a row from the text dump agree. What is left is 5 bits and **21 distinct values**,
and 375,000 rows collapse into their neighbours. `unit_flags2` goes 93 to 15 and `unit_flags3`
53 to 37.

`creature_value` keeps the raw value. `entry-values.sql` is the answer, `creature_value` is the
evidence, and one of the runtime bits it preserves - `PlayerControlled` - is what
`entry-auras.sql` uses to recognise a pet.

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

### Most of `creature_aura` is the sniffer's own debuffs

Of the 335,607 entry-and-spell pairs in the corpus, **7.4% are a creature's own**. The
widest-spread auras on creatures are Winter's Chill on 2,420 entries, Frost Fever on 2,235,
Corruption on 1,989: one warlock's damage over time, following them from mob to mob for a whole
capture. Anything reading the table raw as "auras this creature has" is reading a combat log.

`entry-auras.sql` sorts them into `spell_aura` (what a spell is, corpus-wide) and `entry_aura`
(what an entry carries), on four signals. None of the four is sufficient alone, and each one is
there because it catches something the others miss:

| signal | catches | misses |
|---|---|---|
| never carried a duration | ordinary DoTs and buffs | Savage Combat, permanent on all 39,308 sightings |
| the packet named the caster | Savage Combat, Shadow Embrace, Blood Frenzy | only trustworthy on WotLK and TBC (below) |
| `SpellFamilyName` is a class, consumable or pet talent | anything on a branch with no caster | creature abilities are family 0, so it says nothing about them |
| `UNIT_FLAG_PLAYER_CONTROLLED` on the entry | pet scaling auras, which pass all three others | nothing else; it is an entry-level fact |

A spell judged on trusted evidence anywhere in the corpus is judged everywhere, which is what
lets the caster signal reach branches that cannot supply it.

**The caster column is only trustworthy on WotLK and TBC, and collector version 1 did not say
so.** Eleven call sites across nine version modules read the aura's caster into the protobuf
entry and never onto the `Aura` object, so `CasterGuid` was null on every branch but TBC - and
the collector read null as "no caster was sent, therefore the creature cast it". Cata, MoP,
Retail and Classic came out **100.0% self-cast**. Where the caster does survive the column is
excellent: across 58,147 WotLK sightings of eight known player DoTs, not one is marked
self-cast. Collector version 2 assigns `CasterGuid` in those modules and records **2 for "the
packet did not say"**, so the failure can no longer hide as an answer; `entry-auras.sql` trusts
version 2 on any branch and version 1 only on WotLK and TBC.

| `entry_aura.verdict` | pairs | |
|---|---:|---|
| `player` | 260,693 | someone else cast it |
| `pet` | 27,831 | the entry is a summon, whatever it is carrying |
| `combat` | 19,346 | its own, but seen with a duration, so it cast it during a fight |
| **`addon`** | **24,666** | **its own and never timed - the creature_addon candidates** |
| `unknown` | 3,071 | no trusted sniff ever saw it |

**`addon` is the only one of the five worth publishing**, and 23,313 of its 24,666 rows have a
WotLK or TBC sniff behind them. The list it produces reads like `creature_template_addon` should:
a Wild Flower with a grow visual, a Pyrite Safety Container with a parachute, a Living Poison
with Invisibility and Stealth Detection, a Glacier Penguin with Creature Random Size.

Twenty known player spells - Corruption, Immolate, Shadow Word: Pain, Frost Fever, Winter's
Chill, Sunder Armor and the rest - cover 28,158 entry pairs between them. All 28,158 come out
`player` or `pet`, and **none reaches `addon`**. That is the test worth re-running after any
change to the four signals.

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
| `dg_spawn`, `dg_est`, `dg_approx`, `dg_fixed`, `dg_inst_rad`, `dg_rad_key`, `dg_route_pt`, `dg_state` | `build-digest.sql` | ~4 h |
| `co2_recovered`, `co2_recovered_build` | `recover-co2.sql` | ~1 min. **Additive, not dropped** - they are the record of which rows it changed, and deleting them loses the ability to undo it. |
| `entry_value`, `entry_value_best`, `spell_initial_gap`, `spell_initial_timer`, `waypoint_segment_speed`, `entry_travel_mode` | `entry-values.sql` | minutes |
| `aura_trusted_sniff`, `entry_controlled`, `spell_aura`, `entry_aura` | `entry-auras.sql` | ~7 min |
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
