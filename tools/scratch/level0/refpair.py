import sys, struct, numpy as np, math, collections
db=open(sys.argv[1],'rb').read(); B=2048; sec=int(sys.argv[2],16)
A=np.load(sys.argv[3])
p=db[sec*B:sec*B+8*2048]; nb=p[3]; p=p[:nb*B]
x1,y1,x2,y2=struct.unpack_from('>4i',p,0x44)
bits=''.join(f'{b:08b}' for b in p[0x74:len(p.rstrip(b'\0'))])
m=(A[:,0]>=x1)&(A[:,0]<=x2)&(A[:,1]>=y1)&(A[:,1]<=y2)
ref=sorted({((X-x1)//64,(Y-y1)//64) for X,Y in A[m]})
L=(x2-x1)//64
for w in (14,15,16):
    gaps=collections.Counter(); found=[]
    for u,v in ref:
        if u<1024 or v<1024: continue
        pu=f'{u:0{w}b}'; pv=f'{v:0{w}b}'
        a=[i for i in range(len(bits)) if bits.startswith(pu,i)]
        b=set(i for i in range(len(bits)) if bits.startswith(pv,i))
        for i in a:
            for g in range(-60,61):
                if i+g in b and g!=0: gaps[g]+=1; found.append((i,g,u,v))
    print("w",w,"top gaps (v_pos-u_pos):",gaps.most_common(6))
    g0=gaps.most_common(1)[0][0] if gaps else None
    pos=sorted(i for i,g,u,v in found if g==g0)
    print("   positions for best gap:",pos[:60])
