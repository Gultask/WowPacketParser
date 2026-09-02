"""Recover loot group structure from observed loot sets.

THE ARGUMENT
------------
Items in one loot group are mutually exclusive: the server rolls the group once and keeps one
member. So two items that NEVER appear in the same loot are candidates for a shared group -
but only if chance would plainly have paired them by now. Under independence, a pair with
per-loot rates pa and pb is expected in e = n*pa*pb loots, and the chance of seeing it zero
times is about exp(-e). e = 3 therefore means "5% likely by luck".

That 5% is per pair, and a 100-item chest has 4,950 pairs, so a flat 5% cutoff manufactures
about 250 fake groups out of pure noise. The threshold has to be raised until the expected
number of false pairs ACROSS THE WHOLE RUN is below alpha:

    e_min = ln(pairs_tested / alpha)

Groups are then the connected components of the surviving pairs, not greedy cliques: if a-b
and a-c are both proven exclusive, b-c is in the same group by transitivity, whether or not
b and c are individually frequent enough to prove on their own. A component is refuted the
instant any two of its members turn up in one loot, which is the check printed at the end.

USAGE
-----
  mysql ... --batch -e "<query>" > loot.tsv       # columns: owner, loot_key, item_id
  python loot-groups.py loot.tsv                  # groups
  python loot-groups.py loot.tsv --audit          # + roster, every decisive pair, refutation table
  python loot-groups.py loot.tsv 60 --audit       # only owners with >= 60 loots

Nothing here is asserted that the printed tables do not already show.
"""
import sys, csv, math, subprocess
from collections import defaultdict, Counter
from itertools import combinations

MIN_LOOTS = 50
ALPHA = 0.05      # expected false pairs across an entire owner's run
FLOOR = 3.0       # never assert below this even if the roster is tiny
LOOKAHEAD = 20    # do not list an undecided pair needing more than 20x the loots we have
LIMIT = 12
NL = chr(10)
TAB = chr(9)


def _query(db, sql):
    return subprocess.run(['mysql', '-uroot', '-proot', db, '--batch', '-N', '-e', sql],
                          capture_output=True, text=True).stdout


def _pairs(out):
    d = {}
    for line in out.strip().split(NL):
        if TAB in line:
            a, b = line.split(TAB, 1)
            d[a] = b
    return d


def item_names(ids):
    """Loot CONTENTS are always items, so item_template is the right table for these."""
    if not ids:
        return {}
    return {int(k): v for k, v in _pairs(_query(
        'acore_world', 'SELECT entry,name FROM item_template WHERE entry IN (%s);'
        % ','.join(map(str, ids)))).items()}


def owner_names(owners):
    """Owners are looked up by their OWN type - a gameobject entry is not an item entry."""
    d, want = {}, defaultdict(list)
    for o in owners:
        t, _, e = o.partition(':')
        if e.isdigit():
            want[t].append(e)
    for t, ids in want.items():
        for k, v in _pairs(_query('wpp', "SELECT Id,Name FROM object_names WHERE ObjectType='%s'"
                                  " AND Id IN (%s);" % (t, ','.join(ids)))).items():
            d['%s:%s' % (t, k)] = v
    return d


def tally(sets):
    cnt, co = Counter(), Counter()
    for s in sets:
        for i in s:
            cnt[i] += 1
        for a, b in combinations(sorted(s), 2):
            co[(a, b)] += 1
    return cnt, co


def components(items, edges):
    """Union-find over proven-exclusive pairs. Transitivity is the point: groups partition."""
    parent = {i: i for i in items}

    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    for a, b in edges:
        ra, rb = find(a), find(b)
        if ra != rb:
            parent[ra] = rb
    out = defaultdict(list)
    for i in items:
        out[find(i)].append(i)
    return list(out.values())


def report(owner, sets, nm, onm, audit):
    n = len(sets)
    cnt, co = tally(sets)
    items = sorted(cnt, key=lambda x: (-cnt[x], x))
    npairs = len(items) * (len(items) - 1) // 2
    thresh = max(FLOOR, math.log(npairs / ALPHA)) if npairs else FLOOR
    mean_items = sum(len(s) for s in sets) / n

    def pair(a, b):
        return co[(a, b)] if a < b else co[(b, a)]

    def exp(a, b):
        return cnt[a] * cnt[b] / n

    print('=== %s  %s   %d loots, mean %.2f items' % (owner, onm.get(owner, '?'), n, mean_items))
    print('    %d items, %d pairs -> a pair must be expected %.1f times before "never together"'
          ' means anything here' % (len(items), npairs, thresh))
    if mean_items < 1.5:
        print('    !! only %.2f items per loot: near-single-item loot makes every pair exclusive by'
              % mean_items)
        print('       arithmetic, not by grouping. Treat the roster as one pool until a multi-item'
              ' loot from this owner is seen.')

    edges = [(a, b) for a, b in combinations(items, 2)
             if pair(a, b) == 0 and exp(a, b) >= thresh]
    comps = [c for c in components(items, edges) if len(c) > 1]
    grouped = set()
    for c in comps:
        grouped |= set(c)

    if audit and edges:
        print(NL + '  -- proven-exclusive pairs ' + '-' * 43)
        print('     %8s %8s %7s %10s   %s' % ('item A', 'item B', 'chance', 'p(by luck)', 'names'))
        for a, b in sorted(edges, key=lambda ab: -exp(*ab)):
            print('     %8d %8d %7.1f %10.1e   %s / %s'
                  % (a, b, exp(a, b), math.exp(-exp(a, b)),
                     nm.get(a, '?')[:22], nm.get(b, '?')[:22]))

    # A pair is DECIDED either way once chance would have paired it: seen together means
    # independent for certain, never together means grouped. Everything else is just unseen.
    decided_ind = sum(1 for a, b in combinations(items, 2)
                      if pair(a, b) > 0 and exp(a, b) >= thresh)
    undecided = npairs - decided_ind - len(edges)
    print(NL + '  -- groups ' + '-' * 59)
    print('     %d of %d pairs settled: %d proven independent (seen together), %d proven grouped.'
          '  %d still unseen and underpowered.'
          % (decided_ind + len(edges), npairs, decided_ind, len(edges), undecided))
    if not comps:
        print('     no group proven at this corpus size.')
    for c in sorted(comps, key=lambda c: -sum(cnt[i] for i in c)):
        cs = set(c)
        hit = sum(1 for s in sets if s & cs)
        print('  group of %-2d  fires %3.0f%% of loots' % (len(c), 100 * hit / n))
        for i in sorted(c, key=lambda x: (-cnt[x], x)):
            print('     %8d  %-38s %6.1f%%   (%.0f%% of the group)'
                  % (i, nm.get(i, '?')[:38], 100 * cnt[i] / n, 100 * cnt[i] / hit))

    rest = [i for i in items if i not in grouped]
    if rest:
        print('  ungrouped (independent rolls, or grouped too rarely to prove):')
        for i in rest:
            print('     %8d  %-38s %6.1f%%' % (i, nm.get(i, '?')[:38], 100 * cnt[i] / n))

    if audit and comps:
        print(NL + '  -- refutation: loots holding 0 / exactly 1 / 2+ members ' + '-' * 13)
        print('     %-30s %6s %8s %5s   %s' % ('group', 'zero', 'exactly1', '2+', 'verdict'))
        for c in comps:
            cs = set(c)
            hist = Counter(len(s & cs) for s in sets)
            two = sum(v for k, v in hist.items() if k >= 2)
            label = ','.join(str(x) for x in sorted(c, key=lambda x: -cnt[x]))
            print('     %-30s %6d %8d %5d   %s'
                  % (label[:30], hist.get(0, 0), hist.get(1, 0), two,
                     'holds' if two == 0 else 'REFUTED - these co-occur, not a group'))

    cand = []
    for a, b in combinations(items, 2):
        if pair(a, b) or (a in grouped and b in grouped):
            continue
        e = exp(a, b)
        if e >= thresh:
            continue
        pa, pb = cnt[a] / n, cnt[b] / n
        need = thresh / (pa * pb)
        if need <= LOOKAHEAD * n:
            cand.append((need, e, a, b))
    if cand:
        cand.sort()
        print(NL + '  -- undecided pairs, closest to decidable first ' + '-' * 22)
        print('     %8s %8s %7s %7s   %s' % ('item A', 'item B', 'chance', 'need', 'names'))
        for need, e, a, b in cand[:LIMIT]:
            print('     %8d %8d %7.1f %7.0f   %s / %s'
                  % (a, b, e, need, nm.get(a, '?')[:22], nm.get(b, '?')[:22]))
        if len(cand) > LIMIT:
            print('     ... %d more, all needing over %.0f loots'
                  % (len(cand) - LIMIT, cand[LIMIT][0]))

    model = sum(sum(1 for s in sets if s & set(c)) / n for c in comps) \
        + sum(cnt[i] / n for i in rest)
    ok = abs(model - mean_items) < 0.15
    print(NL + '  model predicts %.2f items per loot, observed %.2f  -- %s'
          % (model, mean_items,
             'no group above is over-merged' if ok else 'OVER-MERGED: a group above is not real'))
    print('  (one-sided: a real group fires exactly as often as its members sum to, so this catches')
    print('   groups merged wrongly, never groups that were missed.)')
    print()


def main(path, *rest):
    audit = '--audit' in rest
    rest = [r for r in rest if not r.startswith('--')]
    min_loots = int(rest[0]) if rest else MIN_LOOTS

    per = defaultdict(lambda: defaultdict(set))
    with open(path, encoding='utf-8') as fh:
        for r in csv.DictReader(fh, delimiter=TAB):
            per[r['owner']][r['loot_key']].add(int(r['item_id']))

    allids = set()
    for loots in per.values():
        for s in loots.values():
            allids |= s
    nm = item_names(sorted(allids))
    onm = owner_names(per.keys())

    for owner in sorted(per, key=lambda o: -len(per[o])):
        sets = list(per[owner].values())
        if len(sets) >= min_loots:
            report(owner, sets, nm, onm, audit)


if __name__ == '__main__':
    main(*sys.argv[1:])
