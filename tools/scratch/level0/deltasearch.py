import sys
db=open(sys.argv[1],'rb').read(); B=2048; sec=0x1c8c2
p=db[sec*B:sec*B+8*2048]; nb=p[3]; p=p[:nb*B]
bits=''.join(f'{b:08b}' for b in p[0x74:len(p.rstrip(b'\0'))])
def enc(v,w,mode):
    if mode=='tc':
        if not -(1<<(w-1))<=v<(1<<(w-1)): return None
        return f'{v & ((1<<w)-1):0{w}b}'
    if mode=='sm':
        if abs(v)>=(1<<(w-1)): return None
        return ('1' if v<0 else '0')+f'{abs(v):0{w-1}b}'
    if mode=='ms':  # magnitude then sign
        if abs(v)>=(1<<(w-1)): return None
        return f'{abs(v):0{w-1}b}'+('1' if v<0 else '0')
    if mode=='neg': # negated
        return enc(-v,w,'tc')
tests=[((36,-47),(16,-14)),((53,-41),(294,-110)),((-8,-62),(12,-169)),((113,-53),(39,-27)),((293,-112),(214,-9))]
for (d1,d2) in tests:
    for mode in ('tc','sm','ms','neg'):
        for w in range(6,14):
            a=enc(d1[0],w,mode); b=enc(d1[1],w,mode)
            if a is None or b is None: continue
            for g in range(0,5):
                for order in (0,1):
                    pat=(a+'x'*g+b) if order==0 else (b+'x'*g+a)
                    import re
                    rx=re.compile(pat.replace('x','.'))
                    hits=[m.start() for m in rx.finditer(bits)]
                    hits=[h for h in range(len(bits)) if rx.match(bits,h)]
                    exp=len(bits)/2**(2*w)
                    if hits and len(hits)<=3 and exp<0.05:
                        print(d1,mode,"w",w,"gap",g,"order",order,"hits",hits)
