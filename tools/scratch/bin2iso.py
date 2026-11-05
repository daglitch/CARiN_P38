import sys
src, dst, nsec = sys.argv[1], sys.argv[2], int(sys.argv[3])
forms = {}
with open(src,'rb') as f, open(dst,'wb') as o:
    for i in range(nsec):
        s = f.read(2352)
        sub = s[16:24]
        form2 = bool(sub[2] & 0x20)
        forms[form2] = forms.get(form2,0)+1
        o.write(s[24:24+2048])
print(forms)
