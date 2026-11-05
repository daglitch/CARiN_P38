import sys, struct, math
sys.path.insert(0, r"C:/Users/duhgl/OneDrive/Documents/P38_Nav/tools")
import carin
db=open(sys.argv[1],'rb').read(); B=2048
cands=[]
for s,ln,typ,cf,us in carin.walk_blocks(db):
    if typ!=0 or not cf: continue
    p=db[s*B:(s+ln)*B]; secs=[struct.unpack_from('>2H',p,8+4*i) for i in range(15)]
    q=0x96+8*secs[9][1]+8*secs[10][1]
    if p[q:q+4]!=b'\x80\0\0\0': continue
    end=len(p.rstrip(b'\0'))
    cands.append((end-q,s,us,secs,q))
cands.sort()
for L,s,us,secs,q in cands[:3]:
    p=db[s*B:(s+ cands[0][2])*B]
    p=db[s*B:s*B+4096]
    W=math.ceil(math.log2(us*2048))
    print(f"\n### sector {s:#x} streamlen {L} bytes us={us} W={W}")
    print("secs",[(hex(o),c) for o,c in secs])
    print("hdr", p[:0x74].hex(' ',2))
    print("raw 0x74..q:", p[0x74:q].hex(' ',2))
    bits=''.join(f'{b:08b}' for b in p[q:q+L])
    for i in range(0,len(bits),W*6):
        row=[bits[j:j+W] for j in range(i,min(i+W*6,len(bits)),W)]
        print(f"{i:5d}", ' '.join(row), '  ', ' '.join(f'{int(r,2):#x}' for r in row))
