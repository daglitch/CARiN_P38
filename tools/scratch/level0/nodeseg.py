import sys, struct, numpy as np
db=open(sys.argv[1],'rb').read(); B=2048; sec=0x1c8c2
A=np.load(sys.argv[2])
p=db[sec*B:sec*B+8*2048]; nb=p[3]; p=p[:nb*B]
x1,y1,x2,y2=struct.unpack_from('>4i',p,0x44)
bits=''.join(f'{b:08b}' for b in p[0x74:len(p.rstrip(b'\0'))])
m=(A[:,0]>=x1)&(A[:,0]<=x2)&(A[:,1]>=y1)&(A[:,1]<=y2)
ref={((X-x1)//64,(Y-y1)//64) for X,Y in A[m]}
lo,hi=int(sys.argv[3]),int(sys.argv[4])
lits=[]
for i in range(lo,hi):
    if bits[i:i+4]=='1100' and bits[i+18:i+22]=='1100':
        u=int(bits[i+4:i+18],2); v=int(bits[i+22:i+36],2)
        if u<=10240 and v<=10240: lits.append((i,u,v))
for (a,u,v),(b,_,_) in zip(lits,lits[1:]+[(hi,0,0)]):
    print(f"{a} ({u},{v}){'*' if (u,v) in ref else ' '} +{b-a-36}: {bits[a+36:b]}")
