import numpy as np, sys, collections, struct
d=open(sys.argv[1],'rb').read(); B=2048; n=len(d)//B
found=[]; offc=collections.Counter(); hdr=collections.Counter()
X0,X1,Y0,Y1=1240326144,1911414784,-171088640,500000000
for i in range(n):
    blk=d[i*B:(i+1)*B]
    for o in range(0,0x200,2):
        x1,y1,x2,y2=struct.unpack('>4i',blk[o:o+16])
        dx,dy=x2-x1,y2-y1
        if 0<dx<=0x800000 and 0<dy<=0x800000 and X0<=x1<X1 and Y0<=y1<Y1 and (dx&0xff)==0 and (dy&0xff)==0:
            found.append((i,o,x1,y1,x2,y2)); offc[o]+=1; hdr[blk[:4].hex()]+=1; break
print("parcels",len(found),"offsets",offc.most_common(8))
print("headers",hdr.most_common(8))
a=np.array([f[2:] for f in found],dtype=np.int64)
print("x1",a[:,0].min(),a[:,0].max()," y1",a[:,1].min(),a[:,1].max())
print("dx",collections.Counter((a[:,2]-a[:,0]).tolist()).most_common(8))
print("dy",collections.Counter((a[:,3]-a[:,1]).tolist()).most_common(8))
np.save(sys.argv[2],np.array(found,dtype=np.int64))
