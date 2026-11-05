import sys, struct, json, collections
d=open(sys.argv[1],'rb').read(); blocks=json.load(open(sys.argv[2])); B=2048
for T in (0,1,2,3):
    us=[];vs=[];fl=collections.Counter(); sizes=collections.Counter(); n=0
    for s,ln,typ,cf,_ in blocks:
        if typ!=T or cf!=0: continue
        p=d[s*B:(s+ln)*B]; pairs=struct.unpack('>30H',p[8:68])
        x1,y1,x2,y2=struct.unpack('>4i',p[0x44:0x54]); sizes[x2-x1]+=1
        off,cnt=pairs[14],pairs[15]
        for k in range(cnt):
            u,v,f=struct.unpack('>3H',p[off+6*k:off+6*k+6]); us.append(u); vs.append(v); fl[f>>12]+=1
        n+=1
        if n>3000: break
    us.sort(); vs.sort()
    q=lambda a,f:a[int(f*(len(a)-1))]
    print("type",T,"parcels",n,"pts",len(us),"u q0,1,50,99,100:",q(us,0),q(us,.01),q(us,.5),q(us,.99),q(us,1),"v:",q(vs,0),q(vs,.01),q(vs,.5),q(vs,.99),q(vs,1))
    print("   tile sizes",sizes.most_common(3),"flag hi-nibble",fl.most_common(6))
