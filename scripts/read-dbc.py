#!/usr/bin/env python3
"""Minimal WDBC reader. Prints selected fields of a 3.3.5a .dbc as TSV.

The client's own Map.dbc is the only authority on which maps exist in 3.3.5, and
AzerothCore ships map_dbc empty, so the allow-list the ingest gate needs is read
straight out of the client install rather than hand-typed.
"""
import struct, sys

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
