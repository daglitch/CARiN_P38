import sys, struct, numpy as np
db=open(sys.argv[1],'rb').read(); B=2048; sec=int(sys.argv[3],16)
A=np.load(sys.argv[2])
p=db[sec*B:sec*B+8*2048]; nb=p[3]; p=p[:nb*B]
x1,y1,x2,y2=struct.unpack_from('>4i',p,0x44)
bits=''.join(f'{b:08b}' for b in p[0x74:len(p.rstrip(b'\0'))])
m=(A[:,0]>=x1)&(A[:,0]<=x2)&(A[:,1]>=y1)&(A[:,1]<=y2)
ref={((X-x1)//64,(Y-y1)//64) for X,Y in A[m]}
W=14
def run(pos,K,LW,verbose=False,maxn=200):
    u=v=0; n=0; hits=0; out=[]
    def val(prev,pos):
        b=bits[pos:pos+4]
        if b[:1]=='0': return prev+int(bits[pos+1:pos+8],2),pos+8
        if b[:2]=='10': return prev-int(bits[pos+2:pos+9],2),pos+9
        if b=='1100': return int(bits[pos+4:pos+4+W],2),pos+4+W
        return None,pos
    while n<maxn:
        s=pos
        u2,pos=val(u,pos)
        if u2 is None: break
        v2,pos=val(v,pos)
        if v2 is None: break
        u,v=u2,v2
        link=int(bits[pos:pos+LW],2); F=bits[pos+LW]; pos+=LW+1
        x=None
        if F=='1': x=bits[pos:pos+K]; pos+=K
        if not(0<=u<=10240 and 0<=v<=10240): break
        hits+=(u,v) in ref; n+=1
        out.append((s,u,v,link,x,(u,v) in ref))
    return n,hits,out,pos
best=[]
for LW in (7,8,9):
    for K in range(0,20):
        n,h,_,_=run(34366,K,LW)
        best.append((h,n,LW,K))
best.sort(reverse=True); print(best[:6])
h,n,LW,K=best[0]
n,h,out,pos=run(34366,K,LW)
for s,u,v,l,x,r in out: print(s,(u,v),"link",l,"x",x,"REF" if r else "")
print("stopped at",pos,bits[pos:pos+40])
