import numpy as np, sys
d=np.fromfile(sys.argv[1],dtype=np.uint8)
res={}
for align in (0,2):
    n=(len(d)-align)//4
    v=d[align:align+n*4].view('>i4').astype(np.int64)
    for name,scale in (("1e-7deg",1e7),("1e-6deg",1e6),("2^32/360",2**32/360),("2^31/180",2**31/180), ("1e-5deg",1e5)):
        lat=((v>=32.5*scale)&(v<=42*scale)).sum()
        lonn=((v>=-124.5*scale)&(v<=-114*scale)).sum()
        lonp=((v<=124.5*scale)&(v>=114*scale)).sum()
        print(align,name,"lat",lat,"lon-",lonn,"lon+",lonp)
