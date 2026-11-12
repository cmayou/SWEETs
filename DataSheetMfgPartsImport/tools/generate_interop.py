#!/usr/bin/env python3
"""
generate_interop.py  <path-to-EwAPIX.dll>  <output.cs>

Reads the COM type library embedded in EwAPIX.dll (native, no .NET or Windows needed) and writes
Interop/EwInterop.cs: ComImport interface declarations with the exact IIDs and vtable layout of the DLL
you point it at. Re-run it when you move to a different SOLIDWORKS Electrical release, then rebuild.

Only the members listed in USED (below) get real signatures; every other vtable slot becomes a placeholder
so that offsets stay correct. To use another API member, add its name to USED and regenerate.
"""
import struct, sys, uuid

if len(sys.argv) != 3:
    sys.exit(__doc__)
DLL, OUT = sys.argv[1], sys.argv[2]

d = open(DLL, 'rb').read()
cands = [i for i in range(len(d) - 8) if d[i:i+4] == b'MSFT' and struct.unpack_from('<I', d, i + 4)[0] == 0x00010002]
if not cands:
    sys.exit('No MSFT type library found in ' + DLL)
t = d[cands[0]:]

I = lambda o: struct.unpack_from('<i', t, o)[0]
H = lambda o: struct.unpack_from('<h', t, o)[0]
NTI = I(32)                       # number of type infos
SEGDIR = 84 + NTI * 4             # 15 segment entries of 16 bytes follow the typeinfo offset table
seg = lambda k: I(SEGDIR + k * 16)
TI, REFS, GUIDS, NAMES, TD = seg(0), seg(3), seg(5), seg(7), seg(9)

def name(off):
    if off < 0: return None
    o = NAMES + off
    ln = I(o + 8) & 0xff
    return t[o + 12:o + 12 + ln].decode('latin1')

def guid(off):
    if off < 0: return None
    return str(uuid.UUID(bytes_le=t[GUIDS + off:GUIDS + off + 16]))

def parse(i):
    b = TI + i * 100
    ce = I(b + 0x18)
    return dict(i=i, mem=I(b + 4), nf=ce & 0xffff, nv=(ce >> 16) & 0xffff,
                name=name(I(b + 0x34)), guid=guid(I(b + 0x2c)), impl=H(b + 0x4c))

tinames = {i: parse(i)['name'] for i in range(NTI)}
by_name = {n: i for i, n in tinames.items() if n}

# ---- structured type descriptors
def tds(x):
    if x<0:
        vt=x&0xfff; return ('vt',vt)
    vt=I(TD+x)&0xffff; data=I(TD+x+4)
    if vt in (26,27): return ('ptr' if vt==26 else 'sa', tds(data))
    if vt==29: return ('ref',tinames.get(data//100))
    return ('vt',vt)

by_name={}
for i in range(463):
    ti=parse(i)
    if ti['name']: by_name[ti['name']]=i

def base_of(i):
    ti=parse(i)
    if ti['impl']<=0: return None
    href=I(TI+i*100+0x54)
    if href%2==1 or href<0: return None   # imported (IDispatch/IUnknown)
    return href//100

def raw_funcs(i):
    ti=parse(i)
    nf,nv,mem=ti['nf'],ti['nv'],ti['mem']
    if mem<0 or nf==0: return []
    infolen=I(mem); rec=mem+4; end=mem+4+infolen; cnt=nf+nv
    nms=[I(end+cnt*4+k*4) for k in range(cnt)]
    p=rec; out=[]
    for k in range(nf):
        rl=struct.unpack_from('<H',t,p)[0]
        vo=H(p+12); na=H(p+20)
        params=[]
        for a in range(na):
            q=p+rl-(na-a)*12
            params.append((tds(I(q)),name(I(q+4)),I(q+8)&0xffff))
        out.append(dict(name=name(nms[k]),vtoff=vo,params=params,ret=tds(I(p+4))))
        p+=rl
    return out

def chain_funcs(i):
    chain=[]; j=i
    while j is not None:
        chain.append(j); j=base_of(j)
    fs=[]
    for j in reversed(chain): fs+=raw_funcs(j)
    return sorted(fs,key=lambda f:f['vtoff'])

VTMAP={2:'short',3:'int',4:'float',5:'double',22:'int',23:'uint',20:'long',21:'ulong',18:'ushort',19:'uint',16:'sbyte',17:'byte'}
def cs_scalar(d,enums):
    k=d[0]
    if k=='vt':
        vt=d[1]
        if vt==8: return ('string','BSTR')
        if vt==11: return ('bool','VB')
        if vt==12: return ('object','VAR')
        if vt==9: return ('object','DISP')
        if vt==13: return ('object','UNK')
        if vt==7: return ('DateTime',None)
        return (VTMAP.get(vt,'int'),None)
    if k=='ref': return (d[1],'ENUMORIF')
    return ('object',None)

def param_info(d):
    """returns (csType, marshal, ptrdepth) stripping pointers"""
    depth=0
    while d[0]=='ptr': d=d[1]; depth+=1
    if d[0]=='sa': return ('object','Struct',depth)
    ty,m=cs_scalar(d,None)
    return (ty,m,depth)

ENUM_NAMES={'EwErrorCode','EwManufacturerPartType','EwClassificationType'}
def mshal(m,ty):
    if m=='BSTR': return 'MarshalAs(UnmanagedType.BStr)'
    if m=='VB': return 'MarshalAs(UnmanagedType.VariantBool)'
    if m=='VAR': return 'MarshalAs(UnmanagedType.Struct)'
    if m=='DISP': return 'MarshalAs(UnmanagedType.IDispatch)'
    if m=='UNK': return 'MarshalAs(UnmanagedType.IUnknown)'
    return None

def method_decl(f, used):
    ps=f['params']
    ret_idx=None
    for k,(d,n,fl) in enumerate(ps):
        if fl&0x8: ret_idx=k
    args=[]; rettype='void'; retattr=''
    for k,(d,n,fl) in enumerate(ps):
        ty,m,depth=param_info(d)
        if ty in ('EwErrorCode',) and n=='EwErrorCode': n='errorCode'
        n=(n or 'p%d'%k)
        if n in ('string','object','params','ref','out','in','long','int','base','event','lock'): n+='_'
        ma=mshal(m,ty)
        if k==ret_idx:
            rettype=ty
            retattr='[return: %s] '%ma if ma else ''
        elif fl&0x2 and depth>=1:
            args.append(('[%s] '%ma if ma else '')+'out %s %s'%(ty,n))
        else:
            args.append(('[In, %s] '%ma if ma else '')+'%s %s'%(ty,n))
    return '%s%s %s(%s);'%(retattr,rettype,f['name'],', '.join(args))

USED={
 'IEwAddIn':'*',
 'IEwInteropFactoryX':['getEwApplication'],
 'IEwApplicationX':['getEwEnvironment','getEwCommandManager','getEwApplicationSettings','getEwRibbon','getApplicationVersion','getName'],
 'IEwEnvironmentX':['getEwManufacturerPartManager','getEwClassificationManager','getEwCircuitTypeManagerX','getEwLibraryManager','getDatabaseConnectionString','getHostname','getUserID','getPassword'],
 'IEwManufacturerPartManagerX':['newEwManufacturerPart','findByManufacturerAndReference'],
 'IEwManufacturerPartX':['setManufacturer','setReference','setDescription','setCommercialReference','setSerie','setClassNodeID','setEwManufacturerPartType','setWidth','setHeight','setDepth','setWeight','setUseVoltage','setUseFrequency','setControlVoltage','setControlFrequency','setStockNumber','setArticleNumber','setSupplierName','setValue','setUserData','setDataSheetFilePath','getReference','getManufacturer','getClassNodeID','getValue','getUserData','addEwManufacturerPartCircuit','getEwManufacturerPartCircuitCount','insert','update','setLibraryCode','getLibraryCode','setClassID','deleteEwManufacturerPartCircuitAt','getDescription'],
 'IEwClassificationManagerX':['getEwClassification'],
 'IEwClassificationX':['getEwClassArray'],
 'IEwClassX':['getID','getParentID','getDescription'],
 'IEwCommandManagerX':['addCommand','removeCommand'],
 'IEwCommandX':'*',
 'IEwCommandContextX':'*',
 'IEwApplicationSettingsX':['getLanguageArray'],
 'IEwCircuitTypeManagerX':['getEwCircuitTypeArray','getEwCircuitTypeByID'],
 'IEwLibraryManagerX':['newEwLibrary','getEwLibraryArray','findEwLibraryById'],
 'IEwLibraryX':['getName','setName','setDescription','setVersionNumber','setEwLibContentType','getEwLibContentType','insert'],
 'IEwCircuitTypeX':['getKeyCode','getDescription','getID'],
 'IEwManufacturerPartCircuitX':['addEwManufacturerPartTerminal','getEwManufacturerPartTerminalCount'],
 'IEwManufacturerPartTerminalX':['setText'],
 'IEwRibbonX':['addTab','recalcLayout'],
 'IEwRibbonTabX':['addGroup','remove'],
 'IEwRibbonGroupX':['addControl'],
 'IEwRibbonControlX':['setCaption','setToolTip','setDescription','setIconFromImage','setCommand','setStyle'],
}
out=['// <auto-generated>',
'// Generated from the type library embedded in EwAPIX.dll (see the DLL file version).',
'// Interface IIDs and vtable slots come from that type library. Unused slots are placeholders so offsets stay correct.',
'// </auto-generated>',
'using System;','using System.Runtime.InteropServices;','',
'namespace DataSheetMfgPartsImport.Interop','{']

def enum_block(name):
    i=by_name[name]; ti=parse(i)
    nf,nv,mem=ti['nf'],ti['nv'],ti['mem']
    infolen=I(mem); p=mem+4; end=mem+4+infolen; cnt=nf+nv
    nms=[I(end+cnt*4+k*4) for k in range(cnt)]
    items=[]
    for k in range(nv):
        rl=struct.unpack_from('<H',t,p)[0]
        rec=struct.unpack_from('<%di'%(rl//4),t,p)
        raw=rec[4]
        val=(raw&0x3ffffff) if raw<0 else -1
        items.append((name_fn(nms[nf+k]),val)); p+=rl
    items.sort(key=lambda x:x[1])
    lines=['    public enum %s'%name,'    {']
    for n,v in items: lines.append('        %s = %d,'%(n,v))
    lines.append('    }')
    return lines
name_fn = name
for e in ['EwErrorCode','EwManufacturerPartType','EwClassificationType','EwRibbonControlType','EwRibbonButtonSize','EwLibContentType','EwRibbonButtonStyle','EwCommandType']:
    out+=enum_block(e); out.append('')

report=[]
for iname,used in USED.items():
    i=by_name[iname]; g=parse(i)['guid']
    fs=chain_funcs(i)
    slots={f['vtoff']//8:f for f in fs}
    out.append('    [ComImport, Guid("%s"), InterfaceType(ComInterfaceType.InterfaceIsDual)]'%g)
    out.append('    public interface %s'%iname)
    out.append('    {')
    if slots:
        for s in range(7,max(slots)+1):
            f=slots.get(s)
            if f is None:
                out.append('        void _slot%d();'%s); continue
            if used=='*' or f['name'] in used:
                out.append('        %s // slot %d'%(method_decl(f,used),s))
            else:
                out.append('        void _%s_%d();'%(f['name'],s))
    out.append('    }'); out.append('')
    report.append((iname,g,len(slots),max(slots) if slots else 0))
out.append('}')
open(OUT,'w',encoding='utf-8').write('\n'.join(out))
for r in report: print(r)
