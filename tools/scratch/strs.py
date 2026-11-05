import sys,re
d=open(sys.argv[1],'rb').read(); n=int(sys.argv[2])
with open(sys.argv[3],'w') as o:
    for m in re.finditer(rb'[\x20-\x7e]{%d,}'%n,d):
        o.write(f"{m.start():x} {m.group().decode()}\n")
