"""Package runtime files only; no input projects, user settings or test state."""
from pathlib import Path
import hashlib
import json
import zipfile

root = Path(__file__).resolve().parents[1]
dist = root/'dist'
dist.mkdir(exist_ok=True)
files = [root/'启动焊接助手.cmd',root/'使用说明.md',root/'AGENTS.md',root/'LICENSE']
files += [root/'app'/name for name in ['generate.py','launcher.ps1','assistant.js','assistant.css']]
vendor = root/'vendor/InteractiveHtmlBom'
files += [vendor/'LICENSE',vendor/'README.md']
files += [p for p in (vendor/'InteractiveHtmlBom').rglob('*') if p.is_file() and '__pycache__' not in p.parts and p.suffix not in {'.pyc','.pyo'}]
archive = dist/'KiCad焊接助手_通用版.zip'
with zipfile.ZipFile(archive,'w',compression=zipfile.ZIP_DEFLATED) as z:
    for p in sorted(set(files)):
        z.write(p,Path('KiCad焊接助手')/p.relative_to(root))
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None
    assert all('output/' not in name and 'settings.json' not in name and '__pycache__' not in name for name in z.namelist())
    assert 'KiCad焊接助手/vendor/InteractiveHtmlBom/LICENSE' in z.namelist()
report = dict(archive=str(archive),bytes=archive.stat().st_size,sha256=hashlib.sha256(archive.read_bytes()).hexdigest(),files=len(set(files)),verified=True)
(dist/'RELEASE.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(report,ensure_ascii=False))

# The native application is distributed independently as one executable.
exe = dist/'KiCad焊接助手.exe'
if exe.is_file():
    native = dist/'KiCadSolderingAssistant-v1.3.0-Windows.zip'
    with zipfile.ZipFile(native,'w',compression=zipfile.ZIP_DEFLATED) as z:
        z.write(exe,exe.name)
        z.write(root/'使用说明.md','使用说明.md')
        z.write(root/'LICENSE','LICENSE')
        z.write(vendor/'LICENSE','ThirdParty/InteractiveHtmlBom-LICENSE')
        z.write(root/'docs/WebView2-LICENSE.txt','ThirdParty/WebView2-LICENSE.txt')
    with zipfile.ZipFile(native) as z:
        assert z.testzip() is None
    (dist/'SHA256SUMS.txt').write_bytes((hashlib.sha256(exe.read_bytes()).hexdigest()+'  '+exe.name+'\n').encode('utf-8'))
    print('Native Windows package: '+str(native))
