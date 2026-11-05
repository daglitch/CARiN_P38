import sys, struct, collections
db=open(sys.argv[1],'rb').read(); B=2048
sec=int(sys.argv[2],16)
p=db[sec*B:sec*B+4096]; secs=[struct.unpack_from('>2H',p,8+4*i) for i in range(15)]
q=0x96+8*secs[9][1]+8*secs[10][1]
end=q+int(sys.argv[3])
bits=''.join(f'{b:08b}' for b in p[q:end])
s4,n4=secs[4]; s5,n5=secs[5]; s6,n6=secs[6]; s7,n7=secs[7]
print("secs",[(hex(o),c) for o,c in secs][:8],"bits",len(bits))
linkset={s4+30*k for k in range(n4+1)}
nodeset={s5+8*k for k in range(n5)}|{s6+16*k for k in range(n6)}
shapeset={s7+6*k for k in range(n7+1)}
for name,S_,w in (("link",linkset,13),("node",nodeset,13),("shape",shapeset,13),("linkW",{v//2 for v in linkset},12),("nodeW",{v//2 for v in nodeset},12)):
    hits=[i for i in range(len(bits)-w) if int(bits[i:i+w],2) in S_]
    exp=(len(bits)-w)*len(S_)/2**w
    # histogram of hits by 200-bit bucket
    h=collections.Counter(i//400 for i in hits)
    print(f"{name:6s} hits {len(hits)} expect {exp:.0f}  by-400:",[h.get(k,0) for k in range(len(bits)//400+1)])
