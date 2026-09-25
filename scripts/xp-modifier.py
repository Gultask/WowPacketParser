"""
Measures creature_template.ExperienceModifier from the kill XP in SMSG_LOG_XP_GAIN.

AzerothCore's kill XP (Formulas.cpp, Acore::XP::Gain):

    xp = BaseGain(player level, mob level, content) * (2 if elite) * ExperienceModifier

content is the bracket of the map the creature stood on - Map.dbc's expansion, after
WorldMapArea.dbc sends the Blood Elf and Draenei starting zones back to Azeroth - and picks the
45 / 235 / 580 base. creature_xp.amount is the XP before the rested bonus, so amount / BaseGain is
the multiplier times the elite doubling. Elite comes from the query response in the same branch
(creature_template.rank in the ingest), not from AzerothCore: Blizzard de-elited whole areas in
2.3, so one branch's rank says nothing about another's kills.

A creature_xp row keeps only the count, min, max and sum of its kills. Where min and max differ,
the sum splits the kills between the two if only those two values occurred; otherwise the row is
dropped, since nothing says how its kills are spread.

The player's own bonus multiplies everything: heirlooms and a +50% event put the druid levelling
sniffs at 1.70 of the formula. The most common ratio over non-elite kills, per sniff and player
level, is taken as the 1 and every kill is divided by it. Ratios within 3% of each other are one
value: BaseGain is integer arithmetic, so a lone player's kills read 1.00, 1.01 and 1.02 alike.
No bonus lowers XP, so a level whose most common ratio is below 1 was spent in a group, and its
kills are dropped rather than scaled up.

A party kill divides the XP by level share, and the packet's group rate reads 1 for a pair as for
a lone player; in a raid it reads 1 too and the XP is split among every member. So a creature is
judged by the median over its kills, which solo levelling carries, and the share of kills at that
median is reported: well under a half means grouping is in it. How many sniffs reach that median
is reported as well, since one player grouped for a whole session reads as a clean, wrong value.

  python scripts/xp-modifier.py [ingest_db] [min_kills] [path to 3.3.5 dbc folder] > xp.tsv
"""
import os, sys, subprocess
from collections import defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from importlib import import_module
read_dbc = import_module('read-dbc').read_dbc

DB = sys.argv[1] if len(sys.argv) > 1 else 'wpp_ingest'
MIN_KILLS = int(sys.argv[2]) if len(sys.argv) > 2 else 3
DBC = sys.argv[3] if len(sys.argv) > 3 else import_module('read-dbc').DBC_DIR

ELITE_RANKS = {1, 2, 3}          # elite, rare elite, boss; 4 is rare and not elite
MODE_TOLERANCE = 0.03            # ratios this close are one value; BaseGain rounds to whole XP


def query(db, sql):
    out = subprocess.run(['mysql', '-uroot', '-proot', db, '--batch', '-N', '-e', sql],
                         capture_output=True, text=True, check=True).stdout
    return [line.split('\t') for line in out.splitlines() if line]


def gray_level(pl):
    if pl <= 5:
        return 0
    if pl <= 39:
        return pl - 5 - pl // 10
    if pl <= 59:
        return pl - 1 - pl // 5
    return pl - 9


def zero_difference(pl):
    for limit, diff in ((8, 5), (10, 6), (12, 7), (16, 8), (20, 9), (30, 11), (40, 12), (45, 13), (50, 14),
                        (55, 15), (60, 16)):
        if pl < limit:
            return diff
    return 17


def base_gain(pl, mob, content):
    base = (45, 235, 580)[content]
    if mob >= pl:
        diff = min(mob - pl, 4)
        return ((pl * 5 + base) * (20 + diff) // 10 + 1) // 2
    if mob > gray_level(pl):
        zd = zero_difference(pl)
        return (pl * 5 + base) * (zd + mob - pl) // zd
    return 0


def split_row(count, lo, hi, total):
    """The kills behind a row, from its count, min, max and sum: all at one value, or split between
    min and max when the sum allows exactly that. Anything else has a third value in it: []."""
    if lo == hi or count == 1:
        return [hi] * count
    at_lo, rest = divmod(count * hi - total, hi - lo)
    if rest or not 1 <= at_lo <= count - 1:
        return []
    return [lo] * at_lo + [hi] * (count - at_lo)


def mode(rs):
    """The most common value among rs, counting every ratio within MODE_TOLERANCE of it: the
    median of that cluster and its size."""
    rs = sorted(rs)
    best, j = (0, 0, 0), 0
    for i in range(len(rs)):
        while rs[i] > rs[j] * (1 + MODE_TOLERANCE):
            j += 1
        if i - j + 1 > best[0]:
            best = (i - j + 1, j, i)
    n, j, i = best
    return (rs[(j + i) // 2], n) if n else (0, 0)


def main():
    rows, _, _, _ = read_dbc(os.path.join(DBC, 'Map.dbc'))
    map_expansion = {r[0]: r[63] for r in rows}
    rows, _, _, _ = read_dbc(os.path.join(DBC, 'WorldMapArea.dbc'))
    virtual_map = {r[2]: (r[8] if r[8] != 0xFFFFFFFF else r[1]) for r in rows}

    def content(map_id, zone):
        if map_id in (530, 571):
            map_id = virtual_map.get(zone, map_id)
        return 0 if map_id < 2 else min(map_expansion.get(map_id, 0), 2)

    sniff_rank = {}                   # (entry, branch) -> rank
    for entry, branch, rank in query(DB, 'SELECT t.entry, s.branch, MAX(t.`rank`) FROM creature_template t '
                                         'JOIN sniff s ON s.id = t.sniff_id GROUP BY t.entry, s.branch'):
        sniff_rank[(int(entry), branch)] = int(rank)

    ac = {}
    for entry, name, rank, xpmod in query('acore_world',
            'SELECT entry, name, `rank`, ExperienceModifier FROM creature_template'):
        ac[int(entry)] = (name, int(rank), float(xpmod))

    kills = []                        # (sniff, player level, entry, branch, amount / BaseGain)
    for sniff, entry, branch, map_id, zone, level, pl, count, amin, amax, asum in query(DB,
            "SELECT x.sniff_id, x.entry, s.branch, x.map, x.zone, x.level, x.player_level, x.kills, "
            "x.amount_min, x.amount_max, x.amount_sum FROM creature_xp x JOIN sniff s ON s.id = x.sniff_id "
            "WHERE x.owner = '' AND x.player_level > 0 AND x.level > 0 AND x.group_bonus = 1"):
        gain = base_gain(int(pl), int(level), content(int(map_id), int(zone)))
        if gain == 0:
            continue
        for value in split_row(int(count), int(amin), int(amax), int(asum)):
            kills.append((int(sniff), int(pl), int(entry), branch, value / gain))

    # The player's own bonus - heirlooms, a server-wide XP event - multiplies every kill in the
    # sniff alike. It is the same character against every creature, so the most common ratio among
    # non-elite kills is where the 1 is, exactly as for damage. Taken per player level, because
    # an heirloom put on or an event starting mid-capture moves it; a level with too few kills to
    # say borrows the sniff's.
    def rank_of(entry, branch):
        return sniff_rank.get((entry, branch), ac.get(entry, ('', 0))[1])

    by_sniff = defaultdict(list)
    by_level = defaultdict(list)
    for sniff, pl, entry, branch, r in kills:
        if rank_of(entry, branch) not in ELITE_RANKS:
            by_sniff[sniff].append(r)
            by_level[(sniff, pl)].append(r)
    factor = {}
    for (sniff, pl), rs in by_level.items():
        top, n = mode(rs)
        if not (n >= 5 and n * 2 > len(rs)):
            top, n = mode(by_sniff[sniff])
        if top >= 1 - MODE_TOLERANCE:
            factor[(sniff, pl)] = top
    for sniff in sorted(by_sniff):
        seen = sorted({f for (s_, _), f in factor.items() if s_ == sniff})
        print(f'# sniff {sniff}: player bonus ' + (', '.join(f'{f:.2f}' for f in seen) or 'none, grouped'),
              file=sys.stderr)

    ratios = defaultdict(list)        # (entry, branch) -> [(ratio, sniff)]
    for sniff, pl, entry, branch, r in kills:
        if (sniff, pl) in factor:
            ratios[(entry, branch)].append((r / factor[(sniff, pl)], sniff))

    print('\t'.join(['entry', 'branch', 'name', 'sniff_rank', 'ac_rank', 'kills', 'ratio', 'at_median',
                     'measured_modifier', 'ac_modifier', 'verdict', 'sniffs_at_median']))
    for key in sorted(ratios):
        entry, branch = key
        pairs = sorted(ratios[key])
        rs = [r for r, _ in pairs]
        if len(rs) < MIN_KILLS or entry not in ac:
            continue
        name, ac_rank, ac_mod = ac[entry]
        median = rs[len(rs) // 2]
        near = {sn for r, sn in pairs if abs(r - median) <= 0.02 * median + 1e-9}
        at_median = sum(abs(r - median) <= 0.02 * median + 1e-9 for r in rs) / len(rs)
        rank = rank_of(entry, branch)
        elite = 2 if rank in ELITE_RANKS else 1
        measured = median / elite
        verdict = ('grouped' if at_median < 0.5
                   else 'agrees' if abs(measured - ac_mod) <= 0.03 * max(ac_mod, measured)
                   else 'differs')
        print('\t'.join(map(str, [entry, branch, name, sniff_rank.get((entry, branch), ''), ac_rank, len(rs),
                                  f'{median:.3f}', f'{at_median:.2f}', f'{measured:.2f}', ac_mod, verdict,
                                  len(near)])))


if __name__ == '__main__':
    main()
