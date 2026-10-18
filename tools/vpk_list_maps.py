import struct,sys
def cstr(b,i):
    j=b.index(b'\0',i); return b[i:j].decode('latin1'),j+1
for p in sys.argv[1:]:
    b=open(p,'rb').read()
    sig,ver=struct.unpack_from('<II',b,0)
    i=12 if ver==1 else 28
    if sig!=0x55aa1234: i=0; print('nosig',p)
    while True:
        ext,i=cstr(b,i)
        if not ext: break
        while True:
            d,i=cstr(b,i)
            if not d: break
            while True:
                n,i=cstr(b,i)
                if not n: break
                crc,pre,arc,off,ln,term=struct.unpack_from('<IHHIIH',b,i); i+=18+pre
                if ext=='bsp': print(f'{d}/{n}.{ext}')
