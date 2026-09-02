"""Chain confirmed movement edges into ordered paths.

Input is the output of scripts/mine-paths.sql: nodes and edges that RECURRED - a position
reached by two move orders, an ordered pair walked twice. Random movement never picks the same
next destination twice, so a repeated ordered pair is authored data whether the repeat came from
two captures or from one player watching two laps. What is left is to walk the edges back into
the routes they came from.

Both counts travel with each edge and they are not the same evidence. `n_sniffs` is independent
captures and is the stronger of the two, so it stays the sort key - `walk` still takes the
best-corroborated edge first, and a single-capture edge is only ever a fallback where nothing
better exists. `n_obs` is total traversals and is what admits an edge at all now, so it is
published alongside: after this change `edge_sniffs = 1` covers both "one capture, two laps" and
"one capture, forty laps", and only `edge_obs` separates them.

Reads two TSV exports, writes two TSVs. No database driver needed - mysql --batch produces the
input and LOAD DATA takes the output back.

  python chain-paths.py nodes.tsv edges.tsv path_summary.tsv path_point.tsv
"""
import sys
from collections import defaultdict
from math import sqrt

MIN_POINTS = 3        # two points is a single edge, not a route
MAX_GAP_YD = 250.0    # a longer hop is a teleport or a bad join; split the chain there

# Two nodes this close are one authored waypoint. A float goes to the database and comes back a
# centimetre away, so a position is not an identity - published path 39742 held 2628.18 where
# 39730 held 2628.19 for the same point. Rounding to a coarser grid only moves the boundary
# instead of removing it, the argument mine-paths.sql already makes about Z. Measured on the
# published corpus, same-entry node pairs run about 200x over background density below 2 cm and
# are back to background by half a yard, so a quarter yard is inside the jitter and nowhere near
# a real gap between waypoints.
COVER_YD = 0.25
COVER_CELL = 0.5      # grid cell for the candidate lookup; must be >= COVER_YD


def read_tsv(path):
    with open(path, encoding='utf-8') as fh:
        head = fh.readline().rstrip('\n').split('\t')
        for line in fh:
            row = line.rstrip('\n').split('\t')
            if len(row) == len(head):
                yield dict(zip(head, row))


def covered_by(summaries, points):
    """path_id -> the longer route that walks all of this one's ground, for the ones that have one.

    One authored walk comes out as several routes, and this is where that happens rather than a
    flaw in the data: `walk` consumes the strongest edges first, so a spurious A->C edge left over
    from a capture that missed B fits no chain, and the next pass starts a fresh route from it. The
    leftovers are permutations of nodes the real route already owns. Published entry 27500 is the
    specimen - one seven-point ring at 32 to 44 sniffs an edge, plus two scrambled copies of four
    and five of its own points at 2 to 7.

    Nothing is discarded here. The column states a geometric fact and leaves the policy to the
    reader, because the two shapes it covers want opposite handling:

      * same entry, and the covered route weaker - a gap artifact. Drop it.
      * different entry - several creatures walking one authored circuit, which is ordinary. Three
        Bloodfury harpy entries share one Stonetalon loop; only 4027's capture of it closes. Fold
        them together and keep every name, or the answer to "who patrols this" loses two thirds of
        itself.

    Measured against the 2026-08-25 published corpus: 8,343 of 36,756 routes are covered by a
    longer route of their own entry, and 1,466 by one of a different entry over 1,082 circuits.
    """
    node = {}
    box = {}
    rank = {}
    for row in summaries:
        pid, entry, mp, _, _, _, _, _, max_edge, _, _, _ = row
        pts = [(x, y) for _, x, y, _, _, _ in points[pid]]
        uniq = []
        for p in pts:
            if not any(_near(p, q) for q in uniq):
                uniq.append(p)
        node[pid] = uniq
        box[pid] = (mp, min(x for x, _ in pts), max(x for x, _ in pts),
                    min(y for _, y in pts), max(y for _, y in pts))
        # More ground wins, then more evidence, then the lower id. A total order, so two routes
        # standing on each other's ground never point at each other and lose both.
        rank[pid] = (len(uniq), max_edge, -pid)

    # A covering route contains this one's FIRST node, so one grid lookup is the whole candidate
    # list. Without it Warmage Silva alone is 1,497 routes compared pairwise in one small room.
    grid = {}
    for pid, uniq in node.items():
        mp = box[pid][0]
        for x, y in uniq:
            grid.setdefault((mp, int(x // COVER_CELL), int(y // COVER_CELL)), set()).add(pid)

    cover = {}
    for pid, uniq in node.items():
        if not uniq:
            continue
        mp, x0, x1, y0, y1 = box[pid]
        fx, fy = uniq[0]
        cands = set()
        for gx in (-1, 0, 1):
            for gy in (-1, 0, 1):
                cands.update(grid.get((mp, int(fx // COVER_CELL) + gx,
                                       int(fy // COVER_CELL) + gy), ()))

        best = None
        for other in cands:
            if other == pid or rank[other] <= rank[pid]:
                continue
            if best is not None and rank[other] <= rank[best]:
                continue
            _, ox0, ox1, oy0, oy1 = box[other]
            if x0 < ox0 - COVER_YD or x1 > ox1 + COVER_YD:
                continue
            if y0 < oy0 - COVER_YD or y1 > oy1 + COVER_YD:
                continue
            if all(any(_near(p, q) for q in node[other]) for p in uniq):
                best = other
        if best is not None:
            cover[pid] = best
    return cover


def _near(a, b):
    dx, dy = a[0] - b[0], a[1] - b[1]
    return dx * dx + dy * dy <= COVER_YD * COVER_YD


def main(node_path, edge_path, out_summary, out_points):
    pos = {}
    for r in read_tsv(node_path):
        pos[(int(r['entry']), int(r['map']), int(r['pos_key']))] = (
            float(r['x']), float(r['y']), float(r['z']))

    out = defaultdict(list)       # (entry,map,from) -> [(to, n_sniffs, spline_obs, n_obs)]
    indeg = defaultdict(int)
    sources = defaultdict(list)   # (entry,map) -> [from_key, ...]  so groups need no rescan
    for r in read_tsv(edge_path):
        e, m = int(r['entry']), int(r['map'])
        f, t = int(r['from_key']), int(r['to_key'])
        k = (e, m, f)
        if k not in out:
            sources[(e, m)].append(f)
        out[k].append((t, int(r['n_sniffs']), int(r['spline_obs']), int(r['n_obs'])))
        indeg[(e, m, t)] += 1

    # Independent captures first, then traversals. The tiebreak matters now that single-capture
    # edges exist: without it two edges at n_sniffs 1 came out in file order.
    for k in out:
        out[k].sort(key=lambda x: (-x[1], -x[3]))

    def dist(e, m, a, b):
        pa, pb = pos.get((e, m, a)), pos.get((e, m, b))
        if pa is None or pb is None:
            return 0.0
        return sqrt((pa[0]-pb[0])**2 + (pa[1]-pb[1])**2 + (pa[2]-pb[2])**2)

    used = set()

    def walk(e, m, start):
        """Follow strongest unused edges until the route runs out or closes on itself."""
        chain, edges, node = [start], [], start
        seen = {start}
        while True:
            nxt = None
            for cand in out.get((e, m, node), ()):
                if (e, m, node, cand[0]) not in used:
                    nxt = cand
                    break
            if nxt is None:
                break
            t, ns, sp, nob = nxt
            used.add((e, m, node, t))
            edges.append((ns, sp, dist(e, m, node, t), nob))
            chain.append(t)
            if t in seen:                   # closed the loop
                return chain, edges, True
            seen.add(t)
            node = t
        return chain, edges, False

    def split_on_gaps(chain, edges):
        """A hop longer than MAX_GAP_YD is not a route step. Cut there."""
        parts, cur_c, cur_e = [], [chain[0]], []
        for i, ed in enumerate(edges):
            if ed[2] > MAX_GAP_YD:
                parts.append((cur_c, cur_e))
                cur_c, cur_e = [chain[i+1]], []
            else:
                cur_c.append(chain[i+1])
                cur_e.append(ed)
        parts.append((cur_c, cur_e))
        return parts

    def parts_of(chain, edges, closed):
        """Split one walk into publishable routes, each with the seq it closes onto or -1.

        A walk stops when it reaches a node it has already stood on - but not necessarily the
        one it started from. A B C D B is a lasso, and the old output called it closed, which
        made the reader draw D->A: a step nobody walked. 10,220 of 16,100 routes marked closed
        were that shape, closing across 19.3 yards on average. The 5,880 that really were rings
        had the opposite problem - the repeated node was published as a second point, so two
        markers stood on one spot and the closing beam had zero length.

        Recording WHICH point the route returns to fixes both without losing the approach. The
        repeated node is dropped, so every point appears once, and close_seq says where the last
        one leads. A true ring is just close_seq 0."""
        if closed:
            k = chain.index(chain[-1])
            # Every point keeps its own outgoing edge, the last one leading back to point k.
            if all(ed[2] <= MAX_GAP_YD for ed in edges):
                return [(chain[:-1], edges, k)]
            # A hole in it means it was never a loop; keep the full chain and cut it normally.
        return [(c, ed, -1) for c, ed in split_on_gaps(chain, edges)]

    summaries, points = [], {}

    path_id = 0
    for (e, m) in sorted(sources):
        src = sources[(e, m)]
        starts = [f for f in src if indeg[(e, m, f)] == 0]
        for start in starts + src:           # open routes first, then whatever cycles remain
            if all((e, m, start, c[0]) in used for c in out.get((e, m, start), ())):
                continue
            chain, edges, closed = walk(e, m, start)
            for c, ed, close_seq in parts_of(chain, edges, closed):
                if len(c) < MIN_POINTS or not ed:
                    continue
                path_id += 1
                summaries.append([
                    path_id, e, m, len(c), 1 if close_seq >= 0 else 0, close_seq,
                    sum(x[2] for x in ed),
                    min(x[0] for x in ed), max(x[0] for x in ed),
                    sum(1 for x in ed if x[1] > 0),
                    min(x[3] for x in ed), max(x[3] for x in ed)])
                pts = []
                for i, key in enumerate(c):
                    x, y, z = pos.get((e, m, key), (0.0, 0.0, 0.0))
                    # A closed route has an edge per point; an open one's last point has no
                    # outgoing edge and reports the one that brought it there.
                    src = ed[i] if i < len(ed) else ed[-1]
                    pts.append((i, x, y, z, src[0], src[3]))
                points[path_id] = pts

    cover = covered_by(summaries, points)

    fs = open(out_summary, 'w', encoding='utf-8', newline='\n')
    fp = open(out_points, 'w', encoding='utf-8', newline='\n')
    fs.write('path_id\tentry\tmap\tn_points\tclosed\tclose_seq\tlength_yd\t'
             'min_edge_sniffs\tmax_edge_sniffs\tspline_edges\t'
             'min_edge_obs\tmax_edge_obs\tcovered_by\n')
    fp.write('path_id\tentry\tmap\tseq\tx\ty\tz\tedge_sniffs\tedge_obs\n')
    for row in summaries:
        fs.write('%d\t%d\t%d\t%d\t%d\t%d\t%.1f\t%d\t%d\t%d\t%d\t%d\t%d\n'
                 % tuple(row + [cover.get(row[0], 0)]))
        for i, x, y, z, ns, nob in points[row[0]]:
            fp.write('%d\t%d\t%d\t%d\t%.4f\t%.4f\t%.4f\t%d\t%d\n'
                     % (row[0], row[1], row[2], i, x, y, z, ns, nob))
    fs.close()
    fp.close()
    print('paths: %d, of which covered by a longer one: %d' % (path_id, len(cover)))


if __name__ == '__main__':
    main(*sys.argv[1:5])
