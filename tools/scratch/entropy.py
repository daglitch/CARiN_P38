import sys, math, collections
p=sys.argv[1]; blk=int(sys.argv[2])
data=open(p,'rb').read()
rows=[]
for off in range(0,len(data),blk):
    b=data[off:off+blk]; c=collections.Counter(b); n=len(b)
    e=-sum(v/n*math.log2(v/n) for v in c.values())
    z=c[0]/n
    rows.append((off,e,z))
hist=collections.Counter(round(e) for _,e,_ in rows)
print("entropy histogram (bits/byte):",sorted(hist.items()))
# print segment transitions
prev=None
for off,e,z in rows:
    k=round(e)
    if k!=prev: print(f"{off:#010x} ent={e:.2f} zero={z:.2f}"); prev=k
