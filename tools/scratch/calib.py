import sys, struct, json
d=open(sys.argv[1],'rb').read(); blocks=json.load(open(sys.argv[2])); B=2048
K=1.8e-7
pts=[]
for s,ln,typ,cf,_ in blocks:
    if typ>3 or cf!=0: continue
    p=d[s*B:(s+ln)*B]; pairs=struct.unpack('>30H',p[8:68])
    x1,y1=struct.unpack('>2i',p[0x44:0x4c])
    for sec in (7,):
        off,n=pairs[2*sec],pairs[2*sec+1]
        for k in range(n):
            u,v,f=struct.unpack('>3H',p[off+6*k:off+6*k+6]); pts.append((x1+u*64,y1+v*64))
    off,n=pairs[10],pairs[11]
    for k in range(n):
        u,v=struct.unpack('>2H',p[off+8*k:off+8*k+4]); pts.append((x1+u*64,y1+v*64))
# Florida keys: lat < 25.3
fk=[(x,y) for x,y in pts if y*K<25.3 and y*K>24.3]
fk.sort()
print("n",len(fk))
xs=sorted(set(x for x,y in fk))
# cluster by x: east-most group (Florida) - print extremes of groups separated by large gaps
groups=[[xs[0]]]
for x in xs[1:]:
    if x-groups[-1][-1]>20_000_000: groups.append([x])
    else: groups[-1].append(x)
for g in groups:
    sel=[(x,y) for x,y in fk if g[0]<=x<=g[-1]]
    ymin=min(y for x,y in sel)
    print("group x",g[0],g[-1],"n",len(sel),"min lat",ymin*K, "min x pt", min(sel))
