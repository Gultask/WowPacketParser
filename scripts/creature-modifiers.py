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

Only TBC and WotLK captures count, and WotLK wins where both measured an entry. Cataclysm and
Mists run 1.8x higher on the same entries, and Retail level-scales. Classic is left out too: it is
1.12 tuning, before 2.3 de-elited whole areas (Jintha'Alor, Mannoroc Coven), and its captures
carry no query responses to say which creatures were elite then.

A reading only counts where the branch that took it agrees with AzerothCore on whether the
creature is elite; where it does not, the rank is the question, not the modifier, and the entry is
held. Raids from before WotLK take TBC's reading instead of WotLK's: WotLK Classic's swings there
run 0.75 of TBC Anniversary's for the same creature at the same level (Karazhan's Phantom Hound,
Fiendish Imp and Malchezaar's Axes all read 0.75), and the TBC numbers are the ones kept. An entry
there that only WotLK measured is held, leaving AzerothCore's value.

A sheet's modifier is taken at AzerothCore's own damage column (exp), so the result reproduces
the observed damage without also changing exp.

Values snap to the grid Blizzard's own numbers sit on (0.05 below 2, 0.25 below 10, 0.5 above)
when that is within the measurement's error, and are otherwise kept to two decimals.

  python scripts/creature-modifiers.py <folder with melee.tsv, stats.tsv, xp.tsv> > modifiers.tsv
      writes <folder>/creature-modifiers.sql beside the report
"""
import csv, os, subprocess, sys
from collections import defaultdict
from importlib import import_module

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
read_dbc = import_module('read-dbc')

FOLDER = sys.argv[1] if len(sys.argv) > 1 else '.'
BRANCHES = ('WotLK', 'TBC')                 # preference order
MIN_HITS = 30                               # clean white swings for a melee reading
MIN_KILLS = 3
MIN_XP_SNIFFS = 2                            # one player grouped all session reads clean and wrong
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


def old_raid_maps():
    """Raid maps from before WotLK, by the 3.3.5 client's Map.dbc (InstanceType 2, expansion < 2)."""
    rows, _, _, _ = read_dbc.read_dbc(os.path.join(read_dbc.DBC_DIR, 'Map.dbc'))
    # Onyxia's Lair is on the list by Map.dbc, but 3.2 rebuilt her at level 80: WotLK is the truth there.
    return {r[0] for r in rows if r[2] == 2 and r[63] < 2} - {249}


def best(rows, key, tbc_first=frozenset()):
    """The row from the latest branch; within a branch, the best supported one. Entries in
    tbc_first take a TBC row whenever there is one."""
    by = defaultdict(list)
    for r in rows:
        by[int(r['entry'])].append(r)
    out = {}
    for e, rs in by.items():
        if e in tbc_first and any(r['branch'] == 'TBC' for r in rs):
            rs = [r for r in rs if r['branch'] == 'TBC']
        out[e] = min(rs, key=lambda r: (BRANCHES.index(r['branch']), -key(r)))
    return out


def join(*notes):
    return '; '.join(n for n in notes if n)


def main():
    ac = {}
    for entry, name, rank, cls, dm, xp, am, variants in query('acore_world',
            'SELECT entry, name, `rank`, unit_class, DamageModifier, ExperienceModifier, ArmorModifier, '
            'difficulty_entry_1 + difficulty_entry_2 + difficulty_entry_3 FROM creature_template'):
        ac[int(entry)] = dict(name=name, rank=int(rank), cls=int(cls), DamageModifier=float(dm),
                              ExperienceModifier=float(xp), ArmorModifier=float(am), variants=int(variants) > 0)

    branch_elite = {}                 # (entry, branch) -> elite in that branch's query responses
    for entry, branch, rank in query('wpp_ingest', 'SELECT t.entry, s.branch, MAX(t.`rank`) FROM creature_template t '
                                                   'JOIN sniff s ON s.id = t.sniff_id GROUP BY t.entry, s.branch'):
        branch_elite[(int(entry), branch)] = int(rank) in ELITE_RANKS

    raids = old_raid_maps()
    in_old_raid = {int(e) for (e,) in query('acore_world', 'SELECT DISTINCT id FROM creature WHERE map IN (%s)'
                                                         % ','.join(map(str, raids)))}
    in_old_raid |= {int(e) for (e,) in query('wpp_ingest', 'SELECT DISTINCT entry FROM creature_melee WHERE map IN (%s) '
                                                         'UNION SELECT DISTINCT entry FROM creature_stats WHERE map IN (%s)'
                                                         % ((','.join(map(str, raids)),) * 2))}

    def rank_note(entry, branch):
        """Why a branch's reading does not apply to AzerothCore's template, or ''."""
        elite = branch_elite.get((entry, branch))
        if elite is None or elite == (ac[entry]['rank'] in ELITE_RANKS):
            return ''
        return f"{branch} {'elite' if elite else 'normal'}, AC rank {ac[entry]['rank']}"

    stat_rows = [r for r in load('stats.tsv') if r['verdict'] != 'mixed' and int(r['entry']) in ac]
    sheets = best(stat_rows, lambda r: int(r['sheets']), in_old_raid)

    melee = best([r for r in load('melee.tsv') if r['verdict'] != 'mixed' and int(r['hits']) >= MIN_HITS],
                 lambda r: int(r['hits']), in_old_raid)
    kills = best([r for r in load('xp.tsv') if r['verdict'] != 'grouped' and int(r['kills']) >= MIN_KILLS
                  and float(r['at_median']) >= MIN_AT_MEDIAN and int(r['sniffs_at_median']) >= MIN_XP_SNIFFS],
                 lambda r: int(r['kills']))
    def old_raid_note(entry, branch):
        """A pre-WotLK raid reading that is not TBC's: WotLK Classic runs 0.75 of it there."""
        return 'pre-WotLK raid: WotLK only' if entry in in_old_raid and branch != 'TBC' else ''

    decisions = []        # entry, field, ac value, measured, new value, source, branch, evidence, note

    for entry in sorted(set(sheets) | set(melee) | set(kills)):
        if entry not in ac:
            continue
        tpl = ac[entry]

        # DamageModifier: the sheet if there is one, else the swings.
        s, m = sheets.get(entry), melee.get(entry)
        if entry in in_old_raid and m and m['branch'] == 'TBC' and s and s['branch'] != 'TBC':
            s = None          # a TBC swing reading beats a WotLK sheet in a pre-WotLK raid
        if s and s['damage_modifier_in_branch']:
            value, source, branch = float(s['damage_modifier_in_branch']), 'sheet', s['branch']
            evidence = f"{s['sheets']} sheets, exp {s['exp']} (AC {s['ac_exp']})"
            note = ''
            if m and abs(float(m['k']) / value - 1) > TOLERANCE['melee']:
                note = f"swings read {m['k']}"
            decisions.append((entry, 'DamageModifier', value, source, branch, evidence,
                              join(note, rank_note(entry, branch), old_raid_note(entry, branch))))
        elif m:
            decisions.append((entry, 'DamageModifier', float(m['k']), 'melee', m['branch'],
                              f"{m['hits']} swings, k {m['k_lo']}-{m['k_hi']}",
                              join(rank_note(entry, m['branch']), old_raid_note(entry, m['branch']))))

        # ArmorModifier: sheets only, each read against its own branch's base armor at that class
        # and level (creature-stats.py), since TBC's is not AzerothCore's.
        if s and s['armor_modifier_in_branch']:
            decisions.append((entry, 'ArmorModifier', float(s['armor_modifier_in_branch']), 'armor', s['branch'],
                              f"{s['sheets']} sheets, {s['armor_modifier']} of basearmor",
                              rank_note(entry, s['branch'])))

        # ExperienceModifier: measured against the branch's own rank, which must be AzerothCore's.
        k = kills.get(entry)
        if k:
            # A party of two reports the same group rate as a lone player and each gets about half,
            # so a creature mostly killed as a pair reads 0.5. Until creature_xp records party
            # size, a half is not told apart from a real 0.5 and is left alone.
            half = 'half: party of two?' if HALF_XP[0] <= float(k['measured_modifier']) <= HALF_XP[1] else ''
            decisions.append((entry, 'ExperienceModifier', float(k['measured_modifier']), 'xp', k['branch'],
                              f"{k['kills']} kills in {k['sniffs_at_median']} sniffs, {k['at_median']} at median",
                              join(rank_note(entry, k['branch']), half)))

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
        held = any(why in note for why in ('party of two', 'difficulty unknown', 'AC rank', 'pre-WotLK raid'))
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
