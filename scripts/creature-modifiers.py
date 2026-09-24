"""
Settles creature_template.DamageModifier, ExperienceModifier and ArmorModifier from the corpus,
one value per entry, and writes the UPDATEs that bring AzerothCore in line.

AzerothCore's own values are mostly defaults, not decisions: DamageModifier sits on 1 for normal
mobs, 7.5 / 4.6 / 13 for elites and 35 for bosses, ArmorModifier on 1 everywhere. Measured
against the corpus, the default holds for 81% of normal mobs, 40% of elites and 5 of 36 bosses.
A measured value therefore replaces AzerothCore's whenever the two differ by more than the
measurement's own error - the same rule as wander_distance, where the column is filler.

Evidence, best first (the fits are separate scripts; run them into one folder first):

  DamageModifier      creature-stats.py     the server's own damage range off a stat sheet; exact
                      melee-multiplier.py   white swings, OriginalDamage; ~3%
  ExperienceModifier  xp-modifier.py        kill XP over the player's own bonus; ~3%
  ArmorModifier       creature-stats.py     armor off a stat sheet; exact

creature-armor.py is deliberately not used: armor read back out of damage reduction runs a median
14% below AzerothCore with only a quarter of rows within 5%, a bias not a spread.

Only Classic, TBC and WotLK captures count. Those three agree on melee (95-99% of shared entries
within 5%); Cataclysm and Mists run 1.8x higher on the same entries, and Retail level-scales.
Where several branches measured an entry, the latest of the three wins.

A sheet's modifier is taken at AzerothCore's own damage column (exp), so the result reproduces
the observed damage without also changing exp. Kill XP is measured against the sniff's rank and
re-expressed against AzerothCore's, since AzerothCore doubles XP for its own idea of elite.

Values snap to the grid Blizzard's own numbers sit on (0.05 below 2, 0.25 below 10, 0.5 above)
when that is within the measurement's error, and are otherwise kept to two decimals.

  python scripts/creature-modifiers.py <folder with melee.tsv, stats.tsv, xp.tsv> > modifiers.tsv
      writes <folder>/creature-modifiers.sql beside the report
"""
import csv, os, subprocess, sys
from collections import defaultdict

FOLDER = sys.argv[1] if len(sys.argv) > 1 else '.'
BRANCHES = ('WotLK', 'TBC', 'Classic')      # preference order
MIN_HITS = 30                               # clean white swings for a melee reading
MIN_KILLS = 3
MIN_AT_MEDIAN = 0.5                          # share of kills at the median; less means grouping
ELITE_RANKS = {1, 2, 3}
HALF_XP = (0.45, 0.55)                       # a duo's share: see ExperienceModifier below

# Error each source carries: within it, AzerothCore's value stands.
TOLERANCE = {'sheet': 0.02, 'melee': 0.05, 'xp': 0.05, 'armor': 0.03}


def query(db, sql):
    out = subprocess.run(['mysql', '-uroot', '-proot', db, '--batch', '-N', '-e', sql],
                         capture_output=True, text=True, check=True).stdout
    return [line.split('\t') for line in out.splitlines() if line]


def load(name):
    path = os.path.join(FOLDER, name)
    with open(path, encoding='utf-8') as f:
        return [r for r in csv.DictReader(f, delimiter='\t') if r['branch'] in BRANCHES]


def snap(x, error):
    grid = 0.05 if x < 2 else 0.25 if x < 10 else 0.5
    g = round(round(x / grid) * grid, 2)
    return g if g > 0 and abs(g - x) <= error * x else round(x, 2)


def best(rows, key):
    """The row from the latest branch; within a branch, the best supported one."""
    by = defaultdict(list)
    for r in rows:
        by[int(r['entry'])].append(r)
    return {e: min(rs, key=lambda r: (BRANCHES.index(r['branch']), -key(r))) for e, rs in by.items()}


def main():
    ac = {}
    for entry, name, rank, cls, dm, xp, am, variants in query('acore_world',
            'SELECT entry, name, `rank`, unit_class, DamageModifier, ExperienceModifier, ArmorModifier, '
            'difficulty_entry_1 + difficulty_entry_2 + difficulty_entry_3 FROM creature_template'):
        ac[int(entry)] = dict(name=name, rank=int(rank), cls=int(cls), DamageModifier=float(dm),
                              ExperienceModifier=float(xp), ArmorModifier=float(am), variants=int(variants) > 0)

    stat_rows = [r for r in load('stats.tsv') if r['verdict'] != 'mixed' and int(r['entry']) in ac]
    sheets = best(stat_rows, lambda r: int(r['sheets']))

    melee = best([r for r in load('melee.tsv') if r['verdict'] != 'mixed' and int(r['hits']) >= MIN_HITS],
                 lambda r: int(r['hits']))
    kills = best([r for r in load('xp.tsv') if r['verdict'] != 'grouped' and int(r['kills']) >= MIN_KILLS
                  and float(r['at_median']) >= MIN_AT_MEDIAN], lambda r: int(r['kills']))

    decisions = []        # entry, field, ac value, measured, new value, source, branch, evidence, note

    for entry in sorted(set(sheets) | set(melee) | set(kills)):
        if entry not in ac:
            continue
        tpl = ac[entry]

        # DamageModifier: the sheet if there is one, else the swings.
        s, m = sheets.get(entry), melee.get(entry)
        if s and s['damage_modifier_in_branch']:
            value, source, branch = float(s['damage_modifier_in_branch']), 'sheet', s['branch']
            evidence = f"{s['sheets']} sheets, exp {s['exp']} (AC {s['ac_exp']})"
            note = ''
            if m and abs(float(m['k']) / value - 1) > TOLERANCE['melee']:
                note = f"swings read {m['k']}"
            decisions.append((entry, 'DamageModifier', value, source, branch, evidence, note))
        elif m:
            decisions.append((entry, 'DamageModifier', float(m['k']), 'melee', m['branch'],
                              f"{m['hits']} swings, k {m['k_lo']}-{m['k_hi']}", ''))

        # ArmorModifier: sheets only.
        # ArmorModifier: sheets only, each read against its own branch's base armor at that class
        # and level (creature-stats.py), since TBC's is not AzerothCore's.
        if s and s['armor_modifier_in_branch']:
            decisions.append((entry, 'ArmorModifier', float(s['armor_modifier_in_branch']), 'armor', s['branch'],
                              f"{s['sheets']} sheets, {s['armor_modifier']} of basearmor", ''))

        # ExperienceModifier: the measured modifier is against the sniff's rank; AzerothCore
        # doubles for its own rank, so carry the doubling across.
        k = kills.get(entry)
        if k:
            sniff_elite = int(k['sniff_rank']) in ELITE_RANKS
            ac_elite = tpl['rank'] in ELITE_RANKS
            value = float(k['measured_modifier']) * (2 if sniff_elite else 1) / (2 if ac_elite else 1)
            note = '' if sniff_elite == ac_elite else f"sniff rank {k['sniff_rank']}, AC rank {tpl['rank']}"
            # A party of two reports the same group rate as a lone player and each gets about half,
            # so a creature mostly killed as a pair reads 0.5. Until creature_xp records party
            # size, a half is not told apart from a real 0.5 and is left alone.
            if HALF_XP[0] <= float(k['measured_modifier']) <= HALF_XP[1]:
                note = (note + '; ' if note else '') + 'half: party of two?'
            decisions.append((entry, 'ExperienceModifier', value, 'xp', k['branch'],
                              f"{k['kills']} kills, {k['at_median']} at median", note))

    out = csv.writer(sys.stdout, delimiter='\t', lineterminator='\n')
    out.writerow(['entry', 'name', 'ac_rank', 'field', 'ac_value', 'measured', 'new_value', 'action',
                  'source', 'branch', 'evidence', 'note'])
    updates = defaultdict(dict)
    notes = defaultdict(list)
    for entry, field, measured, source, branch, evidence, note in decisions:
        tpl = ac[entry]
        current = tpl[field]
        error = TOLERANCE[source]
        new = snap(measured, error)
        keep = current > 0 and abs(measured / current - 1) <= error
        # A creature with heroic or 25-player versions sends the same id on every difficulty, and
        # AzerothCore gives each its own template; nothing recorded says which one was measured.
        if tpl['variants']:
            note = (note + '; ' if note else '') + 'difficulty unknown'
        held = 'party of two' in note or 'difficulty unknown' in note
        action = 'keep' if keep or new == current else 'hold' if held else 'set'
        out.writerow([entry, tpl['name'], tpl['rank'], field, current, f'{measured:.3f}',
                      new if action == 'set' else current, action, source, branch, evidence, note])
        if action == 'set':
            updates[entry][field] = new
            notes[entry].append(f'{field} {current:g} -> {new:g} ({source}, {branch}, {evidence})')

    sql = os.path.join(FOLDER, 'creature-modifiers.sql')
    with open(sql, 'w', encoding='utf-8', newline='\n') as f:
        f.write('-- creature_template modifiers measured from the sniff corpus (scripts/creature-modifiers.py)\n')
        f.write(f'-- {len(updates)} entries\n\n')
        for entry in sorted(updates):
            sets = ', '.join(f'`{k}` = {v:g}' for k, v in sorted(updates[entry].items()))
            f.write(f"-- {ac[entry]['name']}: {'; '.join(notes[entry])}\n")
            f.write(f'UPDATE `creature_template` SET {sets} WHERE `entry` = {entry};\n')
    print(f'# {len(updates)} entries to update, written to {sql}', file=sys.stderr)


if __name__ == '__main__':
    main()
