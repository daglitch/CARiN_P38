import sys, struct, json, collections
d=open(sys.argv[1],'rb').read(); blocks=json.load(open(sys.argv[2])); B=2048
fc=collections.Counter(); done=0
for s,ln,typ,cf,_ in blocks:
    if typ!=1 or cf!=0: continue
    p=d[s*B:(s+ln)*B]; pairs=struct.unpack('>30H',p[8:68])
    off,n=pairs[14],pairs[15]
    if done<1:
        print("parcel sector",hex(s),"pairs",[(hex(pairs[i]),pairs[i+1]) for i in range(0,30,2)])
        for k in range(min(n,40)):
            print(k,struct.unpack('>3H',p[off+6*k:off+6*k+6]))
        o4,n4=pairs[8],pairs[9]; o5=pairs[10]
        print("sec4 bytes/entry",(o5-o4)/n4); print(p[o4:o4+160].hex(' ',2))
        o5,n5,o6=pairs[10],pairs[11],pairs[12]; print("sec5 bytes/entry",(o6-o5)/n5); print(p[o5:o5+64].hex(' ',2))
    for k in range(n): fc[struct.unpack('>3H',p[off+6*k:off+6*k+6])[2]]+=1
    done+=1
print("flag values",fc.most_common(12))
