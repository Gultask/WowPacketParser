"""
Measures creature_template.DamageModifier from the melee a creature was seen swinging.

AzerothCore's creature damage (StatSystem.cpp, Creature::CalculateMinMaxDamage):

    min = (damage_base       + AP/14 * BaseVariance) * DamageModifier * attackTime/1000
    max = (damage_base * 1.5 + AP/14 * BaseVariance) * DamageModifier * attackTime/1000

damage_base and AP come from creature_classlevelstats for the creature's level and class, the
damage_base column picked by creature_template.exp. OriginalDamage in SMSG_ATTACKER_STATE_UPDATE
is the swing before the victim's armor and block, so every white hit is a draw from
DamageModifier * U[min, max] whoever it landed on. Normal mobs measured against this sit at 1.00
to within 3%, which is what anchors the scale: AzerothCore's classlevelstats already is the 1.

Per entry and branch, hits from every level and attack time are brought to one scale by dividing
by the formula at DamageModifier 1, and the multiplier is read off both ends of the range:

    k_hi = top of the range    / formula max       (robust to debuffs, which only lower damage)
    k_lo = bottom of the range / formula min       (robust to buffs, which only raise it)

each with the uniform endpoint correction, range / (n - 1). Where the two agree, the sample is one
clean state; where they do not, something the aura filter missed is in it.

A player's summon is left out, since it hits with its owner's stats behind it. Swings are kept
only from states where the attacker carried no aura that moves melee damage or swing time, going
by the aura types below in the 3.3.5 client's Spell.dbc (read-dbc.DBC_DIR). A spell id Spell.dbc
does not have is treated as relevant, since a Classic re-release can reuse or add ids and nothing
says what they do.

  python scripts/melee-multiplier.py [ingest_db] [min_hits] > multipliers.tsv
"""
import os, sys, json, subprocess
from importlib import import_module

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
read_dbc = import_module('read-dbc')
from collections import defaultdict

DB = sys.argv[1] if len(sys.argv) > 1 else 'wpp_ingest'
MIN_HITS = int(sys.argv[2]) if len(sys.argv) > 2 else 10

# 3.3.5 AuraType values that change a melee swing's damage or its timing.
RELEVANT_AURAS = {
    13,   # MOD_DAMAGE_DONE
    79,   # MOD_DAMAGE_PERCENT_DONE
    99,   # MOD_ATTACK_POWER
    166,  # MOD_ATTACK_POWER_PCT
    138,  # MOD_MELEE_HASTE
    192,  # MOD_MELEE_RANGED_HASTE
    193,  # MELEE_SLOW
    319,  # MOD_MELEE_HASTE_3
}


def tolerance(n):
    """How far apart the two ends may sit and still be one state: 2% for rounding and formula
    slack, plus three standard errors. Each end is off by about the range's width over n, and the
    width is a third of the top end at most."""
    return 0.02 + 3 * 0.33 / n


def query(db, sql):
    out = subprocess.run(['mysql', '-uroot', '-proot', db, '--batch', '-N', '-e', sql],
                         capture_output=True, text=True, check=True).stdout
    return [line.split('\t') for line in out.splitlines() if line]


def relevant_spells():
    return read_dbc.spell_aura_types(RELEVANT_AURAS)


def main():
    relevant, known = relevant_spells()

    stats = {}
    for lvl, cls, db0, db1, db2, ap in query('acore_world',
            'SELECT level, class, damage_base, damage_exp1, damage_exp2, attackpower '
            'FROM creature_classlevelstats'):
        stats[(int(lvl), int(cls))] = ([float(db0), float(db1), float(db2)], float(ap))

    templates = {}
    for entry, name, cls, exp, rank, dm, bv, bat in query('acore_world',
            'SELECT entry, name, unit_class, exp, `rank`, DamageModifier, BaseVariance, BaseAttackTime '
            'FROM creature_template'):
        templates[int(entry)] = (name, int(cls), int(exp), int(rank), float(dm), float(bv), int(bat))

    # Per branch: a TBC Classic capture and a WotLK Classic one are different servers' numbers.
    rows = query(DB, "SELECT m.entry, s.branch, m.level, m.attack_time, m.auras, m.originals "
                     "FROM creature_melee m JOIN sniff s ON s.id = m.sniff_id "
                     "WHERE m.kind = 'hit' AND m.school = 1 AND m.offhand = 0 AND m.melee_spell = 0 "
                     "AND m.owner NOT IN ('player', 'pet') "
                     "AND m.level > 0 AND m.attack_time > 0 AND m.originals IS NOT NULL")

    hits = defaultdict(list)          # (entry, branch) -> [(x / formula max, x / formula min)]
    dirty = defaultdict(int)
    times = defaultdict(set)
    levels = defaultdict(set)
    for entry, branch, level, t, auras, originals in rows:
        entry, level, t = int(entry), int(level), int(t)
        key = (entry, branch)
        tpl = templates.get(entry)
        if tpl is None or (level, tpl[1]) not in stats:
            continue

        values = json.loads(originals)
        spells = [int(s) for s in auras.split(',') if s and s != '+']
        if auras.endswith('+') or any(s in relevant or s not in known for s in spells):
            dirty[key] += len(values)
            continue

        name, cls, exp, rank, dm, bv, bat = tpl
        bases, ap = stats[(level, cls)]
        db = bases[min(exp, 2)]
        lo = (db + ap / 14 * bv) * t / 1000
        hi = (db * 1.5 + ap / 14 * bv) * t / 1000
        if lo <= 0:
            continue          # no swing timer, or a level classlevelstats gives no damage for
        hits[key].extend((x / hi, x / lo) for x in values if x > 0)
        times[key].add(t)
        levels[key].add(level)

    print('\t'.join(['entry', 'branch', 'name', 'rank', 'exp', 'levels', 'attack_time', 'ac_attack_time',
                     'hits', 'dirty_hits', 'k_hi', 'k_lo', 'k', 'ac_dm', 'verdict']))
    for key in sorted(hits):
        entry, branch = key
        pts = hits[key]
        n = len(pts)
        if n < MIN_HITS:
            continue

        name, cls, exp, rank, dm, bv, bat = templates[entry]
        top = [p[0] for p in pts]
        bottom = [p[1] for p in pts]
        k_hi = max(top) + (max(top) - min(top)) / (n - 1)
        k_lo = min(bottom) - (max(bottom) - min(bottom)) / (n - 1)
        k = (k_hi + k_lo) / 2

        if abs(k_hi - k_lo) > tolerance(n) * k:
            verdict = 'mixed'
        elif abs(k - dm) <= tolerance(n) * max(k, dm):
            verdict = 'agrees'
        else:
            verdict = 'differs'

        lv = sorted(levels[key])
        print('\t'.join(map(str, [
            entry, branch, name, rank, exp, f'{lv[0]}-{lv[-1]}' if len(lv) > 1 else lv[0],
            ','.join(map(str, sorted(times[key]))), bat, n, dirty[key],
            f'{k_hi:.3f}', f'{k_lo:.3f}', f'{k:.2f}', dm, verdict])))


if __name__ == '__main__':
    main()
