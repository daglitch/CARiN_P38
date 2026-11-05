import sys, struct, collections
sys.path.insert(0, r"C:/Users/duhgl/AppData/Local/Temp/claude/ee9401b46cd2e5dd/72d0633e-bb04-492d-a17a-be6c18a1fb97/scratchpad/l0")
from nodetable import node_table
def load(db,sec):
    B=2048; p=db[sec*B:sec*B+8*B]; nb=p[3]; p=p[:nb*B]
    secs=[struct.unpack_from('>2H',p,8+4*i) for i in range(15)]
    bits=''.join(f'{b:08b}' for b in p[0x74:len(p.rstrip(b'\0'))])
    return p,secs,bits
def chain(bits,secs,lo,hi,shift=1):
    s5,n5=secs[5]; s6,n6=secs[6]
    nm={ (s5+8*k)>>shift:k for k in range(n5)}; nm.update({(s6+16*k)>>shift:1000+k for k in range(n6)})
    cand=[i for i in range(lo,hi) if int(bits[i:i+13],2) in nm and int(bits[i+13:i+26],2) in nm]
    best={}
    for idx,i in enumerate(cand):
        best[idx]=(1,None)
        for j in range(idx-1,-1,-1):
            g=i-cand[j]
            if g>420: break
            if 80<=g and best[j][0]+1>best[idx][0]: best[idx]=(best[j][0]+1,j)
    end=max(best,key=lambda k:best[k][0]); ch=[]
    while end is not None: ch.append(cand[end]); end=best[end][1]
    ch.reverse(); return ch,nm
