import sys, struct, json, collections
d=open(sys.argv[1],'rb').read(); blocks=json.load(open(sys.argv[2])); B=2048
mx=collections.defaultdict(int); cnt=collections.Counter(); hdr=collections.defaultdict(collections.Counter)
for s,ln,typ,cf,_ in blocks:
    if typ>3 or cf!=0: continue
    p=d[s*B:(s+ln)*B]; pairs=struct.unpack('>30H',p[8:68])
    x1,y1,x2,y2=struct.unpack('>4i',p[0x44:0x54]); w=x2-x1
    off,n=pairs[14],pairs[15]
    m=0
    for k in range(n):
        u,v,f=struct.unpack('>3H',p[off+6*k:off+6*k+6]); m=max(m,u,v)
    key=(typ,w); mx[key]=max(mx[key],m); cnt[key]+=1
    hdr[key][p[0x54:0x74].hex()[:40]]+=1
for k in sorted(mx): print(k,"parcels",cnt[k],"max local",mx[k],"unit",k[1]/max(mx[k],1), "hdr54", hdr[k].most_common(1))
