import sys, struct, collections, math
sys.path.insert(0, r"C:/Users/duhgl/OneDrive/Documents/P38_Nav/tools")
import carin
db=open(sys.argv[1],'rb').read(); B=2048
rows=[]; chk=collections.Counter()
for s,ln,typ,cf,us in carin.walk_blocks(db):
    if typ!=0 or not cf: continue
    p=db[s*B:(s+ln)*B]; secs=[struct.unpack_from('>2H',p,8+4*i) for i in range(15)]
    x1,y1,x2,y2=struct.unpack_from('>4i',p,0x44)
    b0,b1=p[0x74],p[0x75]
    # check stream after raw sections starts with 80000000
    q=0x96+8*secs[9][1]+8*secs[10][1]
    chk[p[q:q+4].hex()]+=1
    rows.append((b0,b1,(x2-x1)//64,secs[4][1],secs[5][1],secs[7][1],secs[4][0]+30*secs[4][1]))
print("marker after sec9+sec10:",chk.most_common(5))
# correlate b0 with log2 of max offset / count etc.
c=collections.Counter((r[0],r[1]) for r in rows); print(c.most_common(10))
for name,idx in (("tile",2),("links",3),("nodes",4),("shape",5),("linkend",6)):
    byb0=collections.defaultdict(list); byb1=collections.defaultdict(list)
    for r in rows: byb0[r[0]].append(r[idx]); byb1[r[1]].append(r[idx])
    print(name,"by b0:",{k:(min(v),max(v)) for k,v in sorted(byb0.items())})
    print(name,"by b1:",{k:(min(v),max(v)) for k,v in sorted(byb1.items())})
