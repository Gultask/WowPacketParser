"""
Reads DamageModifier and ArmorModifier straight off the stat sheets in creature_stats.

The sheet's damage range is the server's own arithmetic, the same formula AzerothCore uses
(StatSystem.cpp, Creature::UpdateDamagePhysical):

    min = (damage_base       + AP/14) * DamageModifier * attack_time
    max = (damage_base * 1.5 + AP/14) * DamageModifier * attack_time

so (max - min) / attack_time = damage_base * DamageModifier / 2, with no attack power in it. Which
of the three damage_base columns applies is not known up front, so each is tried: the one whose
modifier also reproduces min is the creature's. A sheet sent to an owner carries its attack
power; a TBC Anniversary sheet, sent for every creature, does not, and AzerothCore's
classlevelstats attack power stands in - it matched to the digit wherever both were sent. Where
it does not reproduce min, the creature's attack power is its own (min_check says so) and
AzerothCore's exp column is assumed; the modifier itself does not depend on attack power.

Armor is sent whole, so ArmorModifier is armor / basearmor.

Only sheets with no aura on the unit are used, since a buff moves damage, attack power and armor.
Pets are left out: their sheet is pet_levelstats plus the owner's share, not a template.

  python scripts/creature-stats.py [ingest_db] > stats.tsv
"""
import sys, subprocess
from collections import defaultdict

DB = sys.argv[1] if len(sys.argv) > 1 else 'wpp_ingest'
MIN_NORM_SHEETS = 8       # sheets needed to know a branch's base armor at a class and level
TOLERANCE = 0.005          # relative miss on min allowed; the sheet is a float, so the fit is exact


def query(db, sql):
    out = subprocess.run(['mysql', '-uroot', '-proot', db, '--batch', '-N', '-e', sql],
                         capture_output=True, text=True, check=True).stdout
    return [line.split('\t') for line in out.splitlines() if line]


def num(v):
    return None if v in ('NULL', '') else float(v)


def main():
    levels = {}
    for row in query('acore_world', 'SELECT level, class, damage_base, damage_exp1, damage_exp2, basearmor, '
                                    'attackpower FROM creature_classlevelstats'):
        level, cls = int(row[0]), int(row[1])
        levels[(level, cls)] = ([float(x) for x in row[2:5]], float(row[5]), float(row[6]))

    ac = {}
    for entry, name, cls, exp, dm, am in query('acore_world',
            'SELECT entry, name, unit_class, exp, DamageModifier, ArmorModifier FROM creature_template'):
        ac[int(entry)] = (name, int(cls), int(exp), float(dm), float(am))

    fits = defaultdict(list)      # (entry, branch) -> [(level, exp, dm, armor ratio, sheets)]
    for (entry, branch, level, cls, t, mn, mx, ap, armor, updates) in query(DB,
            "SELECT c.entry, s.branch, c.level, c.class, c.attack_time, c.min_damage, c.max_damage, "
            "c.attack_power, c.armor, c.updates FROM creature_stats c JOIN sniff s ON s.id = c.sniff_id "
            "WHERE c.unit_type <> 'pet' AND c.auras = '' AND c.min_damage IS NOT NULL AND c.attack_time > 0"):
        entry, level, t = int(entry), int(level), int(t) / 1000
        if entry not in ac:
            continue
        cls = int(cls) or ac[entry][1]
        stats = levels.get((level, cls))
        if stats is None:
            continue
        bases, basearmor, ac_ap = stats
        mn, mx = float(mn), float(mx)
        if t <= 0 or mx <= mn:
            continue          # no swing timer (a totem, a vehicle seat) or no range to read
        ap = num(ap) if num(ap) is not None else ac_ap


        best = None
        for exp, base in enumerate(bases):
            if not base:
                continue
            dm = 2 * (mx - mn) / t / base
            predicted = (base + ap / 14) * dm * t
            miss = abs(predicted - mn) / max(mn, 1e-9)
            if best is None or miss < best[1]:
                best = (exp, miss)
        # max - min holds no attack power, so the modifier is exact for whichever base column is
        # right. When min cannot pick that column - the creature's attack power is its own,
        # trainers and vendors mostly - AzerothCore's column is assumed instead.
        if best is None:
            continue
        exp, fitted = (best[0], True) if best[1] <= TOLERANCE else (ac[entry][2], False)
        if not bases[exp]:
            continue
        dm = 2 * (mx - mn) / t / bases[exp]
        # The same damage, expressed against the column AzerothCore gives the creature: the value
        # that makes AzerothCore hit this hard without also changing its exp.
        ac_base = bases[min(ac[entry][2], 2)]
        dm_ac_exp = 2 * (mx - mn) / t / ac_base if ac_base else None
        armor_ratio = int(armor) / basearmor if num(armor) is not None and basearmor else None
        fits[(entry, branch)].append((level, exp, dm, armor_ratio, int(updates), fitted, dm_ac_exp, cls))

    # A branch's base tables are not AzerothCore's. TBC sheets put warriors at 1.01-1.24 of
    # basearmor, paladins at 0.82-0.93 and mages at 0.63-0.69 depending on level, where WotLK
    # sheets put all three at 1.00; below level 10, TBC damage also runs 0.93-1.50 of AzerothCore's.
    # The typical sheet for a branch, class and level shows that branch's own base, so each sheet
    # is also read against it, and what is left is the creature's. Levels without enough sheets
    # borrow from their neighbours, up to two away. Damage is compared against AzerothCore's
    # modifier, so a branch full of hand-set elites does not read as a base.
    seen = {3: defaultdict(list), 6: defaultdict(list)}
    for (entry, branch), rows in fits.items():
        for r in {(r[0], r[7]): r for r in rows}.values():
            if r[3] is not None:
                seen[3][(branch, r[7], r[0])].append(r[3])
            if r[6] is not None and ac[entry][3] == 1:
                seen[6][(branch, r[7], r[0])].append(r[6])

    def branch_base(index, branch, cls, level):
        for reach in range(3):
            pool = [x for lv in range(level - reach, level + reach + 1) for x in seen[index].get((branch, cls, lv), [])]
            if len(pool) >= MIN_NORM_SHEETS:
                return sorted(pool)[len(pool) // 2]
        return None

    def in_branch(branch, rows, index):
        own = []
        for r in rows:
            norm = branch_base(index, branch, r[7], r[0]) if r[index] is not None else None
            if norm:
                own.append(r[index] / norm)
        return f'{sorted(own)[len(own) // 2]:.3f}' if own else ''

    print('\t'.join(['entry', 'branch', 'name', 'levels', 'sheets', 'exp', 'ac_exp', 'min_check',
                     'damage_modifier', 'ac_damage_modifier', 'armor_modifier', 'ac_armor_modifier', 'verdict',
                     'damage_modifier_at_ac_exp', 'armor_modifier_in_branch', 'damage_modifier_in_branch']))
    for (entry, branch), rows in sorted(fits.items()):
        name, _, ac_exp, ac_dm, ac_am = ac[entry]
        dms = sorted(round(r[2], 3) for r in rows)
        exps = sorted({r[1] for r in rows})
        ratios = sorted(round(r[3], 3) for r in rows if r[3] is not None)
        dm = dms[len(dms) // 2]
        at_ac = sorted(r[6] for r in rows if r[6] is not None)
        dm_at_ac = f'{at_ac[len(at_ac) // 2]:.3f}' if at_ac else ''
        am = ratios[len(ratios) // 2] if ratios else ''
        am_branch = in_branch(branch, rows, 3)
        dm_branch = in_branch(branch, rows, 6)
        verdict = ('mixed' if dms[-1] - dms[0] > 0.01 * dm
                   else 'agrees' if abs(dm - ac_dm) <= 0.01 * max(dm, ac_dm) else 'differs')
        check = 'ok' if all(r[5] for r in rows) else 'ap differs' if not any(r[5] for r in rows) else 'partly'
        lv = sorted({r[0] for r in rows})
        print('\t'.join(map(str, [entry, branch, name, f'{lv[0]}-{lv[-1]}' if len(lv) > 1 else lv[0],
                                  sum(r[4] for r in rows), ','.join(map(str, exps)), ac_exp, check, f'{dm:.3f}',
                                  ac_dm, am, ac_am, verdict, dm_at_ac, am_branch, dm_branch])))


if __name__ == '__main__':
    main()
