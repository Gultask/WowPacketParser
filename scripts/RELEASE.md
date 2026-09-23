# Sniff data release

One dataset, nine files, two ways to take it.

| how | what you get | needs |
|---|---|---|
| **install mod-sniff-diff** | the in-game diff tool, and the updater applies all nine tables for you | AzerothCore, a build |
| **take the folder** | the same nine `.sql` files, loaded by hand | MySQL and nothing else |

There is no separate download to keep in step, because there is no second artifact. The files
live in `modules/mod-sniff-diff/data/sql/db-world/base/`, which is both the directory
AzerothCore's updater scans and an ordinary folder of SQL.

This file covers the data. If you only want drop tables or gameobject positions, load those
files and ignore the rest - there is no load order between the eight tables, and only the
`sniff_loot` view depends on anything (`sniff_loot_set` and `sniff_names`).

## Read this first

Every sniff here is from a **Classic-era client**: builds 40011 to 68101. None of it is original
3.3.5a (build 12340). The `branch` column says which client each row came from:

| branch | loots | gameobject observations |
|---|---|---|
| WotLK | 176,729 | 1,617,491 |
| TBC | 54,412 | 281,746 |
| Classic | 25 | 30,069 |

**Filter it.** A TBC drop table is not a WotLK drop table, and a TBC gameobject spawned on a
WotLK realm is an object that was never there. Every query in `loot-queries.sql` filters
`branch = 'WotLK'`; take the filter out only on purpose.

This is the closest available evidence, not a recording of the original client. Classic retuned
things. Treat a difference from your own data as a question, not a correction.

## Building it

```
mysql -u root -p wpp_ingest < publish-loot.sql
./make-release.sh
```

`publish-loot.sql` prints its own checks at the end. **Six must read zero**: character guids
remaining, player-owned rows published, loots with more items than columns, items lost by the
pivot, quantities lost by the pivot, and gameobject observations lost. If any is not zero the
data is wrong - do not publish it. The remaining lines are counts, not failures.

`make-release.sh` re-checks four of those itself and refuses to write a file that fails one,
then loads all nine into a scratch database in updater order and counts the rows back.

## Loading it

```
mysql -u acore -p acore_world < sniff_loot_set.sql
```

One file per table, so take what you want:

| file | MiB | file | MiB |
|---|---:|---|---:|
| `sniff_loot_set.sql` | 60.0 | `sniff_gameobject_point.sql` | 14.1 |
| `sniff_creature_spawn.sql` | 23.0 | `sniff_names.sql` | 12.5 |
| `sniff_creature_path.sql` | 16.0 | `sniff_loot_item.sql` | 4.4 |
| `sniff_gameobject_spawn.sql` | 15.7 | `sniff_reject.sql`, `sniff_view_loot.sql` | ~0 |

No foreign keys, no references to any AzerothCore table. They load into an empty database as
happily as into a world one. Each file opens with its own header saying what the data is, which
clients it came from and what was removed, because these travel one at a time.

`sniff_view_loot.sql` is the `sniff_loot` view and is named to sort last - the updater applies
by filename, and a view must come after the tables it reads.

## Tables

| table | rows | what it is |
|---|---|---|
| `sniff_loot_set` | 231,166 | one row per loot; items in `i1`..`i16`, stack sizes in `q1`..`q16` |
| `sniff_loot` | view | the same rows with names filled in |
| `sniff_loot_item` | 250,664 | long form - keeps the packet's slot number and the stack size |
| `sniff_names` | 348,265 | Item, Unit and GameObject names |
| `sniff_gameobject_spawn` | 142,271 | one row per gameobject **and** position - the import-shaped one |
| `sniff_gameobject_point` | 105,526 | one row per **position**, listing what stood on it |
| `sniff_reject` | 100 | loots left out, and why |

### Why the loot is laid out sideways

Sixteen columns, because sixteen is the client's own hard limit on loot slots in this era. The
widest loot in the corpus is thirteen (an Onyxia kill), so the table cannot be outgrown. Every
loot puts one item per column.

The items in each row are sorted **by item id, not by the slot the packet used**. That is the
whole trick: two kills that dropped the same set produce identical rows. So

```sql
SELECT COUNT(*), i1, i2, i3 FROM sniff_loot_set
WHERE branch = 'WotLK' AND owner_entry = 1506 GROUP BY i1, i2, i3 ORDER BY 1 DESC;
```

counts drop *patterns* directly, and identical patterns sort together where you can see them.

That matters because a loot group yields at most one of its members. So two items seen in the
same loot are in **different** groups, and two items never seen together across many chances are
probably in the **same** one. `MAX(items)` for a creature is a floor on how many groups it has:
seven items at once means at least seven groups.

`q1`..`q16` hold the stack size of the item in the matching `i` column, and are deliberately not
part of the grouping key - two kills that dropped the same items in different stack sizes are the
same drop pattern. The `sniff_loot` view writes the stack onto the name instead, so a row reads
`Iceweb Spider Silk x3`, and only when there is more than one.

What this cannot do: give you a drop rate from a small sample, or show you an item that never
dropped in front of anybody. Read the observation count next to every percentage.

Packet slot order and per-slot quantities live in `sniff_loot_item`, untouched.

## What was removed

- **Character guids.** `owner_guid` is nulled wherever `owner_type` is Player or ActivePlayer
  (111 rows), and those rows are not published at all. Creature and gameobject guids are not
  identifying and are not published either, because nothing here needs them.
- **Sniff filenames.** `sniff.file_name` carries character names, races and classes. Only
  `file_hash` crosses over, as `sniff_hash`.
- **100 misparsed loots.** 39 had the same slot filled twice; 61 held an item with a quantity of
  zero or over a thousand - the largest was 1,045,039,105. The whole loot goes, never a single
  row: a drop set with one item quietly removed still looks complete.

`sniff_names` comes from WowPacketParser's own name table, restricted to the three types this data
uses. That table has no player names in it at all.

## Gameobjects

Both tables group on the position rounded to a millimetre. A gameobject never moves, so every
capture reads the same server-side row; anything differing past three decimals is float
serialisation, not a second spawn. 2,227,881 observations collapse to 142,271 spawns on 113,326
distinct positions.

**`sniff_gameobject_spawn`** is the one to import from, and since 2026-09-21 its columns are
spelled the way AzerothCore spells them: `id`, `map`, `zoneId`, `areaId`, `phaseMask`,
`position_x`/`position_y`/`position_z`, `orientation`, `rotation0`..`rotation3` and
`VerifiedBuild`. An import is a SELECT with no aliasing. They were `entry`, `x`/`y`/`z`, `o`,
`rot0`..`rot3` and `first_build` before that date, so anything written against the old bundle
needs updating. `last_build` keeps its name beside `VerifiedBuild`, which holds the **earliest**
client that saw the row rather than the latest - "known good at least since", not AzerothCore's
own convention for that column.

Two of the AzerothCore-named columns are weaker than the rest. `zoneId` and `areaId` are the
commonest values the sniffers recorded at that position, and the parser reads those from where
the **sniffing player** was standing, not from the object - usually the same place, but not at a
zone border. `phaseMask` is 1 everywhere, because no phase heuristic is implemented at all. Both
are there so the insert is literal; neither is evidence.

Rotation is the quaternion of **one real observation**, never an average; a component-wise mean
of four rotations is not a rotation. The commonest quaternion at the spot is chosen by rounding
to four decimals, and then the exact float is published - before 2026-09-21 the rounded value was
published instead, which cut every component to four decimals and flattened small ones to zero.
The client sends floats, so the exact float is as precise as this gets. `rot_variants` says how
many distinct rotations were seen there. It is 1 for 142,032 of the 142,271 spawns. The 239 that
disagree are all transient objects - Blaze, Noblegarden eggs, summoner visuals - genuinely
different objects landing on one coordinate. Treat `rot_variants > 1` as "this spot gets reused",
not as a rotation to import.

**`sniff_gameobject_point`** exists because one spawn point often hosts several different gameobjects, and
that is a pool you cannot see from a per-entry table. `e1`..`e8` are the entries seen there,
commonest first, with each one's share of the observations in `pct1`..`pct8`. `entries` is the
true count, so truncation past eight is visible.

Eight is not an arbitrary cut. 105,510 of the 105,526 positions hold eight entries or fewer, so
the columns hold the whole pool almost everywhere. Exactly 16 positions go past it, and every one
of them is a firework launch point - 22 rockets on one Stormwind coordinate, 10 in Dalaran - seen
by no more than 7 sniffs each. Those are not pools, they are one holiday spot reused. Query 8
reaches past the columns for anything that needs it. Two real examples:

```
Dalaran bookshelf, 338 sniffs   4 books, 28.8 / 25.2 / 24.1 / 21.9
Northrend mining node, 183      Saronite 81.3 / Titanium 10.1 / Rich Saronite 8.6
```

**`pct` is a share of observations, not a spawn chance.** One sniffer parked next to a node for
an hour weights it. Read `obs` and `sniffs` beside it: a split measured across hundreds of sniffs
means something, a split from two sniffs means nothing.

`wotlk_sniffs` is the number of independent WotLK captures that saw a row. One is one person
standing somewhere. Compare against your own `gameobject` table before believing anything:
`loot-queries.sql` query 6 does exactly that.
