def parse_links(bits, start, n, valid_ptr, PW=13, CW=12, DW=3, P1W=14, LW=10, BW=16):
    pos=start; out=[]
    for k in range(n):
        a=pos
        f=valid_ptr.get(int(bits[pos:pos+PW],2)); t=valid_ptr.get(int(bits[pos+PW:pos+2*PW],2))
        if f is None or t is None: return out, pos, False
        pos+=2*PW; X=bits[pos]; pos+=1
        w=CW if X=='1' else DW
        cur=int(bits[pos:pos+w],2) if w else 0; pos+=w
        p1=int(bits[pos:pos+P1W],2); pos+=P1W
        ln=int(bits[pos:pos+LW],2); pos+=LW
        h0=int(bits[pos:pos+8],2); h1=int(bits[pos+8:pos+16],2); pos+=16
        fa=bits[pos]; pos+=1; A=None
        if fa=='1': A=int(bits[pos:pos+6],2); pos+=6
        fb=bits[pos]; pos+=1; Bv=None
        if fb=="1": Bv=int(bits[pos:pos+BW],2); pos+=BW
        fc=bits[pos]; pos+=1; C=None
        if fc=='1': C=(int(bits[pos:pos+16],2),int(bits[pos+16:pos+32],2),int(bits[pos+32:pos+48],2)); pos+=48
        out.append(dict(at=a,frm=f,to=t,X=X,cur=cur,p1=p1,len=ln,h0=h0,h1=h1,A=A,B=Bv,C=C))
    return out,pos,True
