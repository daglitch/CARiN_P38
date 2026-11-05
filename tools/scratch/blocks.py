import sys, collections, struct
d=open(sys.argv[1],'rb').read(); B=2048; n=len(d)//B
c=collections.Counter(); first={}
for i in range(n):
    h=d[i*B:i*B+4]; k=h.hex()
    c[k[:4]+'....' if False else h[2:4].hex()]+=1
    first.setdefault(h[2:4].hex(),i)
print("blocks",n,"len%B",len(d)%B)
print("byte2-3 of block header, top 25:")
for k,v in c.most_common(25): print(" ",k,v,"first block",first[k],hex(first[k]*B))
# runs of type across file
runs=[];prev=None
for i in range(n):
    t=d[i*B+2:i*B+4]
    if t!=prev: runs.append((i,t.hex())); prev=t
print("runs:",len(runs)); print(runs[:40])
