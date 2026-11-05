import sys, struct
sys.path.insert(0, r"C:/Users/duhgl/OneDrive/Documents/P38_Nav/tools")
import carin
db=open(sys.argv[1],'rb').read(); B=2048; sec=int(sys.argv[2],16); nbytes=int(sys.argv[3])
p=db[sec*B:sec*B+4096]; secs=[struct.unpack_from('>2H',p,8+4*i) for i in range(15)]
x1,y1,x2,y2=struct.unpack_from('>4i',p,0x44)
q=0x96+8*secs[9][1]+8*secs[10][1]
bits=''.join(f'{b:08b}' for b in p[q:q+nbytes])
ref=set()
for s,ln,typ,cf,_ in carin.walk_blocks(db):
    if typ not in (1,2,3) or cf: continue
    par=carin.Parcel(db,s,ln,typ)
    if par.x2<x1 or par.x1>x2 or par.y2<y1 or par.y1>y2: continue
    for o,(u,v,lk,fl) in par.nodes().items():
        X,Y=par.glob(u,v)
        if x1<=X<=x2 and y1<=Y<=y2: ref.add(((X-x1)//64,(Y-y1)//64, typ))
print("tile",hex(x1),hex(y1),"ref nodes",len(ref))
W=int(sys.argv[4])
for u,v,t in sorted(ref):
    pu=f'{u:0{W}b}'; pv=f'{v:0{W}b}'
    hu=[i for i in range(len(bits)) if bits.startswith(pu,i)]
    hv=[i for i in range(len(bits)) if bits.startswith(pv,i)]
    huv=[i for i in hu if bits.startswith(pv,i+W)]
    print(f"L{t} u={u:5d} v={v:5d}  u@{hu[:8]} v@{hv[:8]} uv-adjacent@{huv}")
