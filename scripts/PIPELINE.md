# Rebuilding everything from scratch

What runs, in what order, and what each step destroys. `RELEASE.md` covers the published
artifacts; this covers how they are made.

This file exists because two steps had no script at all. `acore_world.sniff_creature_path` was
published by a statement typed at a prompt and saved nowhere, and the export that carries
`mine-paths.sql` into `chain-paths.py` was the same. Both were reconstructed on 2026-08-23 and
are now `build-digest.sql` phase 6 and `mine-paths.sh`. Anything typed at a prompt from here on
belongs in a script before the session ends, because a year from now nobody will remember it.

## Order

```
  ingest-sniffs.ps1        .pkt / .7z  ->  wpp_ingest              hours to days
        |
        v
  recover-co2.sql          rebuilds create_type for silent clients          ~1 min
        |
        v
  roll-up-tables.sql       the eleven tables that had no digest              ~1 min
        |
        v
  mine-paths.sh            creature_waypoint -> path_summary, path_point   ~1 h 35 m
        |
        v
  build-digest.sql         -> acore_world.sniff_creature_spawn
                           -> acore_world.sniff_creature_path      ~4 h
        |
        v
  publish-loot.sql         -> acore_world.sniff_loot/gameobject/names   ~2 min
        |
        v
  make-release.sh          -> 9 per-table files, into the module tree   ~1 min 20 s
```

The three arrows that matter:

- **`mine-paths.sh` must run before `build-digest.sql`.** The digest reads `path_point` twice:
  phase 4c decides whether a spawn patrols by how near it sits to a route, and phase 6 copies
  the routes across. Run them the other way round and both describe the previous mining.
- **`recover-co2.sql` must run before `build-digest.sql`.** It edits `creature_spawn.create_type`
  in place, and the digest reads that column to choose both the published position and the
  accuracy. Run it after the digest and the corpus is right while everything published from it
  is a round behind.
- **`publish-loot.sql` is independent.** It reads `loot_instance` and `gameobject_spawn` and
  touches nothing the digest uses, so it can run whenever.

## Steps

### 1. Ingest

```
.\ingest-sniffs.ps1 -Path 'G:\sniff-storage' -Database wpp_ingest2 -MapPolicy wotlk -Threads 4 -LogFile 'G:\ingest.log'
```

Walks folders for `.pkt` and archives, runs WowPacketParser with DumpFormat 17 (straight to
MySQL, no intermediate files), extracting one archive at a time so the whole store does not need
79 GB free. Sniffs already loaded are skipped by content hash, so it is safe to stop and restart.

`-MaxContentExpansion` filters by content rather than build number, because Burning Crusade
Classic carries a very high build. It drops whole sniffs, so prefer `-MapPolicy` for a
WotLK-and-below corpus: that keeps the parts of a Cataclysm or Shadowlands capture standing on
ground a 3.3.5 server still has, and throws away only the rest.

#### The map gate is what makes Cataclysm and later affordable

`-MapPolicy wotlk` keeps a map only if **both** are true: it is present in 3.3.5a `Map.dbc`
(read out of a client install, because AzerothCore ships `map_dbc` empty), and the sniff's own
branch is early enough that it is looking at the 3.3.5 version of it. Everything else never
reaches a handler.

The second half is not optional, and leaving it out was a real bug. Kalimdor is map 1 in 3.3.5
and map 1 in Cataclysm, and they are not the same Kalimdor. A gate that only asked whether the
map id existed let a 4.4.0 levelling capture through: **320,000 packets, 3,741 spawns and 51,264
waypoints of rebuilt Kalimdor**, plus its rewritten gossip text. The rule is the same one
`map_validity` states - one is about cost and the other about what may be used, and both want
the same answer - so the gate now applies it directly. Thirteen maps have a cut-off: 0, 1, 33,
36, 109, 309 and 568 stop being 3.3.5 after Cataclysm; 189, 289 and 389 after Mists; 47, 48 and
229 after Warlords. MoP is the latest cut-off expressible, because `ClientBranch` stops there
and files everything from Warlords on under `Retail` - enough for every rebuild known to need
gating.

With that in, the 4.4.1 capture measured above goes from 93.2% dropped to **100.0%** - it only
ever stood on Firelands and rebuilt old world, so it has nothing to give a 3.3.5 server. A 3.4.3
WotLK capture is still untouched, keeping all eight of its maps including 0 and 1.

The numbers that forced it, measured on the 3,101 sniff corpus:

| | sniffs | packets |
|---|---:|---:|
| touch only instance maps | 19 | 350,939 |
| mix world and instance | 667 | 553,722,129 |

Only 19 sniffs are pure instance content, so **excluding by file name is worth nothing** - but
the mixed ones hold **71.7% of the whole corpus by packet count**. The saving has to come from
inside the file or not at all. Measured per sniff afterwards:

| capture | dropped | maps |
|---|---:|---|
| 4.4.1 Cata Classic | 100.0% (2,842,004 of 2,842,330) | Firelands (720), rebuilt 0/1/36 |
| 9.0.2 Castle Nathria | 100.0% (720,704 of 720,807) | 2296, 2222 |
| 3.4.3 WotLK Classic | 0% | every map it visited exists in 3.3.5 |

That last row is the point: the gate is a no-op on the branches already being ingested, so it
cannot disturb them.

**How it works.** A capture is a linked list, not an array - records are variable length, so
there is no seeking to packet N. But the map changes only a couple of dozen times in a
multi-hour capture (38 map-defining packets in 2.8M), so those three opcodes are parsed in file
order on the reader thread and every other packet is stamped with the map in force when it
arrived. `Settings.MapFilters` is a different thing entirely: it drops rows on the way out of
the SQL builders, after everything has already been parsed, and saves no time.

**What is never gated.** Opcodes whose handler advances the per-connection zlib stream, because
skipping one desynchronises it and would quietly corrupt every compressed packet after it -
including the ones on the maps being kept. They cost nothing: a 2.8M packet 4.4.1 capture holds
none of them at all.

**Where it does not fire.** Classic Era 1.15.x resolves no map, so nothing is gated on those
sniffs. That is the safe failure - it parses everything rather than dropping the wrong thing -
and it costs nothing here, because every Classic Era map exists in 3.3.5 anyway. `sniff_map`
records the packet census per map, so a build where the gate silently stops working shows up as
a sniff with no attributed packets.

**Gating is not the same as validity.** The gate is about cost; `map_validity` is about whether
the data can be used. It now covers all 135 maps: a map is usable from the branch of the
expansion that introduced it onward, until something rebuilt its terrain. Cataclysm reshaped the
old world plus Deadmines, Shadowfang Keep, Zul'Gurub, Zul'Aman and the Sunken Temple; Mists
rebuilt Scarlet Monastery, Scholomance and Ragefire Chasm; Warlords did Blackrock Spire,
Blackfathom Deeps and Razorfen Kraul. Everything else took minor adjustments at most - which is
why a Cataclysm capture of UBRS or Zul'Farrak, or a Shadowlands one of Outland, is good evidence
for 3.3.5, and a Cataclysm capture of Deadmines is not.

Map 229 is Lower and Upper Blackrock Spire under one id and Warlords rebuilt only the upper
half, so the whole map is cut at MoP; the Cataclysm UBRS captures are still kept, which is where
that evidence comes from.

#### What one sniff now yields

Beyond spawns, waypoints and loot: `creature_spell_cast` (every SMSG_SPELL_START by a creature,
raw), `spell_target`, `spell_destination`, `creature_equip`, `creature_aura`, `gossip_menu`,
`gossip_menu_option`, `npc_text`, `areatrigger_teleport`, `npc_vendor`, `npc_spellclick`,
`creature_template_spell`, `creature_quest_item`, `creature_gossip`, `creature_value` and
`creature_aggro`.

Run `entry-values.sql` afterwards to roll the per-guid values up to the entry - `entry_value`,
`entry_value_best` - and to derive initial cast timers from the pulls. It also strips the runtime
bits out of `unit_flags` on the way through; see `TABLES.md`.

Then `entry-auras.sql`, which is what makes `creature_aura` usable. Read raw the table is a
combat log: only 7.4% of its entry-and-spell pairs are the creature's own aura, and the
widest-spread of them are one warlock's DoTs following them across 1,989 entries. The script
sorts them and only the `addon` verdict is publishable.

```
mysql -u root -p wpp_ingest2 < entry-values.sql
mysql -u root -p wpp_ingest2 < entry-auras.sql
```

**A trap worth knowing about.** Most of those last six read from `Storage` bags that the parser
switches off unless their `SQLOutput` flag is set - `StoreBag.Add` is a no-op when disabled - and
the ingest sets none of them, so the bags were silently empty. Database mode now turns on exactly
the five outputs its collectors read (`creature_template`, `creature_template_gossip`,
`creature_spell_list`, `npc_vendor`, `npc_spellclick_spells`) and no more, because enabling the
lot would collect quest, item and hotfix data nothing here reads.

**And a second one.** `creature_template_spell` is the action bar the server sends for a
controlled creature, and three branches spell it three ways: WotLK Classic fills
`CreatureTemplateSpells`, Cata Classic fills `CreatureSpellLists`, and the legacy handler fills
`SpellsX`. The collector reads all three. Reading one would have returned nothing for two thirds
of the corpus while looking like it worked.

`scripts/TABLES.md` says what each
one holds and which of the old database's 53 tables are worth keeping.

Loot refuses to run on Mists and later: area looting lets one response cover several corpses, so
the collector's one-loot-per-owner model does not hold and it would record confident nonsense.
The `sniff_coverage` row says so rather than leaving a silent zero. Cataclysm loot parses fine -
verified on 4.4.1, which reports `empty` rather than `unsupported`.

### 1b. Recover the CreateObject2 flag

```
mysql -u root -p wpp_ingest2 < recover-co2.sql
```

Run this after every ingest, before anything reads `creature_spawn`. It is fast and it is a
no-op when there is nothing to fix.

The Anniversary client line - TBC 2.5.5 / 2.5.6, builds 65417 and up - does not send
`UpdateType 2` at all, so every spawn arrives as CreateObject1. That was worth 258,230 spawn
rows and 149,528 gameobject rows across 182 sniffs with not one CO2 among them, including whole
zones captured deliberately for their spawn points. The parser was reading the byte correctly;
the client had stopped sending it. Confirmed by the fact that `GetVersionDefiningBuild` routes
those builds to the same module as MoP Classic, which still produces CO2 at build 64857 through
that identical code path.

`WowPacketParserModule.V5_5_0_61735/Parsers/UpdateHandler.cs` now carries the same
`TreatAsCreateObject2` reconstruction that upstream added to V11 and V12, gated on build 65417
so MoP Classic keeps its real flag. **That fixes new ingests only** - this script is what fixes
sniffs already in the corpus, and the two agree because both read
`TreatAsCreateObject2Tolerance`, set to 2 in `App.config`.

It picks its targets by client build, never by individual sniff: a build qualifies on having a
thousand-plus spawn rows and exactly zero CO2, then is remembered in `co2_recovered_build` so
later sniffs on the same build are caught too. Do not be tempted to widen this to "any sniff
with no CO2" - 244 sniffs here have 200+ spawn rows and no CO2 for the ordinary reason that
nothing respawned in view, and most of them are WotLK, where capture works fine.

Every changed row is listed in `co2_recovered` first, so it is reversible. The script's header
carries the validation and the tolerance table.

### 1c. Roll up the tables that had no digest

```
mysql -u root -p wpp_ingest2 < roll-up-tables.sql
```

Eleven parser-written tables were read by no script at all - collected on every ingest, 4.2M
rows, about 860 MB, never turned into an answer. Most needed no inference, only the sniff
dimension collapsed and the observations counted, so `roll-up-tables.sql` does that and keeps a
`sniffs` column throughout: one capture seeing a thing ten times is far weaker evidence than ten
captures seeing it once, and a bare `DISTINCT` throws exactly that away.

Two of its outputs are gates rather than rollups, and both are argued in the script header:

- **`entry_spell_target`** drops the 94.6% of `spell_target` whose spell has no entry-based
  implicit target in Spell.dbc. 2,052,019 rows become 59,832. Spells absent from 3.3.5 Spell.dbc
  are kept and marked `unknown_spell` rather than dropped - there are 1,401 of them, all Cata and
  later, and this database cannot say what they target.
- **`at_teleport`** gates on the pairing delay, not on collapsing each trigger to one
  destination. The collector allows 30 s for a loading screen, which is long enough to catch the
  player's next hearthstone; single-destination triggers average 3.9 s of delay and
  multi-destination ones 16.6 s. After a 3 s gate, 116 of 125 triggers resolve to one
  destination and **nine keep more than one** - those are real. A trigger can have a conditional
  destination, so forcing one row per trigger would delete good data to tidy up an artefact.

`creature_spell_cast` is the twelfth table with no published digest, but it already has a
script - `spell-timers.sql`, step 3b - which had simply never been run against this corpus.

`npc_spellclick` has no rollup, deliberately: the source table is empty and always has been.
See `TABLES.md`.

### 2. Mine the routes

```
./mine-paths.sh
```

Drops and rebuilds `wp_point`, `wp_node`, `wp_edge`, `path_summary` and `path_point`. Two full
scans of `creature_waypoint` (33.4M rows, 8.4 GB); the last run took 110 minutes - 22 in phase 0,
78 in phase 2.

`LOAD DATA LOCAL` needs `local_infile` on **both** sides. The client gets `--local-infile=1`, and
the script now runs `SET GLOBAL local_infile = 1` before the load rather than assuming the server
is still set from last time - the global does not survive a mysqld restart, and a 110 minute mine
that dies on its last step is an expensive way to discover that.

#### Phase 0 throws the server's own pathfinding away

Most positions in a capture are not authored data. A multi-point `SMSG_MONSTER_MOVE` carries the
path the server *computed* - the navmesh corridor it solved, smoothed and snapped to the terrain
under it. That is the server's code talking, not its database, and it is not reproducible: a
different mmap build or a different terrain height gives a different string of points for the
same two waypoints. Mined as waypoints it publishes a straight line of invented points a yard
apart and buries the two real ones at its ends.

Dread Tactician (16959) was the specimen. One packet, 24 points, one timestamp, one
`move_time_ms` of 14158, 32.6 yd dead straight - walked length over chord 1.021, every edge
1.00x or 2.02x a 1.023 yd quantum. It published as 23 of that path's 24 edges. The single real
edge was the 27.51 yd hop off its end, and a different capture entirely reached that same point
as a one-point move order, which is what proves the endpoint authored and the interior computed.

So phase 0 keeps `point_index = segment_points - 1` - the **destination** of each move order,
which is the whole row for the ordinary single-point case - and discards the interior.

**The exception is anything not walking on the ground.** A creature in the air is not pathfound
and not snapped, so its CreateObject spline *is* the authored route, and those keep every point.
The test is `spline_flags & 0x400`, confirmed against the corpus rather than read off an enum,
because neither `SplineFlag` nor `SplineFlag422` lines up with these builds. Grouping every
multi-point creation spline by that bit returns **no rows without it** - Spire Frostwyrm,
Monstrous Kaliri, Spotted Hippogryph, Bat Rider Guard, Cosmetic Toy Plane. Read it as "not
snapped to the ground" rather than strictly flying: it is set on Orca too, and that is correct
for this purpose. If a ground creature ever lands in that branch, phase 0's own report shows it.

What it cost, measured across the change:

| | before | after | |
|---|---:|---:|---:|
| raw points | 33,354,096 | 30,718,272 | −7.9% |
| `wp_node` | 918,817 | 821,847 | −10.6% |
| `wp_edge` | 500,764 | 403,200 | **−19.5%** |
| routes built | 60,418 | 51,202 | −15.3% |
| published (>=4 pts) | 36,756 | 30,151 | −18.0% |
| routes made **entirely** of spline edges | 5,507 | 352 | −94% |

Note the shape of that: only 7.9% of rows go, but 19.5% of confirmed edges do. Interior points
sit in a minority of segments and generate edges out of all proportion to their number, because
every consecutive pair inside one packet is an edge that the 2-sniff test then confirms against
*itself* on the next capture of the same spline.

The 352 that remain, and 894 more with some spline edge, are the air paths kept on purpose.

**1,521,167 points are kept as air paths.** A run where that number collapses means the flying
test stopped matching - check `spline_flags` before believing the route counts.

#### What confirms a node or an edge: recurrence, not independent captures

Changed 2026-08-28, and it is the largest single swing this pipeline has had.

A node is a position visited in at least two separate **move orders**; an edge is an ordered pair
of nodes walked at least twice. Neither test asks who was watching. Both used to require two
**independent captures**, and that quietly deleted every route a single player recorded
completely - which is most of the world outside the levelling corridors, because a route nobody
else walks past has no second capture coming and never will.

The test exists to separate authored waypoints from random wander, and recurrence is what does
that work; independence was never the active ingredient. `MOTION_RANDOM` picks a fresh
destination inside the wander radius on every hop, so the odds it lands on the same centimetre
twice are nil, and the odds it picks the same *ordered pair* twice are nil squared. A second lap
proves the pair authored exactly as well as a second capture does. Independence is better
evidence that the spawn is *live*, which is a different question - so `n_sniffs` is kept, still
travels to the published `edge_sniffs`, and still decides which of two overlapping routes the
chainer prefers. It just no longer decides what exists.

The node test counts `(sniff_id, guid, segment_id)`, not `(sniff_id, guid)`. Without the move
order in the key, two points of one creation spline landing on the same centimetre would confirm
each other, and the rule would be admitting the server's own pathfinding by the back door -
exactly what phase 0 exists to throw away.

| | independent captures | recurrence | |
|---|---:|---:|---:|
| `wp_node` | 821,847 | 1,737,057 | **+111%** |
| `wp_edge` | 403,200 | 534,794 | +32.6% |
| routes built | 51,202 | 67,760 | +32.3% |
| published (>=4 pts) | 30,151 | 40,787 | +35.3% |
| published points | 271,539 | 379,851 | +39.9% |
| closed loops | 8,543 | 10,267 | +20.2% |
| entries with a route | 3,699 | 4,308 | +16.5% |
| average route length | 9.01 | 9.31 | +3.3% |
| spawns flagged `patrols` | 58,388 | 64,166 | +9.9% |

**Read the average before believing the rest.** Routes got longer, so the new edges are extending
chains rather than splintering them; a threshold change that fragmented the corpus would show up
there first. Gap artifacts fell as a share too, 28.9% -> 26.2%, because a denser graph leaves
fewer skip-edges to strand.

**98.9% of the old corpus survived** - 268,570 of 271,539 points reappear at the same
coordinates. The 1.1% that moved is re-chaining churn spread over 591 entries, and every one of
the worst-hit entries gained points overall. Twelve entries lost their last published route, all
of them 4-13 points, and ten of the twelve still build routes now: 3-point ones, dropped by the
publication floor rather than by this rule.

**`path_id` is not stable across releases, and this run proved it.** The chainer numbers routes
sequentially, so a different node set renumbers all of them - zero of the 30,151 old ids point at
the same route afterwards. Nothing keyed to a `path_id` from an earlier dump survives a re-mine,
including specimens quoted in these docs. Key on coordinates.

**26.7% of published points now carry `edge_sniffs = 1`.** That is the quarter of the corpus
this change added, and filtering it back out reproduces the old dataset almost exactly.

**What it does not reach.** A route walked once *per creature* still cannot be confirmed, because
nothing about it recurs. Deserter Agitator (23602) is the specimen: 6 captures, 55 guids, 260
points, and its longest traces hold 22 distinct positions in 22 points - one pass each, no lap.
It recovered from 15 nodes to 36, but 30 of those 36 came from two different Agitators standing
on the same centimetre rather than from anyone doing a second lap, and 144 of its 180 positions
were walked exactly once ever. Cross-creature byte-matching is the only evidence such an entry
can produce, and it is thin by construction.

**Chaining alone is 11 seconds.** `wp_node` and `wp_edge` are what cost the 95 minutes, and
`chain-paths.py` only reads their exports, so a change to how routes are assembled - closure
handling, gap limits, MIN_POINTS - is re-run from `.paths/nodes.tsv` and `.paths/edges.tsv`
without touching the mining:

```
python chain-paths.py .paths/nodes.tsv .paths/edges.tsv .paths/path_summary.tsv .paths/path_point.tsv
```

then the LOAD DATA block from this script, then phases 4c-6 of the digest.

Prints its own node, edge and path counts at each phase. A run that ends with far fewer
confirmed edges than the last one means the corpus changed, not that the script improved.

It also prints how many routes came out **covered by a longer one**, split by whether the covering
route belongs to the same entry. Both are expected and they mean opposite things:

- **same entry** is a gap artifact. `walk()` takes the strongest edges first, so a spurious A→C
  edge left behind by a capture that missed B fits no chain, and the next pass starts a fresh
  route from the leftovers — a scrambled permutation of nodes the real route already owns.
  Published entry 27500 is the specimen: one seven-point ring at 44 sniffs an edge, plus copies
  of four and five of its own points at 6-7. **10,690 of 40,787 published routes**, and each is
  far weaker than the route covering it - best edge 6.1 sniffs against 40.4, 11.4 traversals
  against 190.7.
- **different entry** is several creatures walking one authored circuit, which is ordinary. Three
  Bloodfury harpy entries share one Stonetalon loop and only 4027's capture of it closes.
  **1,541 routes.**

`covered_by` records which route, and nothing is filtered on the way in — the two readings want
opposite handling, so the policy belongs to whoever is asking. A large swing in either number
between runs is worth reading before shipping; a large swing in the *same-entry* one especially,
since that is the corpus getting patchier rather than the world changing.

**Phase 0 did not fix this, and was never going to.** Gap artifacts fell 9,077 → 8,720, but as a
*share* of published routes they rose, 24.7% → 28.9%, because the denominator shrank faster. The
skip-edge mechanism is a missed observation, not a spline, so the two problems are independent
and `covered_by` remains the thing that handles this one. The recurrence change did move it -
26.2% at 10,690 routes - and it is the one lever that has, because it fills in the missed
observations themselves rather than reclassifying what they left behind.

Coverage is judged by distance, not by coordinate equality. A float round-trip moves a node about
a centimetre - two published routes over one waypoint have held `2628.18` and `2628.19` for it -
and rounding to a coarser grid only moves the boundary, which is the argument `mine-paths.sql`
already makes about Z. `COVER_YD` is a quarter yard, inside the jitter and nowhere near a real gap
between waypoints.

### 3. Build and publish the digest

```
mysql -u root -p wpp_ingest < build-digest.sql
```

Six phases, from `creature_spawn` and `creature_movement` to the two `acore_world` tables the
module reads. Roughly four hours end to end.

Only phases 4c, 5 and 6 read the routes, so re-chaining does not need the whole thing. Cut the
file from `DROP TABLE IF EXISTS dg_route_pt;` to the end, drop the `ALTER TABLE dg_spawn ADD
COLUMN` line (those columns already exist), keep the session settings from the top, and run
that: 93 seconds against four hours. Everything before phase 4c depends only on `creature_spawn`,
`creature_movement` and `creature_waypoint`.

### 3b. Derive the spell timers

```
mysql -u root -p wpp_ingest2 < spell-timers.sql
```

Turns raw `creature_spell_cast` rows into `st_timer`, one min/max pair per creature entry and
spell. AzerothCore models a spell as a min and a max timer while retail uses a fixed cooldown
plus a per-update chance, so the pair is an approximation - the script's header says which
quantiles it picks and why the raw extremes are not the ones to publish.

It also classifies every row as `fixed`, `conditional` or `sparse` from the spread between p10
and p75. The min/max model only describes a spell whose sole gate is its cooldown; an interrupt,
a heal or a positional attack fires when its CONDITION occurs, so the observed gap measures the
fight rather than the spell. Measured over this corpus the separation is stark - average spread
1.6 for `fixed`, 133.2 for `conditional`. For a conditional spell publish only the lower bound:
p10 is the floor the cooldown imposes, and the initial timer from `entry-values.sql`, which
measures aggro to first cast, says more than any repeat interval does.

### 3c. Curate the casts and publish them

```
mysql -u root -p wpp_ingest2 < curate-spell-casts.sql
```

`st_timer` is the working table; this is the one that leaves the database. It strips three things
the raw casts carry, and the script header argues each of them:

- **Doubled starts.** One cast can emit two `SMSG_SPELL_START` rows, the first with no matching
  `SPELL_GO`. 10,772 rows, and they are what puts a 247 ms gap where a cooldown should be. Nearly
  a quarter of all gaps go with them - 3,973,316 down to 3,011,563.
- **Spells that are not the creature's.** Five, by hand, in `sc_exclude` with a reason on each.
  48210 Haunt is the big one: 1,285 entries including every raid boss, and 94% of its casts are
  in sniffs named for a warlock. It is the sniffer.
- **670 spell ids absent from the 3.3.5a DBC** - modern internal ids the Classic client emits.

There is no automatic player-spell test and the script does not pretend otherwise. Entry breadth
does not work (Enrage is on 163 entries and is real), `SpellFamilyName` is 0 for Haunt as well as
for creature spells, `sniff.sniffer` is empty for every row, and wotlkmangos has no
`skill_line_ability`. `entries_sharing` is published as a column so a reviewer can see a suspect
instead of the script having silently dropped it.

Two objects come out:

- **`acore_world.sniff_creature_spell`** - one row per entry and spell, initial timers beside
  repeat timers, `accuracy` on the usual 2/1/0, and `shape`. Read the shape split before trusting
  a timer: at accuracy 2 there are 2,515 `conditional` rows against 1,545 `fixed`. Most
  well-sampled spells do not fit a min/max pair at all.
- **`acore_world.sniff_creature_smartai`** - a view. `WHERE entry = 30989` gives that creature's
  proposal with a paste-ready `smart_scripts` tuple and `ac_has_it` saying whether AzerothCore
  already scripts that spell there. It offers accuracy >= 1 only.

**Avenger's Shield on 30986 is the regression check.** AC scripts it as `SMART_EVENT_AGGRO` and
the sniff agrees independently - 41 pulls, median 0 ms. If the rebuilt table does not call it
`opener`, the median or the classifier has drifted. It caught exactly that once already: a
`PERCENT_RANK BETWEEN .4 AND .6` median put it at 524 ms and suppressed 636 openers corpus-wide.

### 4. Publish the loot and gameobjects

```
mysql -u root -p wpp_ingest < publish-loot.sql
```

**Six checks at the end must read zero.** They are listed in `RELEASE.md`; two are the privacy
checks and the other four are pivot-loss checks.

### 5. Cut the bundles

```
./make-release.sh
```

Writes nine files - one per table, plus the view - straight into
`modules/mod-sniff-diff/data/sql/db-world/base/`, re-checking four of the gates first, because
a screenful of output that nobody read is not a gate. 145.8 MiB in total, the largest being
`sniff_loot_set.sql` at 60.0 MiB.

That directory is what AzerothCore's updater scans, so the same nine files are both halves of
the release: they install with the module, and they are the standalone download.

One file per table follows AzerothCore's own convention and buys per-file hashes, so re-mining
just the routes reapplies 16 MiB instead of all 146.

Then it loads all nine into a scratch database **in updater order** and counts the rows back.
That step is not ceremonial - it catches three things nothing earlier does: creates that
disagree with their data, the `sniff_loot` view applied before its tables, and a `DEFINER` the
target server cannot grant. The first of those is exactly how the previous hand-maintained
schema file failed, silently, for as long as it existed.

## Irreversible steps

### distill-waypoints.sql has already been run, and it cost something

It deleted 12,844,552 waypoints belonging to 106,437 instances - creatures with no authored
spline, a radius under 30 yards, and 40 or more recorded destinations. `creature_waypoint` went
from 46.2M rows to 33.1M.

The reasoning was sound: a tight wanderer's destinations are random and never repeat, so they
reduce exactly to the centre and radius `creature_movement` already stores. Route mining cannot
use them, and does not miss them.

**The digest can.** `build-digest.sql` phase 1b measures the radius as the furthest waypoint
from *the point being published*, because that is what AzerothCore's `wander_distance` is a
radius around. `creature_movement.radius_robust` measures from the median of where the creature
was seen instead, which is a different centre. Instances with no waypoints left keep
`radius_robust`, so those 106,437 now carry the weaker number permanently.

2,216,636 of 4,263,869 instances are measured from waypoints. Most of the remainder never moved
at all and were never distilled - the distillation itself accounts for 106,437, about 2.5%.

If the corpus is ever re-ingested from raw sniffs: **run `build-digest.sql` before
`distill-waypoints.sql`**, and keep the digest's radius output, because the waypoints that
produced it will not survive the second step.

### Everything else is a rebuild, not an edit

`mine-paths.sh`, `build-digest.sql` and `publish-loot.sql` all drop and recreate their outputs.
Re-running them is free apart from the time. Nothing downstream is hand-edited, so nothing is
lost by re-running.

## Server settings

`innodb_buffer_pool_size` is 8 GB. The 128 MB default makes every query in this pipeline crawl -
`creature_waypoint` alone is 8.4 GB, so a default server re-reads it from disk continuously.
The three scripts that need more than that set `tmp_table_size` and `sort_buffer_size` per
session at the top of the file.

## Where the reasoning lives

Method and evidence are not repeated here:

| file | what it argues |
|---|---|
| `FINDINGS-random-movement.md` | why a repeated *edge* is evidence and a repeated *position* is not; the AzerothCore cross-check |
| `FINDINGS-money-loot.md` | money loot |
| `RELEASE.md` | what ships, what was removed for privacy, and how to read the loot layout |
| header comment of each `.sql` | why that script does what it does, including the measurements that forced each choice |

The header of `mine-paths.sql` is the longest of those and worth reading before changing the
node key: it records what the old key cost and how it was measured.
