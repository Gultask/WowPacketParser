"""
Measures creature_template.ExperienceModifier from the kill XP in SMSG_LOG_XP_GAIN.

AzerothCore's kill XP (Formulas.cpp, Acore::XP::Gain):

    xp = BaseGain(player level, mob level, content) * (2 if elite) * ExperienceModifier

content is the bracket of the map the creature stood on - Map.dbc's expansion, after
WorldMapArea.dbc sends the Blood Elf and Draenei starting zones back to Azeroth - and picks the
45 / 235 / 580 base. creature_xp.amount is the XP before the rested bonus, so amount / BaseGain is
the multiplier times the elite doubling. Elite comes from the sniff's own query response
(creature_template.rank in the ingest), not from AzerothCore.

The player's own bonus multiplies everything: heirlooms and a +50% event put the druid levelling
sniffs at 1.70 of the formula. The most common ratio over non-elite kills, per sniff and player
level, is taken as the 1 and every kill is divided by it.

A party kill divides the XP by level share, and the packet's group rate reads 1 for a pair as for
a lone player. So a creature is judged by the median over its kills, which solo levelling carries,
and the share of kills at that median is reported: well under a half means grouping is in it.

  python scripts/xp-modifier.py [ingest_db] [min_kills] [path to 3.3.5 dbc folder] > xp.tsv
"""
import os, sys, subprocess
from collections import defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from importlib import import_module
read_dbc = import_module('read-dbc').read_dbc

DB = sys.argv[1] if len(sys.argv) > 1 else 'wpp_ingest'
MIN_KILLS = int(sys.argv[2]) if len(sys.argv) > 2 else 3
DBC = sys.argv[3] if len(sys.argv) > 3 else r'C:\azerothcore-wotlk-build\bin\RelWithDebInfo\Data\dbc'

ELITE_RANKS = {1, 2, 3}          # elite, rare elite, boss; 4 is rare and not elite


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


def main():
    rows, _, _, _ = read_dbc(os.path.join(DBC, 'Map.dbc'))
    map_expansion = {r[0]: r[63] for r in rows}
    rows, _, _, _ = read_dbc(os.path.join(DBC, 'WorldMapArea.dbc'))
    virtual_map = {r[2]: (r[8] if r[8] != 0xFFFFFFFF else r[1]) for r in rows}

    def content(map_id, zone):
        if map_id in (530, 571):
            map_id = virtual_map.get(zone, map_id)
        return 0 if map_id < 2 else min(map_expansion.get(map_id, 0), 2)

    sniff_rank = {}
    for entry, rank in query(DB, 'SELECT entry, MAX(`rank`) FROM creature_template GROUP BY entry'):
        sniff_rank[int(entry)] = int(rank)

    ac = {}
    for entry, name, rank, xpmod in query('acore_world',
            'SELECT entry, name, `rank`, ExperienceModifier FROM creature_template'):
        ac[int(entry)] = (name, int(rank), float(xpmod))

    kills = []                        # (sniff, player level, entry, branch, amount / BaseGain)
    for sniff, entry, branch, map_id, zone, level, pl, count, amin, amax in query(DB,
            "SELECT x.sniff_id, x.entry, s.branch, x.map, x.zone, x.level, x.player_level, x.kills, "
            "x.amount_min, x.amount_max FROM creature_xp x JOIN sniff s ON s.id = x.sniff_id "
            "WHERE x.owner = '' AND x.player_level > 0 AND x.level > 0 AND x.group_bonus = 1"):
        gain = base_gain(int(pl), int(level), content(int(map_id), int(zone)))
        if gain == 0:
            continue
        # min and max bound every kill in the row; with one kill they are the kill
        for value in [int(amin)] * (int(count) - 1) + [int(amax)]:
            kills.append((int(sniff), int(pl), int(entry), branch, value / gain))

    # The player's own bonus - heirlooms, a server-wide XP event - multiplies every kill in the
    # sniff alike. It is the same character against every creature, so the most common ratio among
    # non-elite kills is where the 1 is, exactly as for damage. Taken per player level, because
    # an heirloom put on or an event starting mid-capture moves it; a level with too few kills to
    # say borrows the sniff's.
    def mode(counts):
        return max(counts.items(), key=lambda kv: kv[1])[0]

    by_sniff = defaultdict(lambda: defaultdict(int))
    by_level = defaultdict(lambda: defaultdict(int))
    for sniff, pl, entry, branch, r in kills:
        if sniff_rank.get(entry, ac.get(entry, ('', 0))[1]) not in ELITE_RANKS:
            by_sniff[sniff][round(r, 2)] += 1
            by_level[(sniff, pl)][round(r, 2)] += 1
    factor = {}
    for (sniff, pl), counts in by_level.items():
        top = mode(counts)
        factor[(sniff, pl)] = top if counts[top] >= 5 and counts[top] * 2 > sum(counts.values()) else mode(by_sniff[sniff])
    for sniff in sorted(by_sniff):
        seen = sorted({f for (s_, _), f in factor.items() if s_ == sniff})
        print(f'# sniff {sniff}: player bonus ' + ', '.join(f'{f:.2f}' for f in seen), file=sys.stderr)

    ratios = defaultdict(list)
    for sniff, pl, entry, branch, r in kills:
        if (sniff, pl) in factor:
            ratios[(entry, branch)].append(r / factor[(sniff, pl)])

    print('\t'.join(['entry', 'branch', 'name', 'sniff_rank', 'ac_rank', 'kills', 'ratio', 'at_median',
                     'measured_modifier', 'ac_modifier', 'verdict']))
    for key in sorted(ratios):
        entry, branch = key
        rs = sorted(ratios[key])
        if len(rs) < MIN_KILLS or entry not in ac:
            continue
        name, ac_rank, ac_mod = ac[entry]
        median = rs[len(rs) // 2]
        at_median = sum(abs(r - median) <= 0.02 * median + 1e-9 for r in rs) / len(rs)
        rank = sniff_rank.get(entry, ac_rank)
        elite = 2 if rank in ELITE_RANKS else 1
        measured = median / elite
        verdict = ('grouped' if at_median < 0.5
                   else 'agrees' if abs(measured - ac_mod) <= 0.03 * max(ac_mod, measured)
                   else 'differs')
        print('\t'.join(map(str, [entry, branch, name, sniff_rank.get(entry, ''), ac_rank, len(rs),
                                  f'{median:.3f}', f'{at_median:.2f}', f'{measured:.2f}', ac_mod, verdict])))


if __name__ == '__main__':
    main()
