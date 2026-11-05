import sys
d=open(sys.argv[1],'rb').read()
start=int(sys.argv[2],16); n=int(sys.argv[3])
bits=''.join(f'{b:08b}' for b in d[start:start+n])
for w in range(5,17):
    vals=[int(bits[i:i+w],2) for i in range(0,len(bits)-w,w)][:40]
    print(w, vals)
