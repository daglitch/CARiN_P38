import sys, struct, numpy as np, math
db=open(sys.argv[1],'rb').read(); B=2048; sec=int(sys.argv[2],16)
A=np.load(sys.argv[3])
p=db[sec*B:sec*B+8*2048]; nb=p[3]; p=p[:nb*B]
secs=[struct.unpack_from('>2H',p,8+4*i) for i in range(15)]
x1,y1,x2,y2=struct.unpack_from('>4i',p,0x44)
q=0x96+8*secs[9][1]+8*secs[10][1]
print("marker",p[q:q+4].hex(),"secs",[(hex(o),c) for o,c in secs])
bits=''.join(f'{b:08b}' for b in p[q:len(p.rstrip(b'\0'))])
m=(A[:,0]>=x1)&(A[:,0]<=x2)&(A[:,1]>=y1)&(A[:,1]<=y2)
ref=sorted({((X-x1)//64,(Y-y1)//64) for X,Y in A[m]})
L=(x2-x1)//64; W=max(1,math.ceil(math.log2(L+1)))
print("local size",L,"W",W,"refs",len(ref),"stream bits",len(bits))
uvpos=[]
for u,v in ref:
    for w in (W,):
        pu=f'{u:0{w}b}'; pv=f'{v:0{w}b}'
        h=[i for i in range(len(bits)) if bits.startswith(pu+pv,i)]
        if h: uvpos.append((h,u,v))
print("u|v adjacent matches:",len(uvpos))
for h,u,v in sorted(uvpos,key=lambda t:t[0][0])[:40]: print("  ",h,u,v)
import collections
allbits=''.join(f'{b:08b}' for b in p[0x74:len(p.rstrip(b'\0'))])
for w in (W,W+1,16):
    hu=collections.Counter(); hv=collections.Counter(); tu=tv=0
    for u,v in ref:
        pu=f'{u:0{w}b}'; pv=f'{v:0{w}b}'
        a=[i for i in range(len(allbits)) if allbits.startswith(pu,i)]
        b=[i for i in range(len(allbits)) if allbits.startswith(pv,i)]
        tu+=len(a); tv+=len(b)
        for i in a: hu[i//2000]+=1
        for i in b: hv[i//2000]+=1
    exp=len(ref)*len(allbits)/2**w
    print(f"w={w}: u hits {tu}, v hits {tv}, random expect {exp:.0f}")
    print("   u by 2000:",[hu.get(k,0) for k in range(len(allbits)//2000+1)])
    print("   v by 2000:",[hv.get(k,0) for k in range(len(allbits)//2000+1)])
