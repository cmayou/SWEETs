"""R2004-family (AC1018/AC1024/AC1027/AC1032) DWG container reader/patcher - prototype."""
import struct

def decrypt_header(b):
    out=bytearray(len(b)); r=1
    for i in range(len(b)):
        r=(r*0x343fd+0x269ec3)&0xffffffff
        out[i]=b[i]^((r>>16)&0xff)
    return bytes(out)

def checksum(seed, data):
    s1=seed&0xffff; s2=(seed>>16)&0xffff; i=0; n=len(data)
    while i<n:
        chunk=min(0x15b0,n-i)
        for b in data[i:i+chunk]:
            s1+=b; s2+=s1
        s1%=0xfff1; s2%=0xfff1; i+=chunk
    return ((s2<<16)|(s1&0xffff))&0xffffffff

def decompress(src, pos, end, outsize):
    dst=bytearray(outsize); dp=0
    def rb():
        nonlocal pos
        b=src[pos]; pos+=1; return b
    def litlen(op):
        n=op&0xf
        if n==0:
            while True:
                b=rb()
                if b==0: n+=0xff
                else: break
            n+=0xf+b
        return n+3
    def cbytes(op,bits):
        n=op&bits
        if n==0:
            while True:
                b=rb()
                if b==0: n+=0xff
                else: break
            n+=b+bits
        return n+2
    ops=[]  # (kind, dst_start, length, src_start_or_srcpos)
    op1=rb()
    if op1&0xf0==0:
        L=litlen(op1); ops.append(('L',dp,L,pos)); dst[dp:dp+L]=src[pos:pos+L]; pos+=L; dp+=L; op1=rb()
    while pos<end and dp<outsize and op1!=0x11:
        if op1<0x10 or op1>=0x40:
            cb=(op1>>4)-1; op2=rb(); off=(((op1>>2)&3)|(op2<<2))+1
        elif op1<0x20:
            cb=cbytes(op1,7); off=(op1&8)<<11
            b1=rb(); b2=rb(); off|=(b1>>2); off|=b2<<6; off+=0x4000; op1=b1
        else:
            cb=cbytes(op1,0x1f); off=0
            b1=rb(); b2=rb(); off|=(b1>>2); off|=b2<<6; off+=1; op1=b1
        ops.append(('C',dp,cb,dp-off))
        for k in range(cb):
            dst[dp+k]=dst[dp+k-off]
        dp+=cb
        L=op1&3
        if L==0:
            op1=rb()
            if op1&0xf0==0: L=litlen(op1)
        if L:
            ops.append(('L',dp,L,pos)); dst[dp:dp+L]=src[pos:pos+L]; pos+=L; dp+=L; op1=rb()
    return bytes(dst), ops, pos

class Dwg:
    def __init__(self, path):
        self.path=path; self.data=bytearray(open(path,'rb').read())
        d=self.data
        self.version=d[0:6].decode()
        h=decrypt_header(d[0x80:0x80+0x6c])
        self.fh=h
        f=lambda fmt,o: struct.unpack_from(fmt,h,o)[0]
        self.hdr=dict(id=h[0:12], header_address=f('<I',0x0c), last_section_id=f('<I',0x28),
            last_section_address=f('<Q',0x2c), secondheader_address=f('<Q',0x34), numgaps=f('<I',0x3c),
            numsections=f('<I',0x40), section_map_id=f('<I',0x50), section_map_address=f('<Q',0x54),
            section_info_id=f('<i',0x5c), section_array_size=f('<I',0x60), gap_array_size=f('<I',0x64), crc32=f('<I',0x68))
        # page map
        a=self.hdr['section_map_address']+0x100
        st,dsz,csz,ctype,cks=struct.unpack_from('<5I',d,a)
        assert st==0x41630e3b, hex(st)
        pm,_,_=decompress(d,a+20,a+20+csz,dsz)
        self.pages={}; self.pagelist=[]; addr=0x100; i=0
        while i+8<=len(pm):
            num,size=struct.unpack_from('<iI',pm,i); i+=8
            entry=dict(number=num,size=size,address=addr)
            if num<0:
                entry['gap']=struct.unpack_from('<4I',pm,i); i+=16
            self.pagelist.append(entry)
            if num>=0: self.pages[num]=entry
            addr+=size
        # section info
        si=self.pages[self.hdr['section_info_id']]
        a=si['address']; st,dsz,csz,ctype,cks=struct.unpack_from('<5I',d,a)
        assert st==0x4163003b, hex(st)
        info,_,_=decompress(d,a+20,a+20+csz,dsz)
        num_desc,comp,maxsz,enc,nd2=struct.unpack_from('<5I',info,0); p=20
        self.sections={}
        for _ in range(num_desc):
            size,nsec,maxd,unk,compressed,typ,encr=struct.unpack_from('<QIIIIII',info,p); p+=32
            name=info[p:p+64].split(b'\0')[0].decode(); p+=64
            plist=[]
            for _ in range(nsec):
                pn,psz,paddr=struct.unpack_from('<iIQ',info,p); p+=16
                plist.append(dict(number=pn,size=psz,offset=paddr))
            self.sections[name]=dict(size=size,max_decomp=maxd,compressed=compressed,type=typ,encrypted=encr,pages=plist)

    def read_page(self, number):
        """returns (header dict, decompressed bytes, ops, comp_start, comp_end)"""
        pg=self.pages[number]; a=pg['address']; d=self.data
        mask=0x4164536b ^ (a & 0xffffffff)
        hv=[x^mask for x in struct.unpack_from('<8I',d,a)]
        hd=dict(page_type=hv[0],section_type=hv[1],data_size=hv[2],page_size=hv[3],offset=hv[4],unknown=hv[5],hdr_crc=hv[6],data_crc=hv[7])
        return hd, a

    def section_bytes(self, name):
        s=self.sections[name]; out=bytearray(s['max_decomp']*len(s['pages'])); info=[]
        for pg in s['pages']:
            hd,a=self.read_page(pg['number'])
            if s['compressed']==2:
                dec,ops,end=decompress(self.data,a+32,a+32+hd['data_size'],s['max_decomp'])
            else:
                dec=bytes(self.data[a+32:a+32+hd['page_size']]); ops=None
            out[hd['offset']:hd['offset']+len(dec)]=dec
            info.append(dict(page=pg['number'],hdr=hd,address=a,ops=ops,offset=hd['offset']))
        return bytes(out[:s['size']]), info

# ---------------- compression (port of LibreDWG/ACadSharp ODA LZ77 AC18 compressor)
def _write_long_off(out, off):
    while off>0xff:
        out.append(0); off-=0xff
    out.append(off&0xff)

def _write_length(out, u1, match, u2):
    if u2<match:
        out.append(u1&0xff); _write_long_off(out, match-u2)
    else:
        out.append((u1|(match-2))&0xff)

def _write_literals(out, buf, start, n):
    if n:
        if n>3: _write_length(out,0,n-1,0x11)
        out+=buf[start:start+n]

def _find_match(src, n, pos, ht):
    if pos+3>=n: return 0,0
    v1=src[pos+3]<<6; v2=v1^src[pos+2]; v3=(v2<<5)^src[pos+1]; v4=(v3<<5)^src[pos]
    idx=(v4+(v4>>5))&0x7fff
    prev=ht[idx]; dist=pos-prev if prev>=0 else 0
    ml=0
    if prev>=0 and dist<=0xbfff:
        if dist>0x400 and src[pos+3]!=src[prev+3]:
            idx=(idx&0x7ff)^0x401f
            prev=ht[idx]
            if prev>=0: dist=pos-prev
            if prev<0 or dist>0xbfff or (dist>0x400 and src[pos+3]!=src[prev+3]):
                ht[idx]=pos; return 0,0
        if src[pos]==src[prev] and src[pos+1]==src[prev+1] and src[pos+2]==src[prev+2]:
            p=prev+3; c=pos+3; ml=3
            while c<n and src[p]==src[c]:
                p+=1; c+=1; ml+=1
    ht[idx]=pos
    return (ml,dist) if ml>=3 else (0,0)

def _write_match(out, dist, ml, lits):
    if ml<0x0f and dist<=0x400:
        d=dist-1; b1=((ml+1)<<4)|((d&3)<<2); b2=d>>2
    elif dist<=0x4000:
        d=dist-1; _write_length(out,0x20,ml,0x21); b1=(d&0xff)<<2; b2=d>>6
    else:
        d=dist-0x4000; _write_length(out,((d>>11)&8)|0x10,ml,9); b1=(d&0xff)<<2; b2=d>>6
    if lits<4: b1|=lits
    out.append(b1&0xff); out.append(b2&0xff)

def compress(src):
    src=bytes(src); n=len(src); out=bytearray()
    if n<=0x18:
        _write_literals(out,src,0,n); out.append(0x11); return bytes(out)
    ht=[-1]*0x8000; cur=0; pos=4; pml=0; pmd=0
    while pos<n-0x13:
        ml,md=_find_match(src,n,pos,ht)
        if ml<3:
            pos+=1; continue
        lits=pos-cur
        if pml: _write_match(out,pmd,pml,lits)
        _write_literals(out,src,cur,lits)
        pos+=ml; cur=pos; pml=ml; pmd=md
    lits=n-cur
    if pml: _write_match(out,pmd,pml,lits)
    _write_literals(out,src,cur,lits)
    out.append(0x11)
    return bytes(out)

def crc16(data, seed=0xC0C1):
    crc=seed
    for b in data:
        crc^=b
        for _ in range(8):
            crc=(crc>>1)^0xA001 if crc&1 else crc>>1
    return crc&0xffff

# ---------------- bit-level reading
class Bits:
    def __init__(self, buf, bitpos=0, endbit=None):
        self.b=buf; self.p=bitpos; self.end=endbit if endbit is not None else len(buf)*8
    def bit(self):
        v=(self.b[self.p>>3]>>(7-(self.p&7)))&1; self.p+=1; return v
    def bits(self,n):
        v=0
        for _ in range(n): v=(v<<1)|self.bit()
        return v
    def RC(self): return self.bits(8)
    def RS(self): a=self.RC(); b=self.RC(); return a|(b<<8)
    def RL(self): a=self.RS(); b=self.RS(); return a|(b<<16)
    def RD(self):
        return struct.unpack('<d',bytes(self.RC() for _ in range(8)))[0]
    def B(self): return self.bit()
    def BB(self): return self.bits(2)
    def BS(self):
        c=self.BB()
        return [lambda:self.RS(),lambda:self.RC(),lambda:0,lambda:256][c]()
    def BL(self):
        c=self.BB()
        if c==0: return self.RL()
        if c==1: return self.RC()
        if c==2: return 0
        raise ValueError('BL code 3 @%d'%self.p)
    def BLL(self):
        n=self.bits(3); v=0
        for i in range(n): v|=self.RC()<<(8*i)
        return v
    def BD(self):
        c=self.BB()
        if c==0: return self.RD()
        if c==1: return 1.0
        if c==2: return 0.0
        raise ValueError('BD code 3 @%d'%self.p)
    def H(self):
        x=self.RC(); code=x>>4; n=x&15; v=0
        for _ in range(n): v=(v<<8)|self.RC()
        return code,v
    def CMC(self):  # R2004+ (strings are in the string stream)
        idx=self.BS(); rgb=self.BL(); flag=self.RC(); return idx,rgb,flag
    def OT(self):  # R2010+
        c=self.BB()
        if c==0: return self.RC()
        if c==1: return self.RC()+0x1f0
        return self.RS()
    def TU(self):  # R2007+ string stream text
        n=self.BS(); return bytes(self.RC() for _ in range(2*n)).decode('utf-16-le')

def read_MS(buf, pos):
    v=0; sh=0
    while True:
        w=buf[pos]|(buf[pos+1]<<8); pos+=2
        v|=(w&0x7fff)<<sh; sh+=15
        if not (w&0x8000): return v,pos

def read_UMC(buf,pos):
    v=0; sh=0
    while True:
        b=buf[pos]; pos+=1
        v|=(b&0x7f)<<sh; sh+=7
        if not (b&0x80): return v,pos

def read_MC(buf,pos):
    v=0; sh=0
    while True:
        b=buf[pos]; pos+=1
        if b&0x80:
            v|=(b&0x7f)<<sh; sh+=7
        else:
            neg=b&0x40; v|=(b&0x3f)<<sh
            return (-v if neg else v),pos

def object_map(handles):
    """AcDb:Handles -> list of (handle, offset)"""
    res=[]; pos=0
    while pos+2<=len(handles):
        size=(handles[pos]<<8)|handles[pos+1]
        if size<=2: break
        start=pos; pos+=2; h=0; off=0
        while pos-start<size:
            dh,pos=read_UMC(handles,pos); do,pos=read_MC(handles,pos)
            h+=dh; off+=do; res.append((h,off))
        pos+=2  # crc
    return res

def object_strings_start(objbuf, data_start_bit, bitsize):
    """R2007+: returns bit position of the string stream or None (has_strings bit at bitsize-1)."""
    r=Bits(objbuf); end=data_start_bit+bitsize
    r.p=end-1
    if not r.bit(): return None
    r.p=end-1-16; size=r.RS()
    if size&0x8000:
        r.p=end-1-32; hi=r.RS(); size=(size&0x7fff)|(hi<<15)
        return end-1-32-size
    return end-1-16-size

HEADER_SENTINEL=bytes.fromhex('CF7B1F23FDDE38A95F7C68B84E6D33 5F'.replace(' ',''))

def header_layout(hb, version):
    """Locate ORTHOMODE / LIMMIN / LIMMAX in an R2010+ AcDb:Header section (decompressed).
    Returns dict with bit positions, values, crc range info."""
    size=struct.unpack_from('<I',hb,16)[0]
    # decide whether bitsize_hi is present by checking the CRC
    layouts=[]
    for hi in (True, False):
        data_off=28 if hi else 24
        crc_end=(24 if hi else 20)+size
        if crc_end+2>len(hb): continue
        if crc16(hb[16:crc_end])==struct.unpack_from('<H',hb,crc_end)[0]:
            layouts.append((hi,data_off,crc_end))
    if len(layouts)!=1: raise ValueError('header CRC layout not recognised')
    hi,data_off,crc_end=layouts[0]
    r=Bits(hb,data_off*8)
    if version>='AC1027': r.BLL()
    for _ in range(4): r.BD()
    r.BL(); r.BL()
    r.B(); r.B(); r.B()
    p_ortho=r.p; ortho=r.B()
    for _ in range(5): r.B()           # REGENMODE FILLMODE QTEXTMODE PSLTSCALE LIMCHECK
    r.B()                              # unknown_11 (R2004+)
    for _ in range(11): r.B()          # USRTIMER .. PELLIPSE
    for _ in range(8): r.BS()          # PROXYGRAPHICS TREEDEPTH LUNITS LUPREC AUNITS AUPREC ATTMODE PDMODE
    for _ in range(3): r.BL()
    for _ in range(19): r.BS()         # USERI1-5, SPLINESEGS..TEXTQLTY
    for _ in range(21): r.BD()         # LTSCALE..CELTSCALE
    for _ in range(2): r.BL(); r.BL()  # TDUCREATE TDUUPDATE
    for _ in range(3): r.BL()
    for _ in range(2): r.BL(); r.BL()  # TDINDWG TDUSRTIMER
    r.CMC()                            # CECOLOR
    r.H()                              # HANDSEED (data stream)
    r.BD()                             # PSVPSCALE
    for _ in range(3*3): r.BD()        # PINSBASE PEXTMIN PEXTMAX
    for _ in range(4): r.RD()          # PLIMMIN PLIMMAX
    r.BD()                             # PELEVATION
    for _ in range(9): r.BD()          # PUCSORG PUCSXDIR PUCSYDIR
    r.BS()                             # PUCSORTHOVIEW
    for _ in range(18): r.BD()         # PUCSORGTOP..BACK
    for _ in range(9): r.BD()          # INSBASE EXTMIN EXTMAX
    p_limmin=r.p; limmin=(r.RD(),r.RD())
    p_limmax=r.p; limmax=(r.RD(),r.RD())
    return dict(bitsize_hi=hi, crc_end=crc_end, p_ortho=p_ortho, ortho=ortho, p_limmin=p_limmin, limmin=limmin, p_limmax=p_limmax, limmax=limmax)

# ---------------- patching
def lcg_bytes(n):
    out=bytearray(n); r=1
    for i in range(n):
        r=(r*0x343fd+0x269ec3)&0xffffffff; out[i]=(r>>16)&0xff
    return bytes(out)

def set_bits(buf, bitpos, value, nbits):
    for i in range(nbits):
        b=(value>>(nbits-1-i))&1; p=bitpos+i
        if b: buf[p>>3]|=(0x80>>(p&7))
        else: buf[p>>3]&=~(0x80>>(p&7))&0xff

def write_RD(buf, bitpos, v):
    for i,byte in enumerate(struct.pack('<d',v)): set_bits(buf,bitpos+8*i,byte,8)

class PatchError(Exception): pass

def find_active_vport(objs, om, version='AC1027'):
    found=[]
    for h,off in om:
        size,p=read_MS(objs,off); hsize,p=read_UMC(objs,p)
        r=Bits(objs,p*8)
        if r.OT()!=65: continue
        data_start=p*8; bitsize=size*8-hsize
        r.H()
        while True:
            n=r.BS()
            if n==0: break
            r.H(); r.p+=8*n
        r.BL(); r.B()
        if version>='AC1027': r.B()   # has_ds_data (R2013+)
        sp=object_strings_start(objs,data_start,bitsize)
        name=Bits(objs,sp).TU() if sp is not None else None
        r.BS()
        for _ in range(2): r.BD()
        r.RD(); r.RD()
        for _ in range(6+4): r.BD()
        r.bits(4); r.RC(); r.B(); r.RC(); r.BD(); r.BD(); r.CMC()
        for _ in range(4): r.RD()
        r.B(); r.BS(); r.B(); r.BB()
        pos=dict(gridmode=r.p); r.B(); pos['gridunit']=r.p; r.RD(); r.RD()
        pos['snapmode']=r.p; r.B(); r.B(); r.BS(); r.BD(); r.RD(); r.RD()
        pos['snapunit']=r.p
        found.append(dict(handle=h, off=off, crc_pos=p+size, name=name, pos=pos))
    act=[v for v in found if (v['name'] or '').upper()=='*ACTIVE']
    if len(act)!=1: raise PatchError('expected one *Active VPORT, found %d (of %d VPORTs)'%(len(act),len(found)))
    return act[0]

def patch_file(src, dst, snap_on=None, snap=None, grid_on=None, grid=None, limmin=None, limmax=None, ortho=None):
    g=Dwg(src)
    if g.version not in ('AC1024','AC1027','AC1032'): raise PatchError('unsupported version '+g.version)
    d=bytearray(g.data)
    changed_sections={}
    # --- objects
    if any(v is not None for v in (snap_on,snap,grid_on,grid)):
        objs,pinfo=g.section_bytes('AcDb:AcDbObjects'); objs=bytearray(objs)
        hnd,_=g.section_bytes('AcDb:Handles')
        vp=find_active_vport(objs, object_map(hnd), g.version)
        P=vp['pos']
        if grid_on is not None: set_bits(objs,P['gridmode'],1 if grid_on else 0,1)
        if grid is not None: write_RD(objs,P['gridunit'],grid[0]); write_RD(objs,P['gridunit']+64,grid[1])
        if snap_on is not None: set_bits(objs,P['snapmode'],1 if snap_on else 0,1)
        if snap is not None: write_RD(objs,P['snapunit'],snap[0]); write_RD(objs,P['snapunit']+64,snap[1])
        struct.pack_into('<H',objs,vp['crc_pos'],crc16(objs[vp['off']:vp['crc_pos']]))
        changed_sections['AcDb:AcDbObjects']=(objs,pinfo)
    # --- header
    if any(v is not None for v in (limmin,limmax,ortho)):
        hb,pinfo=g.section_bytes('AcDb:Header'); hb=bytearray(hb)
        L=header_layout(hb,g.version)
        if ortho is not None: set_bits(hb,L['p_ortho'],1 if ortho else 0,1)
        if limmin is not None: write_RD(hb,L['p_limmin'],limmin[0]); write_RD(hb,L['p_limmin']+64,limmin[1])
        if limmax is not None: write_RD(hb,L['p_limmax'],limmax[0]); write_RD(hb,L['p_limmax']+64,limmax[1])
        struct.pack_into('<H',hb,L['crc_end'],crc16(hb[16:L['crc_end']]))
        changed_sections['AcDb:Header']=(hb,pinfo)
    # --- rewrite changed pages
    new_sizes={}  # page number -> new data size
    for name,(buf,pinfo) in changed_sections.items():
        sec=g.sections[name]; orig,_=g.section_bytes(name)
        for pi in pinfo:
            start=pi['offset']; end=min(start+sec['max_decomp'], sec['size'])
            if bytes(buf[start:end])==bytes(orig[start:end]): continue
            plain=bytes(buf[start:end])
            # the page decompresses to max_decomp bytes; keep any tail beyond section size as in the original page
            hd,a=g.read_page(pi['page'])
            full,_,_=decompress(g.data,a+32,a+32+hd['data_size'],sec['max_decomp'])
            full=bytearray(full); full[0:end-start]=plain
            # length of decompressed content actually encoded in the original page
            olddec=page_decoded_length(g.data,a+32,a+32+hd['data_size'],sec['max_decomp'])
            comp=compress(bytes(full[:olddec]))+b'\0\0'
            alloc=g.pages[pi['page']]['size']
            if 32+len(comp)>alloc: raise PatchError('page %d: %d bytes do not fit in %d'%(pi['page'],32+len(comp),alloc))
            # verify round trip before writing
            chk,_,_=decompress(comp,0,len(comp),olddec)
            if chk!=bytes(full[:olddec]): raise PatchError('compression round-trip failed')
            dc=checksum(0,comp)
            hv=[hd['page_type'],hd['section_type'],len(comp),hd['page_size'],hd['offset'],hd['unknown'],0,dc]
            hv[6]=checksum(dc,struct.pack('<8I',*hv))
            mask=0x4164536b ^ (a&0xffffffff)
            struct.pack_into('<8I',d,a,*[x^mask for x in hv])
            d[a+32:a+32+len(comp)]=comp
            pad=alloc-32-len(comp); d[a+32+len(comp):a+alloc]=lcg_bytes(pad)
            new_sizes[pi['page']]=len(comp)
    # --- section info: per-page compressed sizes
    if new_sizes:
        si=g.pages[g.hdr['section_info_id']]; a=si['address']
        st,dsz,csz,ctype,cks=struct.unpack_from('<5I',g.data,a)
        info,_,_=decompress(g.data,a+20,a+20+csz,dsz); info=bytearray(info)
        num_desc=struct.unpack_from('<I',info,0)[0]; p=20; touched=0
        for _ in range(num_desc):
            nsec=struct.unpack_from('<I',info,p+8)[0]; p+=32+64
            for _ in range(nsec):
                pn=struct.unpack_from('<i',info,p)[0]
                if pn in new_sizes: struct.pack_into('<I',info,p+4,new_sizes[pn]); touched+=1
                p+=16
        if touched!=len(new_sizes): raise PatchError('section info: %d/%d pages updated'%(touched,len(new_sizes)))
        comp=compress(bytes(info))+b'\0\0'
        if 20+len(comp)>si['size']: raise PatchError('section info does not fit')
        hdr=struct.pack('<5I',st,dsz,len(comp),ctype,0)
        c=checksum(checksum(0,hdr),comp)
        d[a:a+20]=struct.pack('<5I',st,dsz,len(comp),ctype,c)
        d[a+20:a+20+len(comp)]=comp
        old_end=a+20+csz
        if a+20+len(comp)<old_end: d[a+20+len(comp):old_end]=bytes(old_end-(a+20+len(comp)))  # zero leftover
    open(dst,'wb').write(d)
    return new_sizes

def page_decoded_length(src,pos,end,maxsize):
    dec,ops,_=decompress(src,pos,end,maxsize)
    n=0
    for kind,start,length,_ in ops: n=max(n,start+length)
    return n

# ---------------- v2: rebuild container (allows pages to grow)
import zlib
PM_GAP=bytes.fromhex('3b0e634100000000000000000200000000000000')

def roundup32(n): return (n+31)&~31

def patch_file2(src, dst, **kw):
    g=Dwg(src); d=g.data
    # compute modified decompressed sections exactly as patch_file does, but collect new page payloads
    new_payload={}   # page number -> compressed bytes (incl. 2 trailing zeros)
    tmp=dst+'.tmp_inplace'
    # reuse patch logic: produce page payloads
    payloads=compute_page_payloads(g, **kw)
    new_payload.update(payloads)
    if not new_payload: open(dst,'wb').write(d); return {}
    # section info with new sizes
    si_num=g.hdr['section_info_id']; pm_num=g.hdr['section_map_id']
    si=g.pages[si_num]; a=si['address']
    st,dsz,csz,ctype,cks=struct.unpack_from('<5I',d,a)
    info,_,_=decompress(d,a+20,a+20+csz,dsz); info=bytearray(info)
    num_desc=struct.unpack_from('<I',info,0)[0]; p=20
    for _ in range(num_desc):
        nsec=struct.unpack_from('<I',info,p+8)[0]; p+=96
        for _ in range(nsec):
            pn=struct.unpack_from('<i',info,p)[0]
            if pn in new_payload: struct.pack_into('<I',info,p+4,len(new_payload[pn]))
            p+=16
    si_comp=compress(bytes(info))+b'\0\0'
    si_bytes=struct.pack('<5I',st,dsz,len(si_comp),ctype,0)
    si_bytes=struct.pack('<5I',st,dsz,len(si_comp),ctype,checksum(checksum(0,si_bytes),si_comp))+si_comp
    # emit pages in file order
    out=bytearray(d[0:0x100]); newaddr={}; newsize={}; pm_entries=[]
    oldpos={}  # old address -> new address
    for e in g.pagelist:
        num,size,addr=e['number'],e['size'],e['address']
        here=len(out); oldpos[addr]=here
        if num==pm_num:
            pm_index=len(pm_entries); pm_entries.append([num,size,None]); pm_here=here; break
        if num<0:
            out+=d[addr:addr+size]; pm_entries.append([num,size,e['gap']]); continue
        if num==si_num:
            if len(si_bytes)<=size:
                alloc=size; blk=bytearray(d[addr:addr+size])
                old_end=20+csz
                blk[0:len(si_bytes)]=si_bytes
                for i in range(len(si_bytes),max(len(si_bytes),old_end)): blk[i]=0
            else:
                alloc=roundup32(len(si_bytes)); blk=bytearray(si_bytes)+bytes(alloc-len(si_bytes))
            out+=blk; pm_entries.append([num,alloc,None]); continue
        mask_old=0x4164536b ^ (addr&0xffffffff)
        hv=[x^mask_old for x in struct.unpack_from('<8I',d,addr)]
        if hv[0]!=0x4163043b:
            out+=d[addr:addr+size]; pm_entries.append([num,size,None])
            if here!=addr: raise PatchError('non-data page %d would move'%num)
            continue
        if num in new_payload:
            comp=new_payload[num]
            alloc=max(size, roundup32(32+len(comp)))
            dc=checksum(0,comp)
            hv=[hv[0],hv[1],len(comp),alloc,hv[4],hv[5],0,dc]
            hv[6]=checksum(dc,struct.pack('<8I',*hv))
            body=comp+lcg_bytes(alloc-32-len(comp))
        else:
            alloc=size; body=bytes(d[addr+32:addr+size])
        mask_new=0x4164536b ^ (here&0xffffffff)
        out+=struct.pack('<8I',*[x^mask_new for x in hv])+body
        pm_entries.append([num,alloc,None])
    # page map
    pm=bytearray()
    for num,size,gap in pm_entries:
        pm+=struct.pack('<iI',num,size)
        if gap is not None: pm+=struct.pack('<4I',*gap)
    oldpm=g.pages[pm_num]; a=oldpm['address']
    st,dsz,csz,ctype,cks=struct.unpack_from('<5I',d,a)
    olddec,_,_=decompress(d,a+20,a+20+csz,dsz)
    if len(pm)!=dsz: raise PatchError('page map size changed')
    pm_comp=compress(bytes(pm))+b'\0\0'
    h0=struct.pack('<5I',st,dsz,len(pm_comp),ctype,0)
    out+=struct.pack('<5I',st,dsz,len(pm_comp),ctype,checksum(checksum(0,h0),pm_comp))+pm_comp
    out+=PM_GAP
    second=len(out)
    # file header
    fh=bytearray(g.fh)
    struct.pack_into('<Q',fh,0x54,pm_here-0x100)
    struct.pack_into('<Q',fh,0x2c,pm_here+pm_entries[pm_index][1]-0x100)
    struct.pack_into('<Q',fh,0x34,second)
    fh[0x68:0x6c]=b'\0\0\0\0'; struct.pack_into('<I',fh,0x68,zlib.crc32(bytes(fh))&0xffffffff)
    enc=bytes(a^b for a,b in zip(fh,lcg_bytes(0x6c)))
    out[0x80:0x80+0x6c]=enc
    out+=enc
    # absolute addresses in the fixed header: thumbnail @0x0D, summary @0x20, vba @0x24
    for off in (0x0d,0x20,0x24):
        v=struct.unpack_from('<I',d,off)[0]
        if v==0: continue
        base=max((x for x in oldpos if x<=v), default=None)
        if base is None or oldpos[base]!=base: raise PatchError('absolute address at 0x%x would move'%off)
    open(dst,'wb').write(out)
    return {k:len(v) for k,v in new_payload.items()}

def compute_page_payloads(g, snap_on=None, snap=None, grid_on=None, grid=None, limmin=None, limmax=None, ortho=None):
    res={}
    jobs=[]
    if any(v is not None for v in (snap_on,snap,grid_on,grid)):
        objs,pinfo=g.section_bytes('AcDb:AcDbObjects'); objs=bytearray(objs)
        hnd,_=g.section_bytes('AcDb:Handles')
        vp=find_active_vport(objs, object_map(hnd), g.version); P=vp['pos']
        if grid_on is not None: set_bits(objs,P['gridmode'],1 if grid_on else 0,1)
        if grid is not None: write_RD(objs,P['gridunit'],grid[0]); write_RD(objs,P['gridunit']+64,grid[1])
        if snap_on is not None: set_bits(objs,P['snapmode'],1 if snap_on else 0,1)
        if snap is not None: write_RD(objs,P['snapunit'],snap[0]); write_RD(objs,P['snapunit']+64,snap[1])
        struct.pack_into('<H',objs,vp['crc_pos'],crc16(objs[vp['off']:vp['crc_pos']]))
        jobs.append(('AcDb:AcDbObjects',objs,pinfo))
    if any(v is not None for v in (limmin,limmax,ortho)):
        hb,pinfo=g.section_bytes('AcDb:Header'); hb=bytearray(hb)
        L=header_layout(hb,g.version)
        if ortho is not None: set_bits(hb,L['p_ortho'],1 if ortho else 0,1)
        if limmin is not None: write_RD(hb,L['p_limmin'],limmin[0]); write_RD(hb,L['p_limmin']+64,limmin[1])
        if limmax is not None: write_RD(hb,L['p_limmax'],limmax[0]); write_RD(hb,L['p_limmax']+64,limmax[1])
        struct.pack_into('<H',hb,L['crc_end'],crc16(hb[16:L['crc_end']]))
        jobs.append(('AcDb:Header',hb,pinfo))
    for name,buf,pinfo in jobs:
        sec=g.sections[name]; orig,_=g.section_bytes(name)
        for pi in pinfo:
            start=pi['offset']; end=min(start+sec['max_decomp'],sec['size'])
            if bytes(buf[start:end])==bytes(orig[start:end]): continue
            hd,a=g.read_page(pi['page'])
            full,_,_=decompress(g.data,a+32,a+32+hd['data_size'],sec['max_decomp']); full=bytearray(full)
            full[0:end-start]=buf[start:end]
            n=page_decoded_length(g.data,a+32,a+32+hd['data_size'],sec['max_decomp'])
            comp=compress(bytes(full[:n]))+b'\0\0'
            chk,_,_=decompress(comp,0,len(comp),n)
            if chk!=bytes(full[:n]): raise PatchError('round trip')
            res[pi['page']]=comp
    return res
