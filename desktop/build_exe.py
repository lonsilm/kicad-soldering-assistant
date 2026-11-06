"""Build a single native Windows EXE with an embedded, read-only KiCad backend.

Uses only Python's standard library, Windows PowerShell, and the Windows .NET
Framework C# compiler. No third-party packager, KiCad project, or user state is
included in the executable.
"""
import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess
import zipfile

ROOT=Path(__file__).resolve().parents[1]
BUILD=ROOT/'build/desktop'
DIST=ROOT/'dist'
ASSETS=ROOT/'desktop/assets'
for directory in [BUILD,DIST,ASSETS]:directory.mkdir(parents=True,exist_ok=True)

def run(args):
    p=subprocess.run([str(x) for x in args],cwd=ROOT,capture_output=True,encoding='utf-8',errors='replace')
    if p.returncode:raise RuntimeError(p.stdout+p.stderr)
    if p.stdout.strip():print(p.stdout.strip())

run(['powershell.exe','-NoProfile','-ExecutionPolicy','Bypass','-File',ROOT/'desktop/create_icon.ps1','-OutputDirectory',ASSETS])
files=[ROOT/'app'/name for name in ['generate.py','assistant.js','assistant.css']]
vendor=ROOT/'vendor/InteractiveHtmlBom'
files += [vendor/'LICENSE',vendor/'README.md']
files += [p for p in (vendor/'InteractiveHtmlBom').rglob('*') if p.is_file() and '__pycache__' not in p.parts and p.suffix not in {'.pyc','.pyo'}]
payload=BUILD/'Runtime.zip'
with zipfile.ZipFile(payload,'w',zipfile.ZIP_DEFLATED) as z:
    for p in sorted(set(files)):
        entry=zipfile.ZipInfo(p.relative_to(ROOT).as_posix(),date_time=(2026,1,1,0,0,0))
        entry.compress_type=zipfile.ZIP_DEFLATED
        z.writestr(entry,p.read_bytes())
    entry=zipfile.ZipInfo('runtime_manifest.json',date_time=(2026,1,1,0,0,0))
    entry.compress_type=zipfile.ZIP_DEFLATED
    z.writestr(entry,json.dumps({p.relative_to(ROOT).as_posix():hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(set(files))},sort_keys=True))
with zipfile.ZipFile(payload) as z:
    assert z.testzip() is None
    assert all(not any(segment in name.split('/') for segment in ['output','fixtures','results','settings.json']) for name in z.namelist())
compiler=Path(os.environ.get('WINDIR',r'C:\Windows'))/'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if not compiler.is_file():compiler=Path(os.environ.get('WINDIR',r'C:\Windows'))/'Microsoft.NET/Framework/v4.0.30319/csc.exe'
exe=DIST/'KiCad焊接助手.exe'
run([compiler,'/nologo','/target:winexe','/platform:anycpu','/optimize+',
    '/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll',
    '/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:System.Web.Extensions.dll',
    '/resource:'+str(payload)+',Runtime.zip','/win32icon:'+str(ASSETS/'app.ico'),
    '/win32manifest:'+str(ROOT/'desktop/app.manifest'),'/out:'+str(exe),ROOT/'desktop/Program.cs'])
data=exe.read_bytes()
assert data[:2]==b'MZ'
pe=struct.unpack_from('<I',data,0x3c)[0]
assert data[pe:pe+4]==b'PE\0\0'
subsystem=struct.unpack_from('<H',data,pe+24+68)[0]
assert subsystem==2,'EXE must be a GUI application, without console'
report=dict(version='1.1.0',exe=str(exe),bytes=len(data),sha256=hashlib.sha256(data).hexdigest(),
    subsystem='Windows GUI',single_file=True,payload_files=len(set(files)),
    payload_sha256=hashlib.sha256(payload.read_bytes()).hexdigest(),
    requires='Windows 10/11 with .NET Framework 4.8; installed KiCad for generation',
    source_projects_embedded=False,signing='unsigned')
(DIST/'EXE_BUILD.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps(report,ensure_ascii=False))
