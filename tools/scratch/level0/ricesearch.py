import sys, itertools
db=open(sys.argv[1],'rb').read(); B=2048; sec=0x1c8c2
p=db[sec*B:sec*B+8*2048]; nb=p[3]; p=p[:nb*B]
print("b0,b1 =",p[0x74],p[0x75])
bits=''.join(f'{b:08b}' for b in p[0x74:len(p.rstrip(b'\0'))])
def rice(n,k,unary):   # n>=0
    q=n>>k; r=n&((1<<k)-1)
    u=('1'*q+'0') if unary=='1s' else ('0'*q+'1')
    return u+(f'{r:0{k}b}' if k else '')
def code(v,k,unary,sign):
    if sign=='zz': return rice((v<<1) if v>=0 else ((-v<<1)-1),k,unary)
    if sign=='zz2': return rice((v<<1) if v>0 else ((-v<<1)|1) if v<0 else 0,k,unary)
    s='1' if v<0 else '0'
    if sign=='pre': return s+rice(abs(v),k,unary)
    if sign=='post': return rice(abs(v),k,unary)+s
    if sign=='mid':
        c=rice(abs(v),k,unary); q=abs(v)>>k
        return c[:q+1]+s+c[q+1:]
seqs=[[36,-47,16,-14],[53,-41,294,-110],[113,-53,39,-27],[293,-112,214,-9],[-17,458,52,279],[-8,-62,12,-169]]
for seq in seqs:
    for k in range(0,12):
        for unary in ('1s','0s'):
            for sign in ('zz','zz2','pre','post','mid'):
                # orders: dx,dy interleaved or dy,dx
                for order in ('xy','yx'):
                    vals=seq if order=='xy' else [seq[1],seq[0],seq[3],seq[2]]
                    pat=''.join(code(v,k,unary,sign) for v in vals)
                    if len(pat)<24: continue
                    h=bits.find(pat)
                    if h>=0: print(seq,"k",k,unary,sign,order,"len",len(pat),"at",h)
