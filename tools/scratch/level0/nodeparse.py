import sys, struct, numpy as np
db=open(sys.argv[1],'rb').read(); B=2048; sec=int(sys.argv[3],16)
A=np.load(sys.argv[2])
p=db[sec*B:sec*B+8*2048]; nb=p[3]; p=p[:nb*B]
x1,y1,x2,y2=struct.unpack_from('>4i',p,0x44)
secs=[struct.unpack_from('>2H',p,8+4*i) for i in range(15)]
bits=''.join(f'{b:08b}' for b in p[0x74:len(p.rstrip(b'\0'))])
m=(A[:,0]>=x1)&(A[:,0]<=x2)&(A[:,1]>=y1)&(A[:,1]<=y2)
ref={((X-x1)//64,(Y-y1)//64) for X,Y in A[m]}
W=14
pos=int(sys.argv[4]); u=v=0; n=0
def val(prev):
    global pos
    b=bits[pos:pos+4]
    if b[0]=='0': d=int(bits[pos+1:pos+8],2); pos+=8; return prev+d,'+'
    if b[:2]=='10': d=int(bits[pos+2:pos+9],2); pos+=9; return prev-d,'-'
    if b=='1100': a=int(bits[pos+4:pos+4+W],2); pos+=4+W; return a,'L'
    raise ValueError(b)
hits=0
while n<int(sys.argv[5]):
    start=pos
    try:
        u,cu=val(u); v,cv=val(v)
    except ValueError as e:
        print(f"STOP at {start}: unknown prefix {e}  context {bits[start:start+40]}"); break
    link=int(bits[pos:pos+8],2); F=bits[pos+8]; pos+=9
    fl=None
    if F=='1': fl=int(bits[pos:pos+16],2); pos+=16
    k=(u,v) in ref; hits+=k
    print(f"{start:6d} N{n:<3d} ({u:5d},{v:5d}) {cu}{cv} link {link:3d} flags {hex(fl) if fl is not None else '-':7s} {'REF' if k else ''}")
    n+=1
print("nodes",n,"ref hits",hits, "secs nodes/edges", secs[5][1], secs[6][1])
