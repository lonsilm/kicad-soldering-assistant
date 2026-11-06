"""Read-only KiCad board -> self-contained Chinese assembly guide.

Run with KiCad's bundled Python. No PCB/project save APIs are used.
"""
import argparse
import csv
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import webbrowser
import xml.etree.ElementTree as ET

os.environ.setdefault('INTERACTIVE_HTML_BOM_NO_DISPLAY','1')
os.environ['INTERACTIVE_HTML_BOM_CLI_MODE'] = '1'

APP = Path(__file__).resolve().parent
HOME_DIR = APP.parent

class UserError(Exception):
    pass

def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()

def board_candidates(path):
    path = Path(path).expanduser().resolve()
    if not path.exists():
        raise UserError('路径不存在：' + str(path))
    if path.is_file():
        if path.suffix.lower() == '.kicad_pcb':
            return [path]
        if path.suffix.lower() not in {'.kicad_pro','.kicad_sch'}:
            raise UserError('请选择 KiCad 工程、PCB、根原理图或工程文件夹。')
        same = path.with_suffix('.kicad_pcb')
        if same.is_file():
            return [same]
        path = path.parent
    # Deliberately ignore backup/revision directories.
    found = sorted(p for p in path.glob('*.kicad_pcb') if not p.name.startswith('~'))
    if not found:
        raise UserError('此目录没有 .kicad_pcb 文件。请拖入包含实际PCB的文件夹。')
    return found

def select_board(path):
    found = board_candidates(path)
    if len(found) != 1:
        raise UserError('目录有多个PCB，请选择具体板文件：\n' + '\n'.join(str(p) for p in found))
    return found[0]

def protected_sources(board):
    paths = {board}
    for suffix in ['.kicad_pro','.kicad_prl','.kicad_dru','.kicad_sch']:
        p = board.with_suffix(suffix)
        if p.is_file():
            paths.add(p)
    for name in ['fp-lib-table','sym-lib-table']:
        p = board.parent/name
        if p.is_file():
            paths.add(p)
    pending = [p for p in paths if p.suffix == '.kicad_sch']
    while pending:
        p = pending.pop()
        text = p.read_text(encoding='utf-8-sig')
        for match in re.finditer(r'\(property\s+"Sheetfile"\s+("(?:[^"\\]|\\.)*")',text,re.I):
            try:
                name = json.loads(match.group(1)).replace('${KIPRJMOD}',str(board.parent))
            except ValueError:
                continue
            child = (p.parent/name).resolve()
            if child.is_file() and child not in paths:
                paths.add(child)
                pending.append(child)
    return {str(p):digest(p) for p in sorted(paths)}

def lookup(fields, aliases):
    normalized = {re.sub(r'[\s_\-]','',str(k)).casefold():str(v or '') for k,v in fields.items()}
    for key in aliases:
        value = normalized.get(re.sub(r'[\s_\-]','',key).casefold(),'')
        if value and value != '~':
            return value
    return ''

MPN = ['MPN','Manufacturer Part Number','Manufacturer_Part_Number','制造商料号','制造商料号MPN','制造商零件编号','料号']
NOTES = ['装配备注','核对备注','Assembly Notes','Notes','备注']

def csv_metadata(board, explicit=None):
    names = [board.stem+'_current_bom.csv',board.stem+'_bom.csv','bom_current.csv','bom.csv']
    candidates = [Path(explicit).resolve()] if explicit else [parent/name for name in names for parent in [board.parent/'bom',board.parent]]
    entries = {}
    used = []
    for path in candidates:
        if not path.is_file():
            continue
        try:
            raw = path.read_bytes()
            try:
                text = raw.decode('utf-8-sig')
            except UnicodeDecodeError:
                text = raw.decode('gb18030')
            rows = csv.DictReader(text.splitlines())
            for row in rows:
                refs = lookup(row,['位号','References','Reference','Designator','Designators','Refs'])
                value = lookup(row,['值/型号','Value','值'])
                footprint = lookup(row,['完整KiCad封装','Footprint','封装'])
                if not refs or not value or not footprint:
                    continue
                for ref in re.split(r'[,;，；\s]+',refs):
                    if ref and ref not in entries:
                        entries[ref] = dict(value=value,footprint=footprint,mpn=lookup(row,MPN),note=lookup(row,NOTES))
            used.append(str(path))
        except (UnicodeError,csv.Error):
            continue
    return entries, used

def schematic_metadata(board, staging, enabled):
    sch = board.with_suffix('.kicad_sch')
    cli = Path(sys.executable).parent/'kicad-cli.exe'
    if not cli.is_file():
        name = shutil.which('kicad-cli')
        cli = Path(name) if name else cli
    if not enabled or not sch.is_file() or not cli.is_file():
        return {}, '使用PCB自身的器件信息'
    exported = staging/'schematic.net'
    result = subprocess.run([str(cli),'sch','export','netlist','--format','kicadxml','-o',str(exported),str(sch)],
                            capture_output=True,encoding='utf-8',errors='replace')
    if result.returncode != 0:
        (staging/'schematic_export.log').write_text(result.stdout+'\n'+result.stderr,encoding='utf-8')
        return {}, '原理图补充信息未导出，已使用PCB自身信息'
    root = ET.parse(exported).getroot()
    found = {}
    for c in root.findall('./components/comp'):
        fields = {f.attrib['name']:f.text or '' for f in c.findall('./fields/field')}
        found[c.attrib['ref']] = dict(value=c.findtext('value',''),footprint=c.findtext('footprint',''),fields=fields)
    return found, '原理图仅补充与PCB值/封装一致的物料信息'

def same_footprint(a,b):
    return a == b or (':' not in a and a == b.split(':')[-1])

def build(args):
    try:
        import pcbnew
    except ImportError:
        raise UserError('需要使用KiCad自带的Python启动本工具；请运行工具根目录的启动器。')
    board_path = select_board(args.input)
    project_id = hashlib.sha256(os.path.normcase(str(board_path)).encode('utf-8')).hexdigest()[:20]
    slug = re.sub(r'[<>:"/\\|?*\x00-\x1f]','_',board_path.stem).rstrip(' .') or 'board'
    output_root = Path(args.output_root).expanduser().resolve() if args.output_root else HOME_DIR/'output'
    variant_id = hashlib.sha256(args.variant.encode('utf-8')).hexdigest()[:8] if args.variant else ''
    destination = Path(args.output).expanduser().resolve() if args.output else output_root/(slug+'-'+project_id[:8]+('-'+variant_id if variant_id else ''))
    if destination == board_path.parent:
        raise UserError('请选择独立的输出子目录，不能把输出目录直接设为工程根目录。')
    destination.mkdir(parents=True,exist_ok=True)
    before = protected_sources(board_path)
    csvs, csv_paths = csv_metadata(board_path,args.bom)
    before.update({p:digest(p) for p in csv_paths})
    print('读取PCB：'+str(board_path),flush=True)
    board = pcbnew.LoadBoard(str(board_path))
    if board is None:
        raise UserError('KiCad未能读取这个PCB。')
    if hasattr(board,'SetCurrentVariant'):
        board.SetCurrentVariant(args.variant)
    footprints = list(board.GetFootprints())
    refs = [fp.GetReference() for fp in footprints]
    if len(refs) != len(set(refs)) or any(not r for r in refs):
        raise UserError('PCB位号重复或为空，无法准确保存逐器件进度。请先在工程中核对位号。')
    sys.path.insert(0,str(HOME_DIR/'vendor/InteractiveHtmlBom'))
    from InteractiveHtmlBom.core import ibom
    from InteractiveHtmlBom.core.config import Config
    from InteractiveHtmlBom.ecad.kicad import PcbnewParser
    from InteractiveHtmlBom.version import version
    logger = ibom.Logger(cli=True)
    ibom.log = logger
    with tempfile.TemporaryDirectory(prefix='.building-',dir=destination) as tmp:
        staging = Path(tmp)
        sch, supplement = schematic_metadata(board_path,staging,not args.pcb_only)
        xml_root = ET.Element('export',version='D')
        xml_components = ET.SubElement(xml_root,'components')
        blacklist = []
        parts = []
        mismatches = []
        for fp in footprints:
            ref,value = fp.GetReference(),fp.GetValue()
            footprint = fp.GetFPID().GetUniStringLibId()
            raw = dict(fp.GetFieldsShownText()) if hasattr(fp,'GetFieldsShownText') else dict(fp.GetProperties())
            extra = sch.get(ref)
            note = lookup(raw,NOTES)
            if extra:
                if extra['value'] == value and extra['footprint'] == footprint:
                    raw = dict(extra['fields'],**raw)
                    # An empty board field should not erase a populated schematic supplement.
                    for key,val in extra['fields'].items():
                        if not raw.get(key):
                            raw[key] = val
                else:
                    mismatches.append(ref)
                    note = (note+'；' if note else '')+'原理图值/封装与PCB不同；本表按PCB显示'
            mpn = lookup(raw,MPN)
            candidate = csvs.get(ref)
            if candidate and candidate['value']==value and same_footprint(candidate['footprint'],footprint):
                mpn = mpn or candidate['mpn']
                note = note or candidate['note']
            note = note or lookup(raw,NOTES)
            dnp = bool(fp.GetDNPForVariant(args.variant)) if hasattr(fp,'GetDNPForVariant') else bool(fp.IsDNP())
            excluded = bool(fp.GetExcludedFromBOMForVariant(args.variant)) if hasattr(fp,'GetExcludedFromBOMForVariant') else bool(fp.IsExcludedFromBOM())
            pads = list(fp.Pads())
            mechanical = footprint.split(':')[-1].startswith('MountingHole') or not any(p.GetNumber().strip() for p in pads)
            reason = '不焊件DNP' if dnp else '不计入BOM' if excluded else '安装孔/无编号机械焊盘' if mechanical else ''
            if reason:
                blacklist.append(ref)
            comp = ET.SubElement(xml_components,'comp',ref=ref)
            fields = ET.SubElement(comp,'fields')
            for key,val in [('MPN',mpn),('装配备注',note)]:
                ET.SubElement(fields,'field',name=key).text = val
            parts.append(dict(ref=ref,value=value,footprint=footprint,mpn=mpn,note=note,
                side='F' if fp.GetLayer()==pcbnew.F_Cu else 'B',
                x_mm=pcbnew.ToMM(fp.GetPosition().x),y_mm=pcbnew.ToMM(fp.GetPosition().y),
                angle_degrees=fp.GetOrientationDegrees(),dnp=dnp,excluded_from_bom=excluded,
                assembly_excluded=bool(reason),exclusion_reason=reason))
        fields_path = staging/'assembly_fields.xml'
        ET.ElementTree(xml_root).write(fields_path,encoding='utf-8',xml_declaration=True)
        config = Config(version,str(board_path.parent))
        defaults = argparse.ArgumentParser()
        Config.add_options(defaults,version)
        config.set_from_args(defaults.parse_args([]))
        config.kicad_variant = args.variant
        config.open_browser = False
        config.extra_data_file = str(fields_path)
        config.show_fields = ['Value','Footprint','MPN','装配备注']
        config.group_fields = list(config.show_fields)
        config.component_blacklist = blacklist
        config.blacklist_virtual = False  # All exclusions above are explicit and audited.
        config.include_tracks = True
        config.include_nets = True
        config.show_fabrication = True
        config.highlight_pin1 = 'selected'
        config.dark_mode = True
        config.checkboxes = '已备料,已焊接,已检查'
        config.mark_when_checked = '已焊接'
        config.bom_view = 'left-right'
        config.layer_view = 'FB'
        config.compression = False
        config.component_sort_order = ['R','C','D','U','Q','RS','L','Y','X','F','SW','~','J','P','TP','H']
        config.bom_dest_dir = str(staging)
        config.bom_name_format = '焊接助手'
        print('生成正反面、BOM和器件进度表…',flush=True)
        parser = PcbnewParser(str(board_path),config,logger,board=board)
        pcbdata, components = parser.parse()
        if not pcbdata:
            raise UserError('未读取到有效板框，无法生成可用的装配图。')
        pcbdata['bom'] = ibom.generate_bom(components,config)
        pcbdata['ibom_version'] = version
        wanted = {p['ref'] for p in parts if not p['assembly_excluded']}
        exported = [r[0] for entry in pcbdata['bom']['both'] for r in entry]
        if len(exported)!=len(wanted) or set(exported)!=wanted:
            raise UserError('导出BOM器件集合不一致，已停止发布页面。')
        raw_html = Path(ibom.generate_file(str(board_path.parent),board_path.name,pcbdata,config)).read_text(encoding='utf-8')
        meta = dict(project_id=project_id,project_name=board_path.stem,variant=args.variant,
                    pcb_sha256=before[str(board_path)],board_saved_at=datetime.datetime.fromtimestamp(board_path.stat().st_mtime).astimezone().isoformat())
        # Variant assembly checklists are independent even on the same physical board.
        if args.variant:
            meta['project_id'] += '-'+variant_id
        js = 'const ASSEMBLY_META = '+json.dumps(meta,ensure_ascii=False).replace('<','\\u003c')+';\n'
        js += (APP/'assistant.js').read_text(encoding='utf-8')
        raw_html = raw_html.replace('<html lang="en">','<html lang="zh-CN">')
        raw_html = raw_html.replace('<title>Interactive BOM for KiCAD</title>','<title>KiCad 焊接助手</title>')
        raw_html = raw_html.replace('</head>','<style>\n'+(APP/'assistant.css').read_text(encoding='utf-8')+'\n</style>\n</head>')
        raw_html = raw_html.replace('</body>','<script>\n'+js+'\n</script>\n</body>')
        (staging/'焊接助手.html').write_text(raw_html,encoding='utf-8')
        current = {p:digest(p) for p in before}
        if current != before:
            raise UserError('生成期间源工程文件发生变化，未覆盖已有助手。请保存后重新生成。')
        manifest = dict(tool='KiCad通用焊接助手',tool_version='1.0',
            generated_at=datetime.datetime.now().astimezone().isoformat(),board=str(board_path),
            **meta,footprint_count=len(parts),assembly_count=len(wanted),
            bom_groups=len(pcbdata['bom']['both']),parts=parts,
            source_hashes=before,source_hashes_unchanged=True,
            metadata_supplement=supplement,schematic_mismatched_refs=mismatches,
            html='焊接助手.html',html_sha256=digest(staging/'焊接助手.html'),
            upstream='https://github.com/openscopeproject/InteractiveHtmlBom',upstream_version=version)
        (staging/'manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
        for name in ['焊接助手.html','manifest.json','assembly_fields.xml','schematic.net','schematic_export.log']:
            src = staging/name
            if src.is_file():
                os.replace(str(src),str(destination/name))
    html = destination/'焊接助手.html'
    print('完成：{} 个器件，{} 组BOM。'.format(len(wanted),manifest['bom_groups']),flush=True)
    print('HTML='+str(html),flush=True)
    if not args.no_open:
        webbrowser.open(html.as_uri())
    return manifest

def main():
    p = argparse.ArgumentParser(description='拖入KiCad工程生成离线焊接助手；只读源工程。')
    p.add_argument('input',help='工程、PCB、根原理图或工程文件夹')
    outputs = p.add_mutually_exclusive_group()
    outputs.add_argument('--output',help='独立输出目录；默认放在工具的output目录')
    outputs.add_argument('--output-root',help='输出父目录；内部仍按工程身份分别存放')
    p.add_argument('--bom',help='可选CSV物料备注；只采用值/封装一致的行')
    p.add_argument('--variant',default='',help='KiCad装配变体名称')
    p.add_argument('--pcb-only',action='store_true',help='只读取PCB，不导出原理图补充信息')
    p.add_argument('--no-open',action='store_true',help='生成后不启动浏览器')
    p.add_argument('--list-boards',action='store_true',help='只列出输入对应的板文件，输出JSON')
    args = p.parse_args()
    try:
        if args.list_boards:
            print(json.dumps([str(x) for x in board_candidates(args.input)],ensure_ascii=False))
        else:
            build(args)
    except UserError as e:
        print('错误：'+str(e),file=sys.stderr,flush=True)
        return 2
    except Exception as e:
        print('生成失败：'+str(e),file=sys.stderr,flush=True)
        if os.environ.get('SOLDERING_DEBUG'):
            raise
        return 1
    return 0

if __name__=='__main__':
    sys.exit(main())
