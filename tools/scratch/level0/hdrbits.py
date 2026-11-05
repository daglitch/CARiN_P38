import sys, struct
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
    if n%1700!=3: continue
    words=(secs[14][0]+4*secs[14][1])//2
    bits=''.join(f'{b:08b}' for b in p[q:q+40])
    pat=f'{words:024b}'
    pos=bits.find(pat)
    end=len(p.rstrip(b'\0'))
    print(f"{s:#07x} b={p[0x74]},{p[0x75]} words={words} pos={pos} streambits={(end-q)*8}")
    print("   ",bits[:pos] if pos>=0 else '?', "|", bits[pos+24:pos+24+110] if pos>=0 else bits[:140])
