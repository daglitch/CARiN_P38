import sys, struct
sys.path.insert(0, r"C:/Users/duhgl/OneDrive/Documents/P38_Nav/tools")
import carin
db=open(sys.argv[1],'rb').read(); B=2048
n=0
for s,ln,typ,cf,us in carin.walk_blocks(db):
    if typ!=0 or not cf: continue
    n+=1
    if n%2000!=1: continue
    p=db[s*B:(s+ln)*B]
    secs=[struct.unpack_from('>2H',p,8+4*i) for i in range(15)]
    q=0x74; npairs=p[q]; b2=p[q+1]; q+=2+4*npairs
    recs=0
    while True:
        r=p[q:q+8]
        if r[0] in (0,1,2) and 1<=r[3]<=8 and (r[1]|r[2]): recs+=1; q+=8
        else: break
    print(f"sec {s:#x} len {ln} unpacked {us}  pairs {npairs} b2 {b2} recs8 {recs} -> stream at {q:#x}")
    print("   counts:", [c for o,c in secs], "offs:", [hex(o) for o,c in secs][:8])
    print("   next:", p[q:q+48].hex(' '))
