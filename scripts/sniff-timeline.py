import re,sys
f,t0,t1=sys.argv[1],sys.argv[2],sys.argv[3]; keys=sys.argv[4].split(',')
hdr=re.compile(r'^(ServerToClient|ClientToServer): (\S+).*Time: \S+ (\d\d:\d\d:\d\d\.\d+) Number: (\d+)')
gre=re.compile(r'Full: 0x([0-9A-F]+)(?: \S+ \S+ Map: \d+ Entry: (\d+))?')
def sg(s):
    m=gre.search(s)
    if not m: return '?'
    return (m.group(2) or '')+'/'+m.group(1)[-6:]
def prim(op,lines):
    for l in lines:
        if re.match(r'^(MoverGUID|GUID|\(Cast\) CasterGUID|UnitGUID|SourceGUID|Guid|Unit|CasterGUID):',l) or l.startswith('(Cast) CasterGUID'): return l
    return ''
def show(h,body):
    op=h.group(2); t=h.group(3)
    if op=='SMSG_UPDATE_OBJECT':
        blocks={}
        for l in body:
            m=re.match(r'^(\(Destroyed\) )?\[(\d+)\]',l)
            if m: blocks.setdefault((m.group(1) or '')+m.group(2),[]).append(l)
        for k,b in blocks.items():
            g=[l for l in b if 'Object Guid' in l or 'ObjectGUID' in l]
            if not g or not any(x in g[0] for x in keys): continue
            kept=[re.sub(r'^\[\d+\] ','',l) for l in b if re.search(r'UpdateType|] State:|DynamicFlags|FactionTemplate|\] Flags|Flags2|EmoteState|Target:|StandState|Health:|Stationary Position|NpcFlags|Sheathe|SpellID|AuraState',l) and 'Guid' not in l]
            print(t,'UPD',sg(g[0]),'|',('Destroyed ' if 'Destroyed' in k else '')+'; '.join(kept))
        return
    p=prim(op,body)
    if not p or not any(x in p for x in keys): return
    s='\n'.join(body)
    ex=[]
    if op=='SMSG_ON_MONSTER_MOVE':
        pos=re.search(r'^Position: (.*)$',s,re.M); ex.append('from '+pos.group(1) if pos else '')
        for m in re.finditer(r'\] Points: (.*)$',s,re.M): ex.append('-> '+m.group(1))
        m=re.search(r'MoveTime: (\d+)',s); ex.append('mt='+m.group(1))
        m=re.search(r'FacingGUID: (.*)$',s,re.M)
        if m: ex.append('faceG '+sg(m.group(1)))
        m=re.search(r'Face: (.*)$',s,re.M); ex.append('face '+m.group(1))
        m=re.search(r'\(MovementSpline\) Flags: (.*)$',s,re.M); ex.append('fl '+m.group(1))
    elif op=='SMSG_EMOTE':
        ex.append(re.search(r'Emote ID: (.*)$',s,re.M).group(1))
    elif op in('SMSG_SPELL_START','SMSG_SPELL_GO'):
        ex.append('spell '+re.search(r'SpellID: (\d+)',s).group(1))
        for m in re.finditer(r'HitTarget: (.*)$',s,re.M): ex.append('hit '+sg(m.group(1)))
        m=re.search(r'\(Target\) Unit: (.*)$',s,re.M)
        if m: ex.append('tgt '+sg(m.group(1)))
    elif op=='SMSG_AURA_UPDATE':
        for m in re.finditer(r'\[(\d+)\] (SpellID|HasAura|Duration|Slot): (\S+)',s): ex.append(m.group(2)+'='+m.group(3))
    elif op=='SMSG_CHAT':
        for m in re.finditer(r'^(Text|Chat type|SenderName|Emote ID|BroadcastText.*?): (.*)$',s,re.M): ex.append(m.group(1)+'='+m.group(2))
    else:
        ex.append(' '.join(body[:6]))
    print(t,op.replace('SMSG_',''),sg(p),'|',' '.join(ex))
cur=None;buf=[]
with open(f,encoding='utf-8',errors='replace') as fh:
    for line in fh:
        m=hdr.match(line)
        if m:
            if cur and t0<=cur.group(3)<=t1: show(cur,buf)
            if cur and cur.group(3)>t1: break
            cur=m;buf=[]
        else: buf.append(line.rstrip('\n'))
