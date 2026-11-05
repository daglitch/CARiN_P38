import sys, struct
sys.path.insert(0, r"C:/Users/duhgl/OneDrive/Documents/P38_Nav/tools")
import carin
db=open(sys.argv[1],'rb').read(); B=2048; sec=0x1c8c2
p=db[sec*B:sec*B+8*2048]; nb=p[3]; p=p[:nb*B]
x1,y1,x2,y2=struct.unpack_from('>4i',p,0x44)
bits=''.join(f'{b:08b}' for b in p[0x74:len(p.rstrip(b'\0'))])
lines=[]
for s,ln,typ,cf,_ in carin.walk_blocks(db):
    if typ!=3 or cf: continue
    par=carin.Parcel(db,s,ln,typ)
    if par.x2<x1 or par.x1>x2 or par.y2<y1 or par.y1>y2: continue
    for attr,pts in par.polylines():
        loc=[((X-x1)//64,(Y-y1)//64) for X,Y in pts]
        if all(0<=u<=10240 and 0<=v<=10240 for u,v in loc) and len(loc)>2: lines.append(loc)
print("L3 polylines in tile:",len(lines))
for loc in lines[:6]:
    d=[(b[0]-a[0],b[1]-a[1]) for a,b in zip(loc,loc[1:])]
    print(loc[:6], "deltas", d[:6])
    # search literal of first interior point and the first delta as 10+10
    for (u,v) in loc[1:3]:
        pat='1100'+f'{u:014b}'+'1100'+f'{v:014b}'
        print("   literal",(u,v),"at",[i for i in range(len(bits)) if bits.startswith(pat,i)])
