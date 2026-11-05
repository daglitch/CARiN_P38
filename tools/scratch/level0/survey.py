import sys, struct, math, random
sys.path.insert(0, r"C:/Users/duhgl/OneDrive/Documents/P38_Nav/tools")
sys.path.insert(0, r"C:/Users/duhgl/AppData/Local/Temp/claude/ee9401b46cd2e5dd/72d0633e-bb04-492d-a17a-be6c18a1fb97/scratchpad/l0")
import carin
from linkgrammar import parse_links
def try_parcel(db,s):
    B=2048; p=db[s*B:s*B+8*B]; nb=p[3]; p=p[:nb*B]
    secs=[struct.unpack_from('>2H',p,8+4*i) for i in range(15)]
    x1,y1,x2,y2=struct.unpack_from('>4i',p,0x44)
    L=(x2-x1)//64; W=max(1,math.ceil(math.log2(L+1)))
    b0,b1=p[0x74],p[0x75]
    bits=''.join(f'{b:08b}' for b in p[0x74:len(p.rstrip(b'\0'))])
    s4,n4=secs[4]; s5,n5=secs[5]; s6,n6=secs[6]
    if n4==0 or n5==0: return None
    shift=1 if us_bytes(p)>8192 else 0
    vp={ (s5+8*k)>>shift:k for k in range(n5)}; vp.update({(s6+16*k)>>shift:1000+k for k in range(n6)})
    LWk=max(1,math.ceil(math.log2(n4+1)))
    for BW in (16,17,18,19,20):
        for st in range(0,min(len(bits),20000)):
            if int(bits[st:st+13],2) not in vp or int(bits[st+13:st+26],2) not in vp: continue
            out,end,ok=parse_links(bits,st,n4,vp,PW=13,CW=12,DW=3,P1W=14,LW=b0,BW=BW)
            if not ok: continue
            # nodes
            pos=end; u=v=0; good=True
            for k in range(n5):
                vals=[]
                for prev in (u,v):
                    c=bits[pos:pos+4]
                    if c[:1]=='0': val=prev+int(bits[pos+1:pos+1+b1],2); pos+=1+b1
                    elif c[:2]=='10': val=prev-int(bits[pos+2:pos+2+b1],2); pos+=2+b1
                    elif c=='1100': val=int(bits[pos+4:pos+4+W],2); pos+=4+W
                    else: good=False; break
                    vals.append(val)
                if not good: break
                u,v=vals
                if not(-200<=u<=L+200 and -200<=v<=L+200): good=False; break
                pos+=LWk; F=bits[pos]; pos+=1
                if F=='1': pos+=11
            if good: return dict(BW=BW,start=st,linkend=end,nodeend=pos)
    return False
def us_bytes(p): return p[7]*2048
if __name__=='__main__':
    db=open(sys.argv[1],'rb').read()
    blocks=[b for b in carin.walk_blocks(db) if b[2]==0 and b[3]]
    random.seed(1); sample=random.sample(blocks,int(sys.argv[2]))
    okc=0; res=[]
    for s,ln,typ,cf,us in sample:
        r=try_parcel(db,s); res.append((s,r)); okc+= bool(r)
    print("decoded",okc,"of",len(sample))
    for s,r in res[:15]: print(hex(s),r)
