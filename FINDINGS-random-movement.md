# How random creature movement is actually generated

Measured from a pooled corpus of 3,101 sniffs (WotLK Classic, TBC Classic and Classic Era),
holding 46.2 million movement points. 166,086 spawns have an exact spawn point from a
CreateObject2 event, and 301,808 creatures were observed long enough (20+ movement orders) to
characterise how they move.

## Summary

Random movement destinations are **uniform in distance from the spawn point, not uniform over
the area of the disc**. The radial exponent is 1.00.

This is the signature of picking a random angle and a random distance independently:

```c
float angle = frand(0, 2 * M_PI);
float dist  = frand(0, wander_distance);   // not sqrt(frand()) * wander_distance
```

rather than sampling uniformly over the disc's area. It is the classic "wrong" way to pick a
random point in a circle. The measurement establishes the distribution; the sampling method
above is the straightforward explanation for it, not something observed directly.

The practical consequence runs the other way: because a random destination is never repeated,
any *ordered pair* of positions that shows up in two independent captures is authored movement.
Applied to the whole corpus that reconstructs **84,173 patrol routes** — see *Rebuilding the
routes* below.

## Method

For each creature the distance `d` of every movement destination from the known spawn point was
computed, and the radial cumulative distribution fitted as

```
P(d < r) = (r / R)^k
```

with `R` taken as a robust high quantile (p97) so a single pulled-and-returned excursion cannot
define the radius. `k` was estimated per creature by maximum likelihood,
`k = -n / sum(log(d_i / R))`.

* `k = 2` — uniform over the disc's **area** (most points near the rim, because most area is there)
* `k = 1` — uniform in **distance** (every distance band equally likely)
* `k > 2` — edge-following / ring

Only creatures with at least 60 recorded destinations and a radius above 3 yards were used, and
only destinations, never pathfinding filler.

## Result

4,225 creatures, median **k = 1.00**, p25 0.91, p75 1.09.

| k range | interpretation | share of creatures |
| --- | --- | ---: |
| < 1.0 | strongly centre-weighted | 51.3% |
| 1.0 – 1.6 | centre-weighted | 47.1% |
| 1.6 – 2.5 | uniform over the disc | 1.0% |
| 2.5 – 4.0 | edge-leaning | 0.2% |
| > 4.0 | ring / edge-following | 0.3% |

The outer 20% of the radius holds **18.8%** of destinations. A disc-uniform sample would put
36% there. A ring would put nearly all of them there.

The result does not depend on which centre is used, so it is not an artifact of the spawn point
sitting off-centre:

| centre used | median k |
| --- | ---: |
| CreateObject2 spawn point | 1.00 |
| median of observed destinations | 1.02 |
| centre of the minimum enclosing circle | 1.24 |

## Why it looks like edge-following when you watch it

Watching a creature wander, the movement reads as though it favours the rim and traces shapes —
a triangle, a star. Both impressions are explainable without any edge bias:

* With `k = 1` every distance band is equally likely, so the creature reaches the **boundary
  constantly**. The extent of the circle becomes obvious within a handful of moves, far faster
  than a disc-uniform sample would reveal it. The rim is visited often; it is simply not
  favoured *relative to a uniform disc*.
* Destinations are overwhelmingly **distinct**, not repeated: 90.5% of a creature's destinations
  are unique positions, and only 3.5% of creatures repeat a fixed set of points. The polygon is
  not real. Straight-line travel between scattered points is what draws the star.

## Consequences for deriving spawn data

### Wander distance

Use `max(d)` against a robust radius. It converges quickly, because `k = 1` puts destinations at
the rim far more often than a disc-uniform sample would.

| destinations observed | median share of the p99 radius recovered | IQR |
| ---: | ---: | ---: |
| 10 | 92.7% | 0.16 |
| 20 | 97.9% | 0.08 |
| **40** | **99.9%** | **0.03** |
| 80 | 100.3% | 0.02 |

`2 * mean(d)` — the natural estimator if `k` really were 1 — performs **worse** in practice,
stalling near 95.7% with a three to five times wider spread. Use the maximum.

Note that this figure is against a **robust** radius (p99). Against the raw maximum, which one
pulled excursion inflates, convergence looks far slower and needs 120–200 observations. Discard
outlier excursions first, then 40 destinations is enough.

### Spawn position

`k` does not change the centre estimator. The distribution is radially symmetric whatever `k`
is, so the centre is unaffected. Of the estimators tried, the **median** of observed positions
is best: 1.88 yd typical error at 60 destinations, against 2.42 yd for the mean and 2.66 yd for
the bounding-box midpoint.

Against 18,415 wandering creatures whose true spawn point is known from a CreateObject2 event,
the median estimator converges steadily and is still improving past 160 observations:

| destinations observed | creatures | median error |
| ---: | ---: | ---: |
| 10 – 19 | 6,215 | 9.56 yd |
| 20 – 39 | 4,403 | 5.02 yd |
| 40 – 79 | 3,491 | 2.74 yd |
| 80 – 159 | 2,301 | 2.32 yd |
| 160+ | 2,005 | **1.72 yd** |

But accuracy is decided far more by the size of the wander circle than by effort. At 40 or more
destinations each:

| wander radius | creatures | mean error | within 2 yd | within 5 yd |
| --- | ---: | ---: | ---: | ---: |
| under 5 yd | 3,307 | 1.47 yd | **94.7%** | 96.9% |
| 5 – 15 yd | 3,253 | 4.23 yd | 44.1% | 72.7% |
| 15 – 30 yd | 2,162 | 8.15 yd | 32.2% | 51.3% |
| over 30 yd | 1,602 | 36.77 yd | 21.7% | 27.1% |

Classify by radius first. Tight wanderers are essentially solved. The wide bands are largely
mislabelled patrollers, which have no meaningful centre; reconstruct a path for those instead of
fitting a circle. The over-30-yard row is not a worse estimate of the same thing — it is the
wrong model applied to the wrong creatures.

### How long to watch

Not long — and past a few minutes, watching more does nothing. Against 30,035 wandering
creatures with a known CreateObject2 spawn point and a radius between 3 and 30 yards:

| watched for | creatures | mean error | within 5 yd |
| --- | ---: | ---: | ---: |
| under 2 min | 18,450 | 65.17 yd | 29.5% |
| 2 – 5 min | 3,815 | 9.22 yd | 57.6% |
| **5 – 15 min** | 3,589 | **6.84 yd** | **62.8%** |
| 15 – 30 min | 1,852 | 5.88 yd | 62.4% |
| 30 – 60 min | 1,244 | 6.22 yd | 63.3% |
| over 1 h | 1,085 | 6.85 yd | 58.7% |

Everything useful happens in the first five minutes. From there to an hour the curve is flat,
and the last two rows are no better than the 5–15 minute row. The under-2-minute row's mean is
inflated by creatures caught mid-pull or leashing; its `within 5 yd` share is the honest
comparison, and it is the only row that is clearly worse.

So a Far Sight parked on one spot for an hour is worth about the same as twelve minutes there.
**Twelve five-minute looks at twelve different places beat one hour-long stare**, and they also
attack the one error source that time cannot: the angular bias below. Move the eye.

### Why the centre plateaus

The residual error is **angular, not radial**. 76.3% of creatures show strong one-sidedness
relative to their true spawn point (median directional bias 0.480 on a 0–1 scale, where 0 is
perfectly symmetric), and only about 92% of twelve angular sectors are occupied at all.

The observed cloud's centre sits roughly 0.2–0.3 R away from the true spawn point, and watching
longer within one session does not fix it. This is what observing a bounded random walk over a
window too short to equilibrate looks like. It predicts that pooling **across separate sessions**
should fix what watching longer cannot, since different sessions catch the walk in different
regions. That prediction has not yet been confirmed on enough data — an early test had only 78
spawn points and was inconclusive.

An earlier hypothesis, that the offset came from the observer's vantage point, was tested and
**refuted**: error directions of different creatures within the same sniff are no more aligned
than error directions of creatures picked at random from different sniffs (mean cosine −0.0018
against −0.0007).

## Telling random movement from a patrol

Geometry is the wrong tool. Packet structure settles it directly:

> **98.6% of movement segments carry exactly one point.**

A single random destination per packet. The remaining 1.4% are authored splines — patrol routes
and flight paths. Segment length costs nothing to compute and is the natural first cut, but it
is not sufficient on its own — see the pause section below for the case it gets wrong.

Byte-identical geometry across independent sniffs then confirms that 1.4%, but it has to be
applied to **edges** (consecutive point pairs), not to individual points. Run over the whole
corpus this stops being a discriminator and becomes a reconstruction - see *Rebuilding the
routes* below:

| criterion | AzerothCore marks it a patrol | no path set |
| --- | ---: | ---: |
| shared **edges** across sniffs | 18.8% | 5.4% |

### Pauses between orders

Segment length alone is not enough, and it fails on a specific and common case: a patrol whose
waypoints the server streams **one per packet**. Those look identical to random movement by
segment length, and are misclassified.

The timing separates them. Random movement waits between destinations; an authored path runs
continuously and only stops for roleplay or at its ends. Comparing each order's stated travel
time against when the next order actually arrived:

| | pause rate |
| --- | ---: |
| single-point segments | 39.7% |
| multi-point segments (authored splines) | 21.9% |

Measured per creature across **301,808 creatures** with at least twenty movement orders, the
same split appears — and the third feature, how far the creature ranges, separates them harder
than either:

| segment composition | creatures | pause rate | mean radius |
| --- | ---: | ---: | ---: |
| no multi-point segments | 268,207 | 23.4% | 32.7 yd |
| some authored splines | 33,431 | **13.7%** | **122.1 yd** |

The 11% of creatures that emit any authored spline pause at little more than half the rate and
range nearly four times as far. That is the patrol population, and it is much larger than the
1.4%-of-segments figure suggests: a patrol emits mostly single-point orders and only occasionally
a real spline.

Radius and pause rate agree monotonically over the whole corpus:

| radius | creatures | pause rate | segments that are multi-point |
| --- | ---: | ---: | ---: |
| under 10 yd | 105,255 | **30.4%** | 0.1% |
| 10 – 25 yd | 85,558 | 23.2% | 0.4% |
| 25 – 60 yd | 65,470 | 17.0% | 0.8% |
| 60 – 200 yd | 40,080 | 10.3% | 1.6% |
| over 200 yd | 5,445 | **8.6%** | 1.6% |

Read the other way, sorting by pause rate recovers the radius:

| pause rate | creatures | median radius |
| --- | ---: | ---: |
| over 20% | 175,175 | **9.9 yd** |
| 5 – 20% | 73,559 | 31.2 yd |
| under 5% | 35,178 | 41.2 yd |
| exactly zero | 17,896 | 38.3 yd |

The `exactly zero` row breaks the monotonicity because it collects short observations along with
genuine patrols — a creature watched for two orders has no chance to pause. Require twenty
transitions before trusting a zero.

A worked case, from a sniff whose own file name records that entries 804, 805, 806, 807, 810
and 811 were patrolling:

| entry | points | multi-point segments | transitions | pauses | radius |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 807 | 66 | 0 | 65 | **0** | 124.0 yd |
| 811 | 66 | 0 | 64 | **0** | 124.1 yd |
| 806 | 66 | 0 | 65 | **0** | 124.1 yd |
| 2442 | 16 | 0 | 14 | 4 | 17.6 yd |
| 299 | 12 | 0 | 11 | 1 | 10.1 yd |

Every one of these has zero multi-point segments, so segment length calls them all random
movement. It is wrong about the first three: a 124-yard radius is no wander circle, and 0
pauses in 65 transitions is continuous movement. The pause rate and the radius both get it
right where segment length does not.

So use all three together — segment length, pause rate, and radius — rather than any one alone.
Note also that a per-creature test of pause rate against a label *derived from segment length*
scores only 57.4% against a 50% baseline; that measures agreement between the two rules, not
accuracy, and the case above is precisely where they are supposed to disagree.

### Classification is per spawn, never per entry

The same creature entry can patrol at one spawn point and wander at another. Any classification
keyed on entry mixes the two together and dilutes both — which is why an entry-level test of
shared edges returns a muddy 18.8% against 5.4% rather than a clean split. Group by guid within
a sniff, and by spatial cluster across sniffs.

Shared **points** are useless as a signal — 17.75% of random-movement waypoints are already
byte-identical across sniffs, because a spline's first point is the creature's current position
and an idle creature sits exactly on its spawn point.

(In AzerothCore, movement is stored per spawn, not per template: `creature.MovementType` and
`creature_addon.path_id`. `creature_template.MovementType` is only a default.)

## Rebuilding the routes

Applied to the whole corpus, repeated edges reconstruct patrol routes directly. From 46.2M
movement points: **1,315,664 positions** appear in two or more independent sniffs, they yield
10.2M ordered pairs, and **555,585 of those pairs repeat across two or more sniffs**. Chaining
those confirmed edges gives **84,173 routes over 4,992 creature entries and 76 maps**.

The out-degree distribution is the proof that these are routes and not a graph of coincidences:

| successors from a node | nodes |
| ---: | ---: |
| exactly 1 | **337,490** |
| 2 | 47,263 |
| 3 | 11,544 |
| 4 or more | 13,594 |

82% of nodes have exactly one confirmed successor. Random movement cannot do that.

**26.6% of the reconstructed routes come back on themselves** (16,100 of 60,418), which is what
a patrol circuit looks like. But only 5,873 of those return to their *first* point. The other
10,227 return to somewhere in the middle: the creature walks in along a tail and then circles,
so the loop begins part way along. Which point a route closes onto is recorded rather than
assumed — assuming the first one invents a step, and the fabricated closures spanned up to 1,031
yards against 249 for the real ones.

### Checked against AzerothCore

Entries with a reconstructed route are 5.7 times more likely to be ones AzerothCore already
marks as patrolling:

| | entries | have `creature_addon.path_id` |
| --- | ---: | ---: |
| reconstructed route | 1,776 | **55.5%** |
| no route found | 15,678 | 9.8% |

And where AzerothCore already has a path for the same entry and map, the geometry agrees.
Comparing each reconstructed point to the nearest AzerothCore waypoint (41,942 points over 924
entries):

| agreement | share |
| --- | ---: |
| within 1 yd | **46.7%** |
| within 5 yd | 69.5% |
| within 15 yd | 87.1% |

These two datasets share no inputs — one is hand-authored core data, the other is derived from
packet captures — so the agreement is real corroboration rather than a circular check.

Of 1,913 entries with a high-confidence route, 986 overlap AzerothCore's 2,520 patrolling
entries and **927 are entries AzerothCore has no path for at all**. Examples, each edge
confirmed by 150+ independent sniffs: Marcella Bloom (32421), a 24-point closed loop in
Dalaran; Argent Watchman (33780), 12 points over 598 yards.

### Method

Positions are packed into a single 64-bit integer at centimetre resolution, so grouping never
touches float equality:

```
xy_key   = (round((x + 17100) * 100) << 22) | round((y + 17100) * 100)
node_key = (xy_key << 12) | level
```

**Z is deliberately not part of a node's identity.** X and Y are authored and come back
byte-identical across captures; Z does not, because the server snaps a ground creature to the
terrain and the same waypoint returns a fifth of a yard lower on a different sniff. An earlier
key packed Z at decimetre resolution and paid for it twice: 50,148 X,Y positions carried more
than one node (93% of those splits under 0.25 yd of Z), which cut 31% of routes into arcs
published as separate paths — and, worse, ran the two-sniff test per Z variant, so a waypoint
seen once at 50.18 and once at 50.31 failed it twice instead of passing once.

`level` separates X,Y that really is stacked — a ramp, a tower stair, a bridge over the road
below — found by spread and split by clustering the observed Z values on gaps rather than by
rounding, since a rounding boundary divides 50.04 from 50.06 exactly as readily. 2,031 of
917,535 nodes needed it.

Edges come from consecutive movement orders for one creature within one sniff, ordered by
`(segment_id, point_index)`. That deliberately includes pairs that span two packets — the
streamed-patrol case that segment length misses — and not only pairs inside one spline.

A hop longer than 250 yards is treated as a teleport or a bad join and cuts the chain there.
Where a node has more than one confirmed successor, the strongest edge (most independent
sniffs) is followed first.

The implementation is `scripts/mine-paths.sql` and `scripts/chain-paths.py`.
