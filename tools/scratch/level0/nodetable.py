# decode node table of 0x1c8c2 (start 33328, u-base 66) -> list of (offset, u, v, link, flags)
import struct
def node_table(bits, start, n, W=14, D=7, LW=8, U0=0, V0=0, s5=0):
    u,v,pos=U0,V0,start; out=[]; fl=0
    for k in range(n):
        vals=[]
        for prev in (u,v):
            b=bits[pos:pos+4]
            if b[0]=='0': val=prev+int(bits[pos+1:pos+1+D],2); pos+=1+D
            elif b[:2]=='10': val=prev-int(bits[pos+2:pos+2+D],2); pos+=2+D
            elif b=='1100': val=int(bits[pos+4:pos+4+W],2); pos+=4+W
            else: raise ValueError((k,pos,b))
            vals.append(val)
        u,v=vals
        link=int(bits[pos:pos+LW],2); F=bits[pos+LW]; pos+=LW+1
        if F=='1': x=int(bits[pos:pos+11],2); fl=((x>>3)<<8)|(x&7); pos+=11
        out.append((s5+8*k,u,v,link,fl))
    return out,pos
