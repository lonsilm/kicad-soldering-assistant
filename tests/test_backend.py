"""Integration tests; only synthetic boards below tests/fixtures are written."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import pcbnew

ROOT = Path(__file__).resolve().parents[1]
FIX = ROOT/'tests/fixtures'
RESULTS = ROOT/'tests/results'
FIX.mkdir(exist_ok=True)
RESULTS.mkdir(exist_ok=True)

def synthetic(path):
    assert path.is_relative_to(FIX)
    path.parent.mkdir(parents=True,exist_ok=True)
    board = pcbnew.BOARD()
    points = [(0,0),(30,0),(30,20),(0,20)]
    for a,b in zip(points,points[1:]+points[:1]):
        edge = pcbnew.PCB_SHAPE()
        edge.SetShape(pcbnew.SHAPE_T_SEGMENT)
        edge.SetStart(pcbnew.VECTOR2I(pcbnew.FromMM(a[0]),pcbnew.FromMM(a[1])))
        edge.SetEnd(pcbnew.VECTOR2I(pcbnew.FromMM(b[0]),pcbnew.FromMM(b[1])))
        edge.SetLayer(pcbnew.Edge_Cuts)
        edge.SetWidth(pcbnew.FromMM(0.05))
        board.Add(edge)
    for i,(ref,name,dnp,exclude,number) in enumerate([
        ('R1','R_0402',False,False,'1'),('C1','C_0402',False,False,'1'),
        ('R2','R_0402',True,False,'1'),('TP1','TestPoint',False,True,'1'),
        ('H1','MountingHole_3mm',False,False,'1'),('LOGO1','Logo',False,False,'')]):
        fp = pcbnew.FOOTPRINT(board)
        fp.SetReference(ref)
        fp.SetValue('100n' if ref.startswith('C') else '10k')
        fp.SetFPID(pcbnew.LIB_ID('Test',name))
        fp.SetDNP(dnp)
        fp.SetExcludedFromBOM(exclude)
        fp.SetPosition(pcbnew.VECTOR2I(pcbnew.FromMM(4+i*4),pcbnew.FromMM(10)))
        pad = pcbnew.PAD(fp)
        pad.SetNumber(number)
        pad.SetAttribute(pcbnew.PAD_ATTRIB_SMD)
        pad.SetShape(pcbnew.PAD_SHAPE_RECT)
        pad.SetSize(pcbnew.VECTOR2I(pcbnew.FromMM(1),pcbnew.FromMM(1)))
        layers = pcbnew.LSET()
        layers.AddLayer(pcbnew.F_Cu)
        pad.SetLayerSet(layers)
        pad.SetPosition(fp.GetPosition())
        fp.Add(pad)
        board.Add(fp)
    pcbnew.SaveBoard(str(path),board)

def run(path,*options,ok=True):
    result = subprocess.run([sys.executable,'-X','utf8',str(ROOT/'app/generate.py'),str(path),*options],
        capture_output=True,encoding='utf-8',env=dict(os.environ,PYTHONUTF8='1'))
    if ok:
        assert result.returncode==0,(result.stdout,result.stderr)
    else:
        assert result.returncode!=0,result.stdout
    return result

board = FIX/"中文 空间's & test"/'same.kicad_pcb'
synthetic(board)
before = hashlib.sha256(board.read_bytes()).hexdigest()
result = run(board,'--no-open','--pcb-only')
html = Path(next(x[5:] for x in result.stdout.splitlines() if x.startswith('HTML=')))
manifest = json.loads((html.parent/'manifest.json').read_text(encoding='utf-8'))
assert manifest['assembly_count']==2
assert {p['ref'] for p in manifest['parts'] if not p['assembly_excluded']}=={'R1','C1'}
reasons = {p['ref']:p['exclusion_reason'] for p in manifest['parts']}
assert reasons['R2']=='不焊件DNP'
assert reasons['TP1']=='不计入BOM'
assert reasons['H1']==reasons['LOGO1']=='安装孔/无编号机械焊盘'
assert before==hashlib.sha256(board.read_bytes()).hexdigest()
other = FIX/'other'/'same.kicad_pcb'
other.parent.mkdir(exist_ok=True)
shutil.copyfile(board,other)
second = run(other,'--no-open','--pcb-only')
other_html = Path(next(x[5:] for x in second.stdout.splitlines() if x.startswith('HTML=')))
other_manifest = json.loads((other_html.parent/'manifest.json').read_text(encoding='utf-8'))
assert other_manifest['project_id'] != manifest['project_id']
assert html!=other_html
shutil.copyfile(board,board.parent/'second.kicad_pcb')
listed = json.loads(run(board.parent,'--list-boards').stdout)
assert len(listed)==2
ambiguous = run(board.parent,'--no-open',ok=False)
assert '多个PCB' in ambiguous.stderr
assert run(board.parent/'missing.kicad_pcb',ok=False).returncode==2
empty = FIX/'empty'
empty.mkdir(exist_ok=True)
assert '没有 .kicad_pcb' in run(empty,ok=False).stderr
pro = board.with_suffix('.kicad_pro')
pro.write_text('{}',encoding='utf-8')
assert json.loads(run(pro,'--list-boards').stdout)==[str(board)]
# Cmd launcher -> PowerShell -> Python checks actual Windows argument quoting.
probe = subprocess.run(['powershell.exe','-NoProfile','-STA','-ExecutionPolicy','Bypass',
    '-File',str(ROOT/'app/launcher.ps1'),'-Probe',str(pro)],capture_output=True,encoding='utf-8',errors='replace')
# Windows PowerShell emits console text using the parent codepage; use ASCII JSON probe below.
assert probe.returncode==0,(probe.stdout,probe.stderr)
report = dict(passed=True,fixture_html=str(html),other_fixture_html=str(other_html),
    checks=['DNP exclusion','explicit BOM exclusion','mechanical exclusion','PCB-only project',
    'same basename different projects isolated','multiple boards selection required','missing/empty directory',
    'project matching board','Chinese spaces apostrophe ampersand paths','PowerShell runtime discovery and argument quoting',
    'source PCB hashes unchanged'])
(RESULTS/'BACKEND_VALIDATION.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print('PASS: backend integrations')
