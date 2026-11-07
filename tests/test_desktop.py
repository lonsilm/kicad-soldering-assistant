"""Integration checks for the actual, copied, single-file Windows executable.

Run with KiCad's Python after test_backend.py and desktop/build_exe.py.
Optionally pass --board a real PCB/project (read-only) or KICAD_TEST_BOARD.
Uses an isolated application data folder, not the user's actual app preferences.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess

ROOT=Path(__file__).resolve().parents[1]
RESULTS=ROOT/'tests/results'
EXE=ROOT/'dist/KiCad焊接助手.exe'
fixture=ROOT/"tests/fixtures/中文 空间's & test/same.kicad_pcb"
assert fixture.is_file(),'Run test_backend.py using KiCad Python first'
assert EXE.is_file(),'Run desktop/build_exe.py first'
RESULTS.mkdir(exist_ok=True)
portable=RESULTS/"单独 应用's & folder"
portable.mkdir(exist_ok=True)
copied=portable/EXE.name
shutil.copyfile(EXE,copied)
assert list(portable.iterdir())==[copied],'Portable smoke test must have only the EXE'
image=copied.read_bytes()
pe=struct.unpack_from('<I',image,0x3c)[0]
assert image[:2]==b'MZ' and image[pe:pe+4]==b'PE\0\0'
assert struct.unpack_from('<H',image,pe+24+68)[0]==2,'Must be GUI subsystem'
cases=[]

def run(name,input_path,expected=True):
    state=RESULTS/'desktop_states'/name
    p=subprocess.run([str(copied),'--self-test',str(input_path),'--test-root',str(state)],timeout=120)
    report=json.loads((state/'SELF_TEST.json').read_text(encoding='utf-8'))
    assert (p.returncode==0)==expected,(name,report)
    assert report['passed']==expected,(name,report)
    if expected:
        assert report['source_hashes_unchanged']
        workspace=report['workspace']
        for key in ['embedded','search_highlight','progress_reload','native_backup_restore','native_file_export','front_back','project_isolation','cached_ui_upgrade','no_script_errors','offline_requests_blocked']:
            assert workspace[key],(name,key)
        assert workspace['external_browser_opened'] is False
        manifest=json.loads((Path(report['html']).parent/'manifest.json').read_text(encoding='utf-8'))
        for source,digest in manifest['source_hashes'].items():assert hashlib.sha256(Path(source).read_bytes()).hexdigest()==digest
    cases.append(dict(name=name,expected_success=expected,**report))
    return state,report

state,report=run('portable-special-path',fixture)
assert report['assembly_count']==2 and report['bom_groups']==2
native=Path(report['runtime_root'])
assert native.is_relative_to(state.resolve())
generator=native/'app/generate.py'
original=generator.read_bytes()
generator.write_text('# simulated corrupted private test cache\n',encoding='utf-8')
state,repaired=run('portable-special-path',fixture)
assert generator.read_bytes()==original,'Executable must restore its embedded files'
assert repaired['workspace']['prior_session_progress'],'Progress must survive closing and restarting the EXE'
assert repaired['html']==report['html'],'Same source must retain output identity'
prefs=json.loads((state/'settings.json').read_text(encoding='utf-8'))
assert len(prefs['Recent'])==1,'Regeneration must not duplicate recent projects'
run('multiple-boards',fixture.parent,False)
run('missing-path',fixture.parent/'no-such-board.kicad_pcb',False)
run('unsupported-file',ROOT/'README.md',False)
parser=argparse.ArgumentParser()
parser.add_argument('--board',action='append',default=[])
args=parser.parse_args()
boards=args.board+([os.environ['KICAD_TEST_BOARD']] if os.environ.get('KICAD_TEST_BOARD') else [])
for index,board in enumerate(boards):run('real-board-'+str(index),board)
result=dict(passed=True,single_copied_exe_sha256=hashlib.sha256(image).hexdigest(),cases=cases,
    checks=['Windows GUI subsystem','single EXE with no adjacent resources','Unicode space apostrophe ampersand paths',
    'real native window generation','source hashes unchanged','corrupt cache self-repair','stable output identity',
    'recent projects deduplication','embedded WebView2 workspace','no external HTML shell navigation',
    'native progress backup/restore','progress survives EXE restart','project-isolated storage','offline resource blocking','multi-board selection required','invalid inputs produce usable errors'])
(RESULTS/'DESKTOP_VALIDATION.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print('PASS: copied EXE, embedded workspace, highlight, native backup/restore, restart persistence, isolation, offline, read-only boards')
