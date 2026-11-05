import sys, struct, math
sys.path.insert(0, r"C:/Users/duhgl/OneDrive/Documents/P38_Nav/tools")
import carin
db=open(sys.argv[1],'rb').read(); B=2048
for sec in (0x16656,0x1a2a2):
    p=db[sec*B:sec*B+4096]; secs=[struct.unpack_from('>2H',p,8+4*i) for i in range(15)]
    us=p[7]; W=math.ceil(math.log2(us*2048))
    q=0x96+8*secs[9][1]+8*secs[10][1]; end=len(p.rstrip(b'\0'))
    bits=''.join(f'{b:08b}' for b in p[q:end])
    vals={'sec4':secs[4][0],'sec5':secs[5][0],'sec6':secs[6][0],'sec7':secs[7][0],'sec14':secs[14][0],
          'strbase':secs[14][0]+4*secs[14][1],'0120':0x120,'00e0':0xe0,'0074':0x74,'007e':0x7e,'0092':0x92,'009c':0x9c,'1f20':0x1f20,'1f10':0x1f10,
          'sec4+30':secs[4][0]+30,'sec4+60':secs[4][0]+60,'sec5+8':secs[5][0]+8}
    print(f"\nsector {sec:#x} W={W} bits={len(bits)}")
    for k,v in vals.items():
        for w in (16,W,W+1):
            pat=f'{v:0{w}b}'
            if len(pat)>w: continue
            pos=[i for i in range(len(bits)-w) if bits.startswith(pat,i)]
            exp=len(bits)/2**w
            if len(pos)>max(2,3*exp): print(f"  {k:8s}={v:#06x} as {w:2d}-bit: {len(pos)} hits (expect {exp:.1f}) first {pos[:12]}")
