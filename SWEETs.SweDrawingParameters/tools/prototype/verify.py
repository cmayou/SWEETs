import sys,json,struct,subprocess
from dwg2004 import *
def check_pages(path):
    g=Dwg(path); bad=[]
    for name,s in g.sections.items():
        for pg in s['pages']:
            hd,a=g.read_page(pg['number'])
            comp=bytes(g.data[a+32:a+32+hd['data_size']]); dc=checksum(0,comp)
            hv=[hd['page_type'],hd['section_type'],hd['data_size'],hd['page_size'],hd['offset'],hd['unknown'],0,dc]
            if dc!=hd['data_crc'] or checksum(dc,struct.pack('<8I',*hv))!=hd['hdr_crc']: bad.append(pg['number'])
            if pg['size']!=hd['data_size']: bad.append(('infosize',pg['number']))
    return bad
def dump(path,out):
    subprocess.run(['/tmp/claude-0/libredwg/b/dwgread','-v2','-O','JSON','-o',out,path],stderr=open(out+'.err','w'))
    return json.load(open(out)), [l for l in open(out+'.err') if any(w in l.lower() for w in ('error','mismatch','crc'))]
def diff(a,b):
    out=[]
    def walk(x,y,p):
        if type(x)!=type(y): out.append((p,x,y)); return
        if isinstance(x,dict):
            for k in set(x)|set(y): walk(x.get(k),y.get(k),p+'.'+k)
        elif isinstance(x,list):
            if len(x)!=len(y): out.append((p,'len')); return
            for i,(q,r) in enumerate(zip(x,y)): walk(q,r,f'{p}[{i}]')
        elif x!=y: out.append((p,x,y))
    walk(a,b,''); return out
if __name__=='__main__':
    src=sys.argv[1]; kw=eval(sys.argv[2]); dst='p_'+src
    sizes=patch_file(src,dst,**kw)
    print(src, 'rewritten pages', sizes, 'bad checksums', check_pages(dst))
    a,ea=dump(src,'/tmp/claude-0/dwg/a.json'); b,eb=dump(dst,'/tmp/claude-0/dwg/b.json')
    print('  libredwg warnings equal:', sorted(ea)==sorted(eb), len(ea), len(eb))
    for d in diff(a,b): print('  ',d)
