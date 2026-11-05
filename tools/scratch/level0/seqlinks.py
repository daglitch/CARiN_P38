import sys, math, struct
sys.path.insert(0, r"C:/Users/duhgl/AppData/Local/Temp/claude/ee9401b46cd2e5dd/72d0633e-bb04-492d-a17a-be6c18a1fb97/scratchpad/l0")
from linkchain import load
from nodetable import node_table
db=open(sys.argv[1],'rb').read()
p,secs,bits=load(db,0x1c8c2)
nodes,_=node_table(bits,33328,164,U0=66,s5=secs[5][0])
s5,n5=secs[5]; s6,n6=secs[6]
nm={ (s5+8*k)>>1:k for k in range(n5)}; nm.update({(s6+16*k)>>1:1000+k for k in range(n6)})
def hb(dx,dy): return round(64-math.degrees(math.atan2(dy,dx))*256/360)%256
def ok_start(a):
    f=nm.get(int(bits[a:a+13],2)); t=nm.get(int(bits[a+13:a+26],2))
    return f is not None and t is not None
def rec(a, extra_rules):
    pos=a+26; X=bits[pos]; pos+=1
    pos+= 12 if X=='1' else 3
    pos+=40
    fa=bits[pos]; pos+=1
    if fa=='1': pos+=6
    fb=bits[pos]; pos+=1
    if fb=='1': pos+=16
    fc=bits[pos]; pos+=1
    if fc=='1': pos+=48
    return pos
a=int(sys.argv[2]); n=0
while n<int(sys.argv[3]):
    if not ok_start(a): print("BAD START at",a); break
    b=rec(a,None)
    f=nm[int(bits[a:a+13],2)]; t=nm[int(bits[a+13:a+26],2)]
    nxt_ok=ok_start(b)
    print(n,a,f"{f}->{t}","X",bits[a+26],"len",b-a,"next ok" if nxt_ok else "NEXT BAD")
    if not nxt_ok:
        print("  record bits:",bits[a:a+200]); break
    a=b; n+=1
