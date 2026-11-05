import sys
d=open(sys.argv[1],'rb').read()
base=int(sys.argv[2],16); start=int(sys.argv[3],16)
bits=''.join(f'{b:08b}' for b in d[base+start:base+start+1200])
for shift in range(0,15):
    vals=[int(bits[i:i+15],2) for i in range(shift,len(bits)-15,15)]
    run=1
    while run<len(vals) and vals[run]>=vals[run-1]: run+=1
    if run>8: print("shift",shift,"run",run,[v for v in vals[:run]][:80]); print("  after:",vals[run:run+12])
