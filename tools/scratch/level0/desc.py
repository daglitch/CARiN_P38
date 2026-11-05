import sys, struct
sys.path.insert(0, r"C:/Users/duhgl/OneDrive/Documents/P38_Nav/tools")
import carin
db=open(sys.argv[1],'rb').read(); B=2048
n=0
def body_start(p):
    secs=[struct.unpack_from('>2H',p,8+4*i) for i in range(15)]
    q=0x74; np_=p[q]; q+=2+4*np_
    n9=secs[9][1]; n10=secs[10][1]; n11=secs[11][1]; n12=secs[12][1]
    return q, secs
for s,ln,typ,cf,us in carin.walk_blocks(db):
    if typ!=0 or not cf: continue
    n+=1
    if n%1500!=7: continue
    p=db[s*B:(s+ln)*B]; q,secs=body_start(p)
    c=[c for o,c in secs]
    # sizes of 8-byte record sections between sec9..sec12 in unpacked
    sz=[secs[i+1][0]-secs[i][0] if i<14 else 0 for i in range(15)]
    print(f"{s:#07x} b0b1={p[0x74]:2d},{p[0x75]} cnt={c} sz9-12={sz[9:13]}")
    print("    after pairs:", p[q:q+8*0+64].hex(' ',2))
