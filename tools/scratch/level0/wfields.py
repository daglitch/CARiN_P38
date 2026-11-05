import sys, struct, math
sys.path.insert(0, r"C:/Users/duhgl/OneDrive/Documents/P38_Nav/tools")
import carin
db=open(sys.argv[1],'rb').read(); B=2048
n=0
for s,ln,typ,cf,us in carin.walk_blocks(db):
    if typ!=0 or not cf: continue
    p=db[s*B:(s+ln)*B]; secs=[struct.unpack_from('>2H',p,8+4*i) for i in range(15)]
    q=0x96+8*secs[9][1]+8*secs[10][1]
    if p[q:q+4]!=b'\x80\0\0\0': continue
    n+=1
    if n%2500!=3: continue
    W=math.ceil(math.log2(us*2048))
    bits=''.join(f'{b:08b}' for b in p[q:q+120])
    f=[int(bits[i:i+W],2) for i in range(0,W*40,W)]
    print(f"{s:#07x} us={us} W={W} secs={[(hex(o),c) for o,c in secs]}")
    print("    fields:",[hex(x) for x in f[:40]])
