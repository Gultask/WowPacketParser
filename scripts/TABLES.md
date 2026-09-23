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
| `sniff` | file | build, branch, packet counts, clock. Everything else hangs off `sniff_id`. `file_crc32` is what 7-Zip lists, so `ingest-sniffs.ps1` can pass an archive over unopened. |
| `ingest_archive` | archive inside an archive | written by `ingest-sniffs.ps1`, not the parser: nested archives that finished, by the CRC their parent lists |
| `sniff_map` | sniff × map | yield per map, plus the packet census and how many were gated |
| `sniff_coverage` | sniff × capability | what this sniff *could* give up; see below |
| `map_validity` | target × map | which branches' terrain matches 3.3.5, and what rebuilt the rest |
| `creature_spawn` | sniff × creature | position, level, faction, flags. Dead creatures excluded. |
| `gameobject_spawn` | sniff × gameobject | position and rotation quaternion |
| `creature_waypoint` | move order point | the big one — destinations, not pathfinding filler. **No summon gate**; see below |
| `creature_movement` | sniff × creature | movement reduced to a centre and a radius |
| `loot_instance` / `loot_instance_item` | loot opened | empty loots kept on purpose: they are the denominator |
| `creature_spell_cast` | SMSG_SPELL_START | raw; the gap between two is the cooldown observation |
| `spell_target` | sniff × spell × target entry | what an entry-targeted spell actually hit; players are entry 0, `self_hits` counts hits on the caster |
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
| `creature_melee` | sniff × entry × swing state | every landed `OriginalDamage` as a JSON array; auras in the key |
| `creature_armor` | sniff × victim entry × state | clean-hit damage sums; the ratio is armor reduction |
| `creature_xp` | sniff × entry × both levels | kill XP before the rested bonus |
| `creature_stats` | sniff × entry × stat sheet | the owner-only paperdoll: damage range, attack power, stats, all seven resistances |

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

### `creature_waypoint` never learned about summons

`IsTemporarySpawn()` - pets, guardians, totems, anything with `CreatedBySpell` - gates
`CollectCreatureSpawns` and the gameobject collector. `CollectCreatureWaypoints` does not call
it, so a creature the pipeline has already decided is not world content still contributes every
move order it made:

| entry | `creature_spawn` | `creature_waypoint` |
|---|---:|---:|
| Army of the Dead Ghoul (24207) | 0 | 291,967 |
| Sprite Darter Hatchling (9662) | 0 | 126,235 |
| Bloodworm (28017) | 0 | 76,971 |

It stayed invisible while mining was per point, because a pet following a player never walks the
same centimetre twice and nothing it did survived the recurrence test. Phase 4 of
`mine-paths.sql` leans on whole walks, and these surfaced immediately - they clear its
`radius_robust >= points` condition **better than a real one-way route does**, because the pet
goes wherever the player goes and the player covers ground.

They are refused today by phase 4 needing a confirmed *run* as well, and by the anchor rule at
publish. Both are statistical answers to something the pipeline has a categorical rule for. The
fix is the missing call in the waypoint collector, and it needs a re-ingest.

### The same guid can be two creatures

Wood Frog (7550) guid `0016ABED` in sniff 553 holds 592 points spanning x −2868 to 1684 and y
−4458 to 8517 - the width of Kalimdor - on one map, one entry, inside 44 minutes. A frog did not
do that. The server recycled the low guid across despawns as the capture moved, and the parser
keys movement by guid alone, so unrelated creatures merge into one walk.

The visible damage is `creature_movement.radius_robust`, which came out at 9,494 yards for that
frog and carried a pure wander walk through phase 4 into a 1,043 point published route. The
anchor rule catches that particular one. The merge itself is not fixed, and `radius_robust` is
used elsewhere - `build-digest.sql` phase 1b falls back to it for instances with no waypoints.
Phase 4b's `radius > 50` ceiling means the digest already refuses the worst of them for spawn
radii, so this shows up as a route problem rather than a `wander_distance` one.

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

Take the median by **rank**, not by percentile band. `PERCENT_RANK() BETWEEN 0.4 AND 0.6` reads
as a median and is not one: with four pulls the ranks are 0, .33, .67 and 1, so nothing lands in
the band and the answer is NULL; and on a spell cast at 0 ms on most pulls it drifts upward off
the true middle. It cost 636 openers before anyone noticed.

### `creature_spell_cast` needs curating before it can be published

Three things are wrong with the raw table, and none of them is a parse error - the caster gate in
`SniffFile.cs` correctly requires a Creature or Vehicle GUID.

**One cast can emit two `SMSG_SPELL_START` rows.** The first carries no matching `SPELL_GO`
(`completed = 0`) and a second completes within about a quarter second. 12 of the 13 sub-1.5s
consecutive pairs on the Icecrown group carry exactly that 0-then-1 signature. This is what puts
a 247 ms gap where a cooldown should be, and `min_gap_ms` is the number an AC timer is read from.
`curate-spell-casts.sql` drops 10,772 such rows, and with them nearly a quarter of every gap in
the corpus: 3,973,316 down to 3,011,563.

**Some spells are not the creature's.** 48210 Haunt sits on 1,285 entries including Onyxia,
Anub'arak, Thorim and Algalon, and 11,081 of its 11,770 casts are in sniffs named for a warlock.
On Onyxia it fires every ~11 s at 100% completion - the warlock's refresh cycle, not a boss
mechanic. Others are real creature casts that are still not abilities: 1604 Dazed is the melee
proc (1,966 entries), 29266 Permanent Feign Death is a corpse prop and the single largest spell
in the corpus at 309,544 casts, 18950 is a passive.

**670 spell ids are absent from the 3.3.5a DBC** - 101,511 casts, 1.7% of the table. Modern
internal ids the Classic client emits (378027, 414266, 413265 and friends). Whatever they do,
they cannot go to a 3.3.5a core.

**There is no automatic test for the second one.** Entry breadth does not separate it: Enrage is
on 163 entries, Shoot 139, Thrash 104, Cleave 102, and all four are genuine. `SpellFamilyName` is
0 for Haunt as much as for creature spells. `sniff.sniffer` is empty on every row in this corpus.
wotlkmangos has no `skill_line_ability`. So `sc_exclude` is hand-written with a reason per row and
`entries_sharing` ships as a column - a number in the hundreds is a reason to look, not to drop.

### What `sniff_creature_spell` says that `st_timer` does not

`shape` is the column to read first. At accuracy 2 the corpus holds 2,515 `conditional` rows
against 1,545 `fixed`: **most well-sampled creature spells do not fit a min/max timer at all.**
They wait on something the packets do not carry - health, range, a friendly target, an interrupt
window - so the observed gap measures the fight, not the spell. For those, publish the lower
bound and the initial timer and let AC gate the rest with an event.

`opener` is the fourth shape: median initial under 500 ms over five or more pulls. 715 of them at
accuracy 2. Those are `SMART_EVENT_AGGRO`, not a timer.

The companion view `sniff_creature_smartai` turns a row into a `smart_scripts` proposal, with
`ac_has_it` flagging whether AzerothCore already scripts that spell on that creature. Of what it
offers, 2,073 accuracy-2 proposals across 981 entries are things AC does not have.

`sniff_coverage` is the one that is easy to underrate. A sniff that produced no loot because its
build has no `SMSG_LOOT_RESPONSE` and a sniff that produced no loot because the player looted
nothing both look like zero rows afterwards. The difference is only knowable at parse time, so
it is recorded then: `status` is `ok`, `empty` or `unsupported`, and `collector_version` makes
the sniffs that predate a collector improvement selectable as a re-parse work list.

### Damage and armor both come out of `OriginalDamage`

`SMSG_ATTACKER_STATE_UPDATE` carries the swing twice: `OriginalDamage` before the victim's
armor, block, absorb and resist, and `Damage` after. So a creature's own damage range reads
straight off its hits on anything, and on a hit with none of the other mitigations the ratio of
the two is the victim's armor reduction.

`scripts/melee-multiplier.py` divides each hit by AzerothCore's formula at `DamageModifier` 1
(`damage_base` + AP/14, times attack time) and reads the multiplier off both ends of the range.
**Normal mobs land at 1.00 to within 3%, so `creature_classlevelstats` already is the 1.** Where
the top and bottom ends disagree, something the aura filter missed is in the sample (`mixed`).

The attacker's auras are in `creature_melee`'s key because Enrage, Frenzy and Demoralizing Roar
are on for part of a fight: before they were, a third of the well-sampled entries read as a
mixture. The script keeps only states with no aura of a damage or haste type in 3.3.5 Spell.dbc.
`owner` marks player summons, whose damage follows their owner's stats; the script drops them.

`scripts/creature-armor.py` inverts the 3.3.5 armor formula. It checks out on the player's own
armor, which is sent: 72% of rows within 1%. Creature attackers measure creature armor with no
armor penetration in the way; a player's reading can only be low. Treat armor as indicative -
the inversion multiplies a 0.005 error in the reduction into about 2% of armor.

`scripts/xp-modifier.py` does the same for `ExperienceModifier` with `creature_xp`. Kill XP runs
at a player bonus - heirlooms and a +50% event put the druid levelling set at 1.70 of AC's
formula - so the most common ratio over non-elite kills, per sniff and player level, is the 1.
After that 211 of 231 WotLK entries agree with AC, and the ones that do not tend to carry the same
scaling in their damage: Heckling Fel Sprite 0.40 XP and 0.42 damage, Sapphire Hive Drone 0.50
and 0.51. The query response's `health_modifier` matches the damage multiplier less often than
AC does - 367 of 502 normal mobs against 439 - so it is a hint, not a stand-in.

All three are keyed by entry with a surrogate `id`, not by guid: the natural key ran through a
512-character aura list that InnoDB copied into every index, at 500 to 850 bytes a row.

### `creature_stats` gives the multiplier without fitting anything

The server sends a unit's stat sheet - `MinDamage`/`MaxDamage`, attack power, the five stats and
their buffs, armor and the six resistances - only to whoever owns, charms or rides it. For a
creature that means pets, guardians, quest vehicles and mind-controlled mobs. **TBC Anniversary
(2.5.5 and 2.5.6) sends it for every creature in sight**; the 1.15.7, 3.4, 4.4.1 and 5.5.0
samples checked sent it to the owner only.

The range is the server's own arithmetic, so `(max - min) / attack time` is
`damage_base × DamageModifier / 2` exactly, with no attack power in it. `scripts/creature-stats.py`
reads the modifier off that, tries each `damage_base` column, and keeps the one whose modifier
also reproduces `min`. The owned sheets matched AzerothCore's `creature_classlevelstats` (stats,
armor, attack power, `damage_exp2`) to the digit. Where they differ, Blizzard's modifiers come out
as round numbers - Gymer 10 against AC's 1, Wyrmrest Vanquisher 4 against 7.5, Theramore Guard 2
at every level from 53 to 57 - and TBC Anniversary trainers and vendors sit at 0.5. A
`min_check` of `ap differs` means the creature's attack power is not AC's; the modifier is still
exact, but the base column is AC's guess. TBC Anniversary armor runs 1-2% above AC's `basearmor`
across the board, so treat `armor_modifier` near 1.01 as 1.

A create block whose holder lost its `UpdateObject` to a spline (see `creature_waypoint`) leaves
the sheet's first row partial: empty slots in `stats` were never sent, not zero.

## 2. Script-derived — rebuildable, and dropped by their own script

Re-running the script drops and recreates these, so losing them costs only time.

| tables | built by | cost |
|---|---|---|
| `wp_point`, `wp_step`, `wp_step_batch`, `wp_node`, `wp_edge`, `wp_edge_raw`, `wp_level`, `wp_stack`, `wp_zobs`, `wp_step_ok`, `wp_walk`, `wp_walk_xy`, `wp_edge_walk` | `mine-paths.sql` | ~2 h. `wp_point` is 10.5 GB and `wp_step` about 4 GB - budget the disk, not just the time. |
| `path_summary`, `path_point` | `mine-paths.sh` (via `chain-paths.py`) | 11 s once the graph exists |
| `dg_spawn`, `dg_est`, `dg_approx`, `dg_fixed`, `dg_inst_rad`, `dg_rad_key`, `dg_path_span`, `dg_route_pt`, `dg_state` | `build-digest.sql` | ~4 h |
| `co2_recovered`, `co2_recovered_build` | `recover-co2.sql` | ~1 min. **Additive, not dropped** - they are the record of which rows it changed, and deleting them loses the ability to undo it. |
| `entry_value`, `entry_value_best`, `spell_initial_gap`, `spell_initial_timer`, `waypoint_segment_speed`, `entry_travel_mode` | `entry-values.sql` | minutes |
| `aura_trusted_sniff`, `entry_controlled`, `spell_aura`, `entry_aura` | `entry-auras.sql` | ~7 min |
| `st_gap`, `st_timer` | `spell-timers.sql` | ~5 min |
| `entry_equip`, `entry_model`, `entry_vendor`, `entry_quest_item`, `entry_action_spell`, `entry_gossip_menu`, `menu_text`, `menu_option`, `text_line`, `at_teleport`, `entry_spell_target` | `roll-up-tables.sql` | ~1 min |

### `close_seq` is an observation, not a shape

`path_summary.close_seq` is the seq the route's LAST point leads back to, or -1. It says an edge
was walked. It does not say the route is a circuit, and the difference costs points:

| `close_seq` | ring size | what it is |
|---|---|---|
| 0 | all of it | a plain ring |
| between 1 and n-3 | n - close_seq | walks in along a tail, then circles |
| n-2 | 2 | **not a loop** - the creature turned round at the end of a line |
| -1 | - | never came back |

`n-2` is the trap and it is not a rare corner: 13,139 of 25,318 closed routes, more than half.
Plagued Fiend (31150) has one that is six points in a straight line west to east with close_seq 4,
so the "ring" is its last two points and the other four are filed as an approach. Path ids are not
stable across a re-mine, which is why that route is named by its shape and not by its id. Any
consumer
that keeps the ring and drops the approach publishes two points of a six point route.
mod-sniff-diff did, until `routes/whole-route-on-add`. A ring needs three points to be a ring;
below that the closing edge means the same thing as retracing an open route, which is what it
should be turned into.

### Eleven tables were collected and never read

Until `roll-up-tables.sql` existed, eleven parser-written tables had no consumer anywhere in the
repository: 4.2M rows, about 860 MB, rewritten on every ingest and never turned into an answer.
They are not hard problems the way `stand_state` is — they mostly want the sniff dimension
collapsed and the observations counted. The collapse is large because the raw tables record one
row per sniff per sighting:

| source | raw rows | rolled up | ratio |
|---|---:|---:|---:|
| `creature_equip` | 2,133,503 | 11,337 | 188:1 |
| `creature_template_model` | 1,624,189 | 35,424 | 46:1 |
| `npc_vendor` | 126,637 | 39,411 | 3:1 |
| `npc_text` | 90,824 | 22,480 | 4:1 |
| `creature_gossip` | 31,345 | 4,036 | 8:1 |
| `gossip_menu` | 25,655 | 5,839 | 4:1 |
| `gossip_menu_option` | 21,682 | 3,596 | 6:1 |
| `creature_template_spell` | 14,244 | 1,343 | 11:1 |
| `creature_quest_item` | 12,519 | 5,342 | 2:1 |
| `spell_target` | 2,052,019 | 59,832 | 34:1 |
| `areatrigger_teleport` | 1,862 | 135 | 14:1 |

Every rolled-up table keeps a `sniffs` count. That column is the point: a vendor item seen by
one capture out of twenty is conditional stock, not standard stock, and nothing else in the data
can tell those apart.

### `npc_spellclick` has never had a row, and never could

Zero `ok` sniffs out of 4,511 — 2,757 `empty` and 1,754 `unsupported`. This is not missing data
in the captures, it is a hole in the parser, and the same hole as divergence 3 in the fork notes.

`CollectNpcSpellClicks` pairs `Storage.NpcSpellClicks` (the click) with `Storage.SpellClicks`
(the cast it produced) and needs both. `Storage.NpcSpellClicks` is filled by `V3_4_0_45166`,
`V4_4_0_54481` and `V5_5_0_61735`. **`Storage.SpellClicks` is filled by none of them** — only by
the legacy core `SpellHandler` and by `V4_3_4_15595` / `V5_4_8_18291`. So on WotLK Classic, Cata
Classic, MoP Classic, TBC Anniversary and Classic Era the inner loop never runs.

Fixing it needs a parser change and a re-ingest, so `roll-up-tables.sql` deliberately writes no
rollup for it: an empty table would suggest the sniffs lack the data rather than the parser.

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
