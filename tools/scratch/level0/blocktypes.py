import sys, struct, re
sys.path.insert(0, r"C:/Users/duhgl/OneDrive/Documents/P38_Nav/tools")
import carin
db=open(sys.argv[1],'rb').read(); B=2048
seen={}
for s,ln,typ,cf,us in carin.walk_blocks(db):
    k=(typ,cf)
    if k in seen: continue
    seen[k]=s
    p=db[s*B:(s+ln)*B]
    txt=re.findall(rb'[a-z][a-z \',.-]{4,}',p)
    print(f"type {typ:#04x} comp {cf} sector {s:#x} n={ln} us={us} text={[t.decode() for t in txt[:6]]}")
    if cf: print("    ",p[:0x60].hex(' ',2))
