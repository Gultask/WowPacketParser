# Raw .pkt (V3_1) scan for chat/strings when WPP's opcode table lacks SMSG_CHAT (e.g. 3.4.1).
# usage: python sniff-chat-scan.py file.pkt "Rocknot" "kisses" [--op 0x2bad]
# prints UTC time, opcode, first body byte (chat type for SMSG_CHAT: 12 say, 14 yell, 16 emote), strings
import datetime, re, struct, sys
args = sys.argv[1:]
op_filter = None
if '--op' in args:
    i = args.index('--op'); op_filter = int(args[i + 1], 16); del args[i:i + 2]
path, keys = args[0], [k.encode() for k in args[1:]]
f = open(path, 'rb').read()
assert f[:3] == b'PKT'
p = 3 + 2 + 1 + 4 + 4 + 40
st, stick, addl = struct.unpack_from('<IIi', f, p); p += 12 + addl
while p + 20 <= len(f):
    d, ci, tick, adds, ln = struct.unpack_from('<IiIii', f, p); p += 20 + adds
    op = struct.unpack_from('<i', f, p)[0]; body = f[p + 4:p + ln]; p += ln
    if d != 0x47534D53 or (op_filter is not None and op != op_filter) or not any(k in body for k in keys):
        continue
    t = datetime.datetime.fromtimestamp(st + (tick - stick) / 1000, datetime.UTC).strftime('%H:%M:%S.%f')[:-3]
    print(t, hex(op), body[0] if body else '', [s.decode('latin1') for s in re.findall(rb'[ -~]{5,}', body)[:4]])
