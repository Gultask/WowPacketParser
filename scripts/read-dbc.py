#!/usr/bin/env python3
"""Minimal WDBC reader. Prints selected fields of a 3.3.5a .dbc as TSV.

The client's own Map.dbc is the only authority on which maps exist in 3.3.5, and
AzerothCore ships map_dbc empty, so the allow-list the ingest gate needs is read
straight out of the client install rather than hand-typed.
"""
import os, struct, sys

# The 3.3.5a client's DBC folder: the authority on what a spell id does.
DBC_DIR = r'C:\Azeroth-WoW\dbc'

# Spell.dbc field indices (3.3.5a, 234 fields): Id, then EffectApplyAuraName[3].
SPELL_ID = 0
SPELL_AURA_NAMES = (95, 96, 97)

def read_dbc(path):
    with open(path, 'rb') as f:
        data = f.read()
    if data[:4] != b'WDBC':
        raise SystemExit(f'{path}: not a WDBC file')
    count, fields, rec_size, str_size = struct.unpack_from('<4I', data, 4)
    body = 20
    strings = body + count * rec_size
    rows = []
    for i in range(count):
        off = body + i * rec_size
        rows.append(struct.unpack_from('<%dI' % fields, data, off))
    return rows, data[strings:strings + str_size], fields, rec_size

def spell_aura_types(aura_types, dbc_dir=DBC_DIR):
    """Returns (spells applying any of aura_types, every spell id in Spell.dbc)."""
    rows, _, _, _ = read_dbc(os.path.join(dbc_dir, 'Spell.dbc'))
    wanted = set(aura_types)
    relevant = {r[SPELL_ID] for r in rows if any(r[i] in wanted for i in SPELL_AURA_NAMES)}
    return relevant, {r[SPELL_ID] for r in rows}

def s(block, off):
    if off == 0 or off >= len(block):
        return ''
    end = block.index(b'\0', off)
    return block[off:end].decode('utf-8', 'replace')

if __name__ == '__main__':
    path = sys.argv[1]
    cols = [int(x) for x in sys.argv[2].split(',')] if len(sys.argv) > 2 else None
    strcols = set(int(x) for x in sys.argv[3].split(',')) if len(sys.argv) > 3 else set()
    rows, block, fields, rec_size = read_dbc(path)
    print(f'# {path}: {len(rows)} rows, {fields} fields, {rec_size} bytes/record', file=sys.stderr)
    if cols is None:
        sys.exit(0)
    for r in rows:
        out = []
        for c in cols:
            v = r[c]
            out.append(s(block, v) if c in strcols else str(v))
        print('\t'.join(out))
