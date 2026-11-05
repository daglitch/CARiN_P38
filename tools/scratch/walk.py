import sys, struct, collections, json
d=open(sys.argv[1],'rb').read(); B=2048; n=len(d)//B
s=0; stats=collections.Counter(); first={}; blocks=[]; bad=0
while s<n:
    bid,typ,cf,us=struct.unpack('>IHBB',d[s*B:s*B+8])
    if bid>>8!=s or (bid&0xff)==0:
        bad+=1; s+=1; continue
    ln=bid&0xff
    stats[(typ,cf)]+=1; first.setdefault((typ,cf),(s,us))
    blocks.append((s,ln,typ,cf,us)); s+=ln
print("blocks",len(blocks),"unaligned sectors skipped",bad)
for (t,c),v in sorted(stats.items()): print(f"type {t:#06x} comp {c} count {v} first sector {first[(t,c)][0]:#x} usz {first[(t,c)][1]}")
json.dump(blocks,open(sys.argv[2],'w'))
