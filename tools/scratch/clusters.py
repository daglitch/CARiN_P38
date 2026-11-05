import numpy as np, sys, collections
f=np.load(sys.argv[1]); a=f[:,2:]; sz=a[:,2]-a[:,0]
s=a[sz==81920]; cx=(s[:,0]+s[:,2])//2; cy=(s[:,1]+s[:,3])//2
g=5_000_000
c=collections.Counter(zip(cx//g,cy//g))
for (gx,gy),n in c.most_common(15):
    m=(cx//g==gx)&(cy//g==gy)
    print(n, int(cx[m].mean()), int(cy[m].mean()), "xmin",cx[m].min(),"xmax",cx[m].max())
print("overall small-tile x",cx.min(),cx.max(),"y",cy.min(),cy.max())
