import sys,struct
from dwg2004 import *
g=Dwg(sys.argv[1])
objs,_=g.section_bytes('AcDb:AcDbObjects')
hnd,_=g.section_bytes('AcDb:Handles')
om=object_map(hnd)
print('objects',len(om))
for h,off in om:
    size,p=read_MS(objs,off); hsize,p=read_UMC(objs,p)
    r=Bits(objs,p*8); t=r.OT()
    if t!=65: continue
    bitsize=size*8-hsize
    data_start=p*8
    code,hv=r.H()
    # EED
    while True:
        n=r.BS()
        if n==0: break
        r.H(); r.p+=8*n
    nreact=r.BL(); xdicmiss=r.B(); hasds=r.B()
    sp=object_strings_start(objs,data_start,bitsize)
    name=None
    if sp is not None:
        s=Bits(objs,sp); name=s.TU()
    xref_res=r.BS()
    f={}
    f['VIEWSIZE']=r.BD(); f['view_width']=r.BD(); f['VIEWCTR']=(r.RD(),r.RD())
    f['target']=(r.BD(),r.BD(),r.BD()); f['dir']=(r.BD(),r.BD(),r.BD()); f['twist']=r.BD(); f['lens']=r.BD(); f['front']=r.BD(); f['back']=r.BD()
    f['VIEWMODE']=r.bits(4); f['render_mode']=r.RC()
    f['use_default_lights']=r.B(); f['lighting']=r.RC(); f['brightness']=r.BD(); f['contrast']=r.BD(); f['ambient']=r.CMC()
    f['lower_left']=(r.RD(),r.RD()); f['upper_right']=(r.RD(),r.RD()); f['UCSFOLLOW']=r.B(); f['circle_zoom']=r.BS(); f['FASTZOOM']=r.B(); f['UCSICON']=r.BB()
    pos_gridmode=r.p; f['GRIDMODE']=r.B()
    pos_gridunit=r.p; f['GRIDUNIT']=(r.RD(),r.RD())
    pos_snapmode=r.p; f['SNAPMODE']=r.B()
    f['SNAPSTYLE']=r.B(); f['SNAPISOPAIR']=r.BS(); f['SNAPANG']=r.BD(); f['SNAPBASE']=(r.RD(),r.RD())
    pos_snapunit=r.p; f['SNAPUNIT']=(r.RD(),r.RD())
    print('VPORT handle',hex(h),'off',off,'size',size,'name',name, 'ambient',f['ambient'])
    print('  ',{k:f[k] for k in ['GRIDMODE','GRIDUNIT','SNAPMODE','SNAPUNIT','lower_left','upper_right']})
    print('   bitpos rel data start: gridmode',divmod(pos_gridmode-data_start,8),'gridunit',divmod(pos_gridunit-data_start,8),'snapmode',divmod(pos_snapmode-data_start,8),'snapunit',divmod(pos_snapunit-data_start,8))
    crc_stored=struct.unpack_from('<H',objs,p+size)[0]
    print('   crc stored',hex(crc_stored),'calc',hex(crc16(objs[off:p+size])))
