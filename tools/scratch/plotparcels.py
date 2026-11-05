import numpy as np, sys
from PIL import Image, ImageDraw
f=np.load(sys.argv[1]); a=f[:,2:]
sz=a[:,2]-a[:,0]
for lvl in sorted(set(sz.tolist())):
    s=a[sz==lvl]; print(lvl,len(s),"x",s[:,0].min(),s[:,2].max(),"y",s[:,1].min(),s[:,3].max())
sel=a[sz<=327680]
x0,x1=sel[:,0].min(),sel[:,2].max(); y0,y1=sel[:,1].min(),sel[:,3].max()
W=900; sc=W/(x1-x0); H=int((y1-y0)*sc)+1
im=Image.new('RGB',(W,H),'white'); dr=ImageDraw.Draw(im)
for b in sel:
    dr.rectangle([ (b[0]-x0)*sc, H-(b[3]-y0)*sc, (b[2]-x0)*sc, H-(b[1]-y0)*sc ], outline=(0,0,180))
im.save(sys.argv[2]); print("img",W,H,"extent",x0,x1,y0,y1)
