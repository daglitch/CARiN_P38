import sys, struct
sys.path.insert(0, r"C:/Users/duhgl/OneDrive/Documents/P38_Nav/tools")
import carin
db=open(sys.argv[1],'rb').read(); B=2048
best=None
for s,ln,typ,cf,us in carin.walk_blocks(db):
    if typ!=0 or cf: continue
    p=db[s*B:(s+ln)*B]; secs=[struct.unpack_from('>2H',p,8+4*i) for i in range(15)]
    if secs[2][1]>=25 and secs[4][1]>=40 and (best is None or ln<best[1]): best=(s,ln)
s,ln=best; p=db[s*B:(s+ln)*B]; secs=[struct.unpack_from('>2H',p,8+4*i) for i in range(15)]
print("sector",hex(s),"len",ln,[(hex(o),c) for o,c in secs])
print("hdr 0x54..0x74:",p[0x54:0x74].hex(' ',2))
for i in (0,1,2):
    o,c=secs[i]; e=secs[i+1][0]
    print(f"sec{i} @{o:#x} n={c} bytes={e-o}")
    for k in range(0,e-o,10): print("   ",p[o+k:o+k+10].hex(' ',2))
o,c=secs[3]; print("sec3",[hex(x) for x in struct.unpack_from('>%dH'%((secs[4][0]-o)//2),p,o)])
o,c=secs[4]
print("links:")
for k in range(min(c+1,12)): print("   ",p[o-8+30*k:o-8+30*k+30].hex(' ',2))
o,c=secs[5]; print("nodes:",[struct.unpack_from('>4H',p,o+8*k) for k in range(min(c,10))])
o,c=secs[7]; print("shape n",c,[struct.unpack_from('>3H',p,o+6*k) for k in range(min(c,10))])
