import sys, struct, json
from PIL import Image
d=open(sys.argv[1],'rb').read(); blocks=json.load(open(sys.argv[2])); B=2048
types=set(int(t) for t in sys.argv[4].split(','))
pts=[]
for s,ln,typ,cf,us in blocks:
    if typ not in types or cf!=0: continue
    p=d[s*B:(s+ln)*B]
    pairs=struct.unpack('>30H',p[8:68])
    x1,y1,x2,y2=struct.unpack('>4i',p[0x44:0x54])
    off,cnt=pairs[14],pairs[15]
    w=x2-x1; h=y2-y1
    for k in range(cnt):
        u,v,f=struct.unpack('>3H',p[off+6*k:off+6*k+6])
        D=20480 if typ==0 else 40960; pts.append((x1+u*w/D, y1+v*h/D))
print("points",len(pts))
xs=[a for a,b in pts]; ys=[b for a,b in pts]
# CA/NV window by default
X0,X1,Y0,Y1=[float(v) for v in sys.argv[5].split(',')] if len(sys.argv)>5 else (min(xs),max(xs),min(ys),max(ys))
W=1400; sc=W/(X1-X0); H=int((Y1-Y0)*sc)
im=Image.new('L',(W,H),255); px=im.load()
for x,y in pts:
    i=int((x-X0)*sc); j=int(H-1-(y-Y0)*sc)
    if 0<=i<W and 0<=j<H: px[i,j]=0
im.save(sys.argv[3]); print(W,H)
