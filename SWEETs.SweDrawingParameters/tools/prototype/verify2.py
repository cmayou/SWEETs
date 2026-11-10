import sys
from verify import *
from dwg2004 import patch_file2
src=sys.argv[1]; kw=eval(sys.argv[2]); dst='q_'+src
sizes=patch_file2(src,dst,**kw)
print(src,'rewritten',sizes,'bad',check_pages(dst))
a,ea=dump(src,'/tmp/claude-0/dwg/a.json'); b,eb=dump(dst,'/tmp/claude-0/dwg/b.json')
print('  warnings equal:',sorted(ea)==sorted(eb),len(ea),len(eb))
for x in diff(a,b): print('  ',x)
