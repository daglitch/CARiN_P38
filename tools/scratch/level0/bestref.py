import sys, struct, numpy as np, pickle
sys.path.insert(0, r"C:/Users/duhgl/OneDrive/Documents/P38_Nav/tools")
import carin
db=open(sys.argv[1],'rb').read(); B=2048
pts=[]
blocks=list(carin.walk_blocks(db))
for s,ln,typ,cf,_ in blocks:
    if typ not in (1,2,3) or cf: continue
    par=carin.Parcel(db,s,ln,typ)
    for o,(u,v,lk,fl) in par.nodes().items(): pts.append(par.glob(u,v))
A=np.array(sorted(set(pts)),dtype=np.int64); np.save(sys.argv[2],A)
print("ref nodes",len(A))
res=[]
for s,ln,typ,cf,us in blocks:
    if typ!=0 or not cf: continue
    p=db[s*B:s*B+0x80]; secs=[struct.unpack_from('>2H',p,8+4*i) for i in range(15)]
    x1,y1,x2,y2=struct.unpack_from('>4i',p,0x44)
    m=(A[:,0]>=x1)&(A[:,0]<=x2)&(A[:,1]>=y1)&(A[:,1]<=y2)
    res.append((int(m.sum()),secs[5][1],s,us,(x2-x1)//64))
res.sort(reverse=True)
for r in res[:10]: print("refs %d nodes %d sector %#x us %d localsize %d"%r)
