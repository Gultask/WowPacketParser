# Creature money loot is set by level, not per template

Measured from 231,377 loot instances across a pooled corpus of 3,101 sniffs (Classic Era,
TBC Classic and WotLK Classic), of which 169,149 are corpse loots and 121,477 carried money.
Joined against AzerothCore's `creature_template` for rank, creature type and level band.

## Summary

Three independent findings, each measured rather than assumed:

1. **The amount is a function of the creature's level band**, shared exactly by unrelated
   creatures. AzerothCore's per-template `mingold` / `maxgold` does not reflect this.
2. **Within a level the roll is uniform**, on the closed range `[maxgold / 7, maxgold]`.
3. **Whether a creature drops money at all is a separate per-entry gate**, tracking creature
   type, and independent of the amount.
4. **The level-to-amount relation is a power law, broken at the vanilla/Outland boundary** —
   `0.0433 * level^2.44` for levels 21–58 and `0.000143 * level^3.91` for 61–80, each within a
   few percent. Every level from 1 to 80 is now measured; nothing here is interpolated.

## 1. The amount is level-driven

Three unrelated level-80 creatures, thousands of observations each:

| entry | name | loots | min | max |
| ---: | --- | ---: | ---: | ---: |
| 34734 | Dark Ritualist | 665 | 559 | 3886 |
| 34702 | Dark Zealot | 1294 | 556 | 3886 |
| 34701 | Converted Hero | 1180 | 555 | 3881 |

The same holds at the bottom of the range. Seven unrelated level-3 creatures — from Tirisfal,
Durotar and Elwynn, across both factions — all drop **exactly 1 to 5 copper**: Scarlet Convert,
Rattlecage Skeleton, Bristleback Quilboar, Bristleback Shaman, and the Deathknell trio of
Stephen Bhartec, Daniel Ulfman and Karrel Grayves.

### AzerothCore's stored values do not match

`creature_template.mingold` / `maxgold` disagrees badly with what the server actually sends.
Level 71 non-elites, all sharing one real range of roughly 350–2450:

| entry | name | observed | AzerothCore stores |
| ---: | --- | --- | --- |
| 21644 | Skettis Wing Guard | 362 – 2434 | 254 / 337 |
| 27226 | Forgotten Peasant | 465 – 2262 | 1100 / 3300 |
| 27203 | Onslaught Footman | 383 – 2695 | 1160 / 1934 |
| 24013 | Deathless Watcher | 358 – 2403 | 470 / 1410 |

### The key is `maxlevel`, not the individual's level

A creature's rolled level does not matter; the template's band does. Boulderfist Crusher gives
max 1724 at level 64 and 1723 at level 65. Snowfall Glade Den Mother gives 2590 at level 71 and
2589 at level 72.

Tested directly by building a reference from templates where `minlevel = maxlevel`, then scoring
51 multi-level templates against both ends of their band:

| keyed on | median error |
| --- | ---: |
| `minlevel` | 7.3% |
| **`maxlevel`** | **1.9%** |

## 2. The roll is uniform, with min = max / 7

A histogram of 368 money drops from a single entry (Time-Lost Skettis High Priest, level 71) is
flat from roughly 350 to 2445 — no peak, no skew, no taper at either end.

The lower bound is exactly one seventh of the upper bound:

| level | observed max | max / 7 | observed min |
| ---: | ---: | ---: | ---: |
| 80 | 3886 | 555.1 | **555** |
| 71 | 2445 | 349.3 | **350** |

Consistency check: for a uniform distribution on `[max/7, max]` the mean should be 0.571 of the
max. Measured across 37 well-sampled entries: **0.580**. The small excess is the expected bias
of a sample maximum, which sits just under the true bound.

The rule was originally read off two levels. On the full corpus it can be tested against sample
size directly, which is the strongest form of the check: `min / max` measured per entry is
biased low when there are few observations, and should converge on 1/7 as observations
accumulate. It does, from every level above 25 at once:

| money drops per entry | entries | implied divisor |
| --- | ---: | ---: |
| 5 – 19 | 553 | 3.81 |
| 20 – 49 | 365 | 5.69 |
| 50 – 149 | 298 | 6.56 |
| 150 – 499 | 44 | **7.03** |
| 500+ | 3 | **6.98** |

Monotonic, and it lands on 7. Any entry with fewer than about 150 observed money drops will
understate its own range.

## 3. Dropping money at all is a separate gate

Independent of the amount, and closely tracking creature type:

| type | loots | dropped money |
| --- | ---: | ---: |
| Humanoid | 42,524 | 98.9% |
| Undead | 10,737 | 95.6% |
| Dragonkin | 1,525 | 81.5% |
| Demon | 3,454 | 74.1% |
| Totem | 1,348 | 32.9% |
| Not specified | 352 | 30.1% |
| Giant | 286 | 25.9% |
| Elemental | 4,538 | 24.9% |
| **Beast** | **21,709** | **0.2%** |
| **Mechanical** | **63** | **0.0%** |

The intermediate types are per-entry, not per-type: some Elementals and Demons always drop
money, others never do. So the gate belongs on the entry, with type only as a strong prior.

Of 884 beast entries looted, exactly six ever produced money — and each did so on **100%** of
its loots, so these are flagged money-droppers rather than noise:

| entry | name | loots with money |
| ---: | --- | ---: |
| 29392 | Ravenous Jaws | 20 |
| 30206 | Carrion Fleshstripper | 9 |
| 30430 | Sentry Worg | 3 |
| 17447 | The Kurken | 3 |
| 31265 | Savage Proto-Drake | 1 |
| 29710 | Onslaught Destrier | 1 |

Three of the six have `family = 0`, i.e. untameable, which suggests the type data rather than
the money data is what is odd about them.

Elites need no special handling in this analysis: no elite template has `HealthModifier = 1`, so
filtering on that excludes them automatically.

## Scaling across levels

With the middle of the range filled in, the shape resolves: money is a **power law in level,
broken at the vanilla/Outland boundary**. It is not a lookup table, and it is not one equation
either.

| level range | fit | mean error | worst level |
| --- | --- | ---: | ---: |
| 21 – 58 | `maxgold = 0.0433 * level^2.44` | **2.6%** | 7.0% |
| 61 – 80 | `maxgold = 0.000143 * level^3.91` | **1.3%** | 8.1% |
| 1 – 80, single fit | `maxgold = 0.273 * level^2.00` | 38.6% | 93.3% |

The earlier reading of this data as "a per-level lookup table rather than an equation" was an
artifact of the gap: with levels 21–58 missing, any single fit had to span the break, and the
residuals it left looked like structure. They were the break.

Levels 1–20 do not follow the power law. An exponential fits them better
(`3.27 * 1.170^level`, 8.5% mean error against 25.7% for a power law), but at values between 4
and 67 copper the integer quantisation dominates and neither form means much. Use the measured
values directly.

### The break at 59–61

Levels 59 through 61 sit **24% to 34% above** what the 21–58 curve predicts, and the curve
restarts steeper on the far side:

| level | observed | 21–58 formula predicts | ratio |
| ---: | ---: | ---: | ---: |
| 58 | 927 | 869 | 1.07 |
| 59 | 1125 | 906 | 1.24 |
| 60 | 1073 | 944 | 1.14 |
| 61 | 1317 | 983 | 1.34 |

Level 60 dips below 59 — the only inversion in the whole table apart from level 78. Both are
places where the corpus mixes content from either side of an expansion boundary.

### Content expansion

At the **TBC/WotLK overlap the difference disappears** once there is enough data. Levels 69–71
agree within 5% in both directions, so the 5% edge reported from the smaller corpus was noise:

| level | TBC content | WotLK content | ratio |
| ---: | ---: | ---: | ---: |
| 69 | 2301 | 2281 | 0.99 |
| 70 | 2258 | 2369 | 1.05 |
| 71 | 2459 | 2455 | 1.00 |

At **low levels the difference is real**, and runs the other way. TBC starting zones
(Eversong, Azuremyst) pay roughly 20% more than vanilla ones at the same level:

| level | Vanilla content | TBC content | ratio |
| ---: | ---: | ---: | ---: |
| 11 | 17 | 25 | 1.47 |
| 15 | 33 | 41 | 1.24 |
| 16 | 39 | 48 | 1.23 |
| 18 | 54 | 67 | 1.24 |

Levels 7–10 agree to within a copper, so the split opens up around level 11 rather than being
present from the start.

## Per-level values

Bounds were estimated per entry with the unbiased correction for the upper end of a uniform
distribution, `b = max + (max - min) / n`, then taken as the median across entries at that
level. Only non-elite templates (`rank = 0`) with at least 5 money drops were used, keyed on
`maxlevel`. `mingold` is `maxgold / 7` per the rule above, not measured separately.

| lvl | maxgold | mingold | entries | loots | | lvl | maxgold | mingold | entries | loots |
| ---: | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: | ---: | ---: |
| 1 | 4 | 1 | 1 | 64 | | 41 | 362 | 52 | 19 | 1102 |
| 2 | 4 | 1 | 4 | 470 | | 42 | 382 | 55 | 29 | 908 |
| 3 | 5 | 1 | 8 | 2495 | | 43 | 404 | 58 | 14 | 559 |
| 4 | 5 | 1 | 11 | 1974 | | 44 | 431 | 62 | 26 | 1499 |
| 5 | 8 | 1 | 10 | 564 | | 45 | 456 | 65 | 24 | 888 |
| 6 | 7 | 1 | 16 | 1498 | | 46 | 488 | 70 | 16 | 511 |
| 7 | 12 | 2 | 31 | 3567 | | 47 | 509 | 73 | 18 | 662 |
| 8 | 13 | 2 | 33 | 2012 | | 48 | 542 | 77 | 14 | 1075 |
| 9 | 14 | 2 | 27 | 1832 | | 49 | 571 | 82 | 15 | 475 |
| 10 | 16 | 2 | 48 | 2541 | | 50 | 598 | 85 | 15 | 389 |
| 11 | 18 | 3 | 27 | 992 | | 51 | 645 | 92 | 11 | 923 |
| 12 | 25 | 4 | 26 | 852 | | 52 | 680 | 97 | 14 | 775 |
| 13 | 28 | 4 | 26 | 1471 | | 53 | 724 | 103 | 11 | 409 |
| 14 | 32 | 5 | 30 | 1823 | | 54 | 759 | 108 | 26 | 1015 |
| 15 | 34 | 5 | 23 | 1274 | | 55 | 781 | 112 | 23 | 771 |
| 16 | 39 | 6 | 33 | 1729 | | 56 | 829 | 118 | 24 | 715 |
| 17 | 50 | 7 | 33 | 2183 | | 57 | 861 | 123 | 18 | 371 |
| 18 | 55 | 8 | 33 | 1853 | | 58 | 927 | 132 | 20 | 555 |
| 19 | 61 | 9 | 35 | 2421 | | 59 | 1125 | 161 | 21 | 1198 |
| 20 | 67 | 10 | 31 | 1130 | | 60 | 1073 | 153 | 13 | 590 |
| 21 | 72 | 10 | 25 | 854 | | 61 | 1317 | 188 | 18 | 1117 |
| 22 | 88 | 13 | 15 | 838 | | 62 | 1423 | 203 | 26 | 1367 |
| 23 | 96 | 14 | 17 | 574 | | 63 | 1528 | 218 | 30 | 1450 |
| 24 | 106 | 15 | 27 | 1009 | | 64 | 1632 | 233 | 28 | 1520 |
| 25 | 114 | 16 | 26 | 1188 | | 65 | 1731 | 247 | 21 | 1569 |
| 26 | 124 | 18 | 26 | 973 | | 66 | 1848 | 264 | 26 | 1268 |
| 27 | 136 | 19 | 22 | 1224 | | 67 | 1947 | 278 | 22 | 707 |
| 28 | 146 | 21 | 16 | 764 | | 68 | 2044 | 292 | 44 | 966 |
| 29 | 156 | 22 | 12 | 587 | | 69 | 2242 | 320 | 79 | 2593 |
| 30 | 173 | 25 | 19 | 699 | | 70 | 2333 | 333 | 77 | 3134 |
| 31 | 183 | 26 | 19 | 667 | | 71 | 2464 | 352 | 66 | 3903 |
| 32 | 199 | 28 | 16 | 625 | | 72 | 2591 | 370 | 65 | 2129 |
| 33 | 214 | 31 | 19 | 640 | | 73 | 2692 | 385 | 30 | 686 |
| 34 | 232 | 33 | 15 | 978 | | 74 | 2884 | 412 | 25 | 1258 |
| 35 | 247 | 35 | 21 | 1098 | | 75 | 3020 | 431 | 21 | 632 |
| 36 | 267 | 38 | 34 | 1033 | | 76 | 3220 | 460 | 20 | 615 |
| 37 | 285 | 41 | 34 | 1146 | | 77 | 3374 | 482 | 27 | 676 |
| 38 | 305 | 44 | 15 | 255 | | 78 | 3258 | 465 | 17 | 659 |
| 39 | 327 | 47 | 15 | 516 | | 79 | 3787 | 541 | 19 | 394 |
| 40 | 340 | 49 | 15 | 871 | | 80 | 3904 | 558 | 77 | 7542 |

Every level from 1 to 80 is now measured; none of these values are interpolated. Levels 1–6 and
level 60 rest on few enough entries to be worth re-checking as the corpus grows.

## Cross-validation

The model was built entirely from TBC Classic and early WotLK Classic builds. It was later used
to check a newly mapped `SMSG_LOOT_RESPONSE` opcode on 3.4.2 — a build that had produced no loot
at all until then. Level-80 non-elites in that sniff came out at **632–3757**, against
**556–3894** predicted. Two independent derivations, agreeing.

That mapping is now in the parser for 3.4.1 and 3.4.2, and the re-parse it enabled is what
filled levels 21–58: loot instances went from 99,151 to 231,377, and the 3.4.x builds are now
the largest single contributors.

## Remaining gap

`SMSG_LOOT_RESPONSE` is still unmapped for **123 sniffs**, all but one of them 2.5.5 Anniversary
TBC (builds 65417, 65534, 65676, 65795, 65895, 68101) plus a single 1.15.8. Those sniffs
contribute no loot at all. The ledger records them as `unsupported` rather than `empty`, so they
are a known gap rather than a silent one.
