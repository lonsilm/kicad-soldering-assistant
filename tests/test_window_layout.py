"""Check the real native startup form at multiple work areas and DPI values.

Does not change Windows display settings or read any KiCad input project.
The app uses the same explicit scaling/reflow path in normal operation.
"""
import json
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]
state = ROOT / 'tests/results/responsive-layout'
exe = ROOT / 'dist/KiCad焊接助手.exe'
result = subprocess.run([str(exe), '--layout-test', '--test-root', str(state)], timeout=90)
report = json.loads((state / 'LAYOUT_TEST.json').read_text(encoding='utf-8'))
assert result.returncode == 0 and report['passed'], report
assert len(report['layouts']) == 18, report
assert {r['dpi'] for r in report['layouts']} == {96, 120, 144, 192}
assert all(Path(r['screenshot']).is_file() for r in report['layouts'])
print('PASS: 16 work-area/DPI layouts plus two resized windows; text fits, window stays inside working area, short pages scroll')
