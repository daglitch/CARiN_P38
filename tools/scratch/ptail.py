import sys,struct
d=open(sys.argv[1],'rb').read()
for base in [int(x,16) for x in sys.argv[2:]]:
    nb=d[base+3]; ln=nb*2048; p=d[base:base+ln]
    end=len(p.rstrip(b'\0'))
    offs=struct.unpack('>32H',p[8:8+64])
    print(hex(base),"blocks",nb,"data end",hex(end),"type",p[6:8].hex(),"hdr pairs",[hex(o) for o in offs[0::2][:16]])
    print("   counts",offs[1::2][:16], "last-nonzero bytes", p[end-16:end].hex())
