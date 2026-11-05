import sys, zlib, lzma, bz2
d=open(sys.argv[1],'rb').read()
base=int(sys.argv[2],16); nb=d[base+3]; p=d[base:base+nb*2048]
hits=0
for off in range(0x08,0x400):
    for wb in (-15,15,31):
        try:
            o=zlib.decompressobj(wb).decompress(p[off:])
            if len(o)>200: print("zlib",wb,hex(off),len(o)); hits+=1
        except Exception: pass
print("deflate hits",hits)
# byte-level stats of the dense region
import collections
seg=p[0x200:0xf00]; c=collections.Counter(seg)
print("top bytes in dense region",[(hex(k),v) for k,v in c.most_common(12)])
