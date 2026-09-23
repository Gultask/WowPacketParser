"""
Measures a creature's armor from the player's melee against it, and sets it against AzerothCore's
creature_classlevelstats.basearmor * creature_template.ArmorModifier.

SMSG_ATTACKER_STATE_UPDATE carries OriginalDamage beside Damage; on a swing with no block, absorb
or resist only armor separates them (creature_armor keeps just those). With DR = 1 - damage/original
and the attacker at level L, the 3.3.5 formula inverts to

    armor = DR / (1 - DR) * K(L),   K(L) = 400 + 85 L below 60, 467.5 L - 22167.5 from 60

The formula checks out against the player's own armor, which is sent: 72% of rows within 1%.

Swings made while the victim carried an aura that modifies resistances are dropped. A creature
attacker - a guard, an escort, a quest ally - has no armor penetration, so its hits measure armor
outright, and several of them agreeing is the best evidence there is; the median is reported with
the spread. A player's armor penetration only ever reads low, so from players the highest estimate
is the closest, and it is used only where no creature ever hit the entry.

  python scripts/creature-armor.py [ingest_db] [min_swings] > armor.tsv
"""
import sys, subprocess
from collections import defaultdict

DB = sys.argv[1] if len(sys.argv) > 1 else 'wpp_ingest'
MIN_SWINGS = int(sys.argv[2]) if len(sys.argv) > 2 else 8

# MOD_RESISTANCE, MOD_RESISTANCE_PCT, MOD_BASE_RESISTANCE_PCT, MOD_RESISTANCE_EXCLUSIVE
ARMOR_AURAS = {22, 101, 142, 143}


def query(db, sql):
    out = subprocess.run(['mysql', '-uroot', '-proot', db, '--batch', '-N', '-e', sql],
                         capture_output=True, text=True, check=True).stdout
    return [line.split('\t') for line in out.splitlines() if line]


def k(level):
    return 400 + 85 * level if level < 60 else 467.5 * level - 22167.5


def main():
    auras = ','.join(map(str, ARMOR_AURAS))
    cols = ' OR '.join(f'EffectApplyAuraName{i} IN ({auras})' for i in (1, 2, 3))
    armor_spells = {int(r[0]) for r in query('wotlkmangos', f'SELECT Id FROM spell_template WHERE {cols}')}
    known = {int(r[0]) for r in query('wotlkmangos', 'SELECT Id FROM spell_template')}

    ac = {}
    for entry, name, rank, am, cls, lo, hi in query('acore_world',
            'SELECT entry, name, `rank`, ArmorModifier, unit_class, minlevel, maxlevel FROM creature_template'):
        ac[int(entry)] = (name, int(rank), float(am), int(cls))
    base = {(int(l), int(c)): int(a) for l, c, a in query('acore_world',
            'SELECT level, class, basearmor FROM creature_classlevelstats')}

    rows = query(DB, "SELECT a.entry, s.branch, a.level, a.auras, a.attacker_type, a.attacker_level, "
                     "a.swings, a.original_sum, a.damage_sum FROM creature_armor a JOIN sniff s ON s.id = a.sniff_id "
                     "WHERE a.entry > 0 AND a.attacker_type IN ('player', 'creature') "
                     "AND a.attacker_level > 0 AND a.level > 0")

    # Rows are split by every aura the victim carried, most of them the attacker's own damage over
    # time. Those are dropped only if they touch armor, and what is left is summed per attacker level.
    sums = defaultdict(lambda: [0, 0, 0])
    for entry, branch, level, victim_auras, atype, alevel, swings, orig, dmg in rows:
        spells = [int(x) for x in victim_auras.split(',') if x and x != '+']
        if victim_auras.endswith('+') or any(x in armor_spells or x not in known for x in spells):
            continue
        acc = sums[(int(entry), branch, int(level), atype, int(alevel))]
        acc[0] += int(swings)
        acc[1] += int(orig)
        acc[2] += int(dmg)

    # Creatures carry no armor penetration, so their swings measure armor outright and should agree
    # with one another. A player's can only read low; the highest of them is the closest.
    found = defaultdict(lambda: {'creature': [], 'player': []})
    for (entry, branch, level, atype, alevel), (swings, orig, dmg) in sums.items():
        if swings < MIN_SWINGS or orig <= 0:
            continue
        dr = 1 - dmg / orig
        if 0 <= dr < 0.75:
            found[(entry, branch, level)][atype].append((dr / (1 - dr) * k(alevel), swings))

    print('	'.join(['entry', 'branch', 'name', 'rank', 'level', 'creature_hits', 'armor_by_creatures',
                     'spread', 'player_hits', 'armor_by_players', 'ac_armor', 'ac_modifier', 'measured_modifier']))
    for key in sorted(found):
        entry, branch, level = key
        if entry not in ac:
            continue
        name, rank, am, cls = ac[entry]
        by_c = sorted(e for e, _ in found[key]['creature'])
        by_p = sorted(e for e, _ in found[key]['player'])
        c_armor = by_c[len(by_c) // 2] if by_c else None
        p_armor = by_p[-1] if by_p else None
        best = c_armor if c_armor is not None else p_armor
        spread = f'{(by_c[-1] - by_c[0]) / c_armor:.2f}' if len(by_c) > 1 and c_armor else ''
        b = base.get((level, cls))
        print('	'.join(map(str, [
            entry, branch, name, rank, level,
            sum(s for _, s in found[key]['creature']), round(c_armor) if c_armor is not None else '', spread,
            sum(s for _, s in found[key]['player']), round(p_armor) if p_armor is not None else '',
            round(b * am) if b else '', am, f'{best / b:.2f}' if b else ''])))


if __name__ == '__main__':
    main()
