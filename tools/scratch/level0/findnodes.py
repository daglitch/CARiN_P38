import sys, struct
db=open(sys.argv[1],'rb').read(); B=2048; sec=int(sys.argv[2],16)
p=db[sec*B:sec*B+8*2048]; nb=p[3]; p=p[:nb*B]
secs=[struct.unpack_from('>2H',p,8+4*i) for i in range(15)]
x1,y1,x2,y2=struct.unpack_from('>4i',p,0x44)
import math
L=(x2-x1)//64; W=math.ceil(math.log2(L+1))
bits=''.join(f'{b:08b}' for b in p[0x74:len(p.rstrip(b'\0'))])
nn=secs[5][1]; ne=secs[6][1]
ids=[struct.unpack_from('>I',p,0x96+8*k)[0] for k in range(secs[9][1])]
idpos=sorted(i for bid in ids for i in range(len(bits)) if bits.startswith(f'{bid:032b}',i))
print("W",W,"nodes",nn,"edges",ne,"block-id positions",idpos[:30])
LW=max(1,math.ceil(math.log2(secs[4][1]+1)))
print("link width guess",LW)
def parse(start,n,U0=0,V0=0):
    u,v,pos=U0,V0,start; recs=[]
    for k in range(n):
        s=pos
        for c in (0,1):
            prev=u if c==0 else v
            b=bits[pos:pos+4]
            if b[:1]=='0': val=prev+int(bits[pos+1:pos+8],2); pos+=8
            elif b[:2]=='10': val=prev-int(bits[pos+2:pos+9],2); pos+=9
            elif b=='1100': val=int(bits[pos+4:pos+4+W],2); pos+=4+W
            else: return None
            if c==0: u2=val
            else: v2=val
        if not(0<=v2<=L and 0<=u2<=L): return None
        u,v=u2,v2
        link=int(bits[pos:pos+LW],2); F=bits[pos+LW]; pos+=LW+1
        if link>=secs[4][1]: return None
        if F=='1': pos+=11
        recs.append((s,u,v,link))
    return pos,recs
