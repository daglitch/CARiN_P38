import sys, numpy as np
d=open(sys.argv[1],'rb').read(); base=int(sys.argv[2],16)
S0=base+int(sys.argv[3],16); E=base+int(sys.argv[4],16)
N=int(sys.argv[5]); LIM=int(sys.argv[6]); W=int(sys.argv[7])
bits=np.unpackbits(np.frombuffer(d[S0:E],dtype=np.uint8)).astype(np.int64)
nb=len(bits)
# value of W-bit field at every bit position
val=np.zeros(nb-W,dtype=np.int64)
for k in range(W): val=val*2+bits[k:nb-W+k]
ok=val<=LIM
best=[]
for stride in range(2*W, 80):
    for off in range(0, nb-stride*N-2*W):
        idx=off+stride*np.arange(N)
        # u at idx, v at idx+W (adjacent) or anywhere: test adjacent first
        if ok[idx].all() and ok[idx+W].all():
            u=val[idx]; best.append((stride,off,int(u.min()),int(u.max()),int(val[idx+W].min()),int(val[idx+W].max())))
print(len(best)); print(best[:30])
