/* Chinese assembly workflow around the upstream BOM and renderer. No network calls. */
// Stable project identity, and reference-based storage survive footprint reordering.
storagePrefix='KiCad_Soldering__'+ASSEMBLY_META.project_id+'__#';
pcbdata.metadata.assembly_id=ASSEMBLY_META.project_id;
const assemblyIndices=new Set(pcbdata.bom.both.flat().map(r=>r[1]));
const upstreamWriteStorage=writeStorage;
function assemblyRefString(value) {
  return String(value||'').split(',').filter(Boolean).map(ref=>{
    if(/^\d+$/.test(ref) && pcbdata.footprints[Number(ref)]) return pcbdata.footprints[Number(ref)].ref;
    return ref;
  }).join(',');
}
writeStorage=function(key,value) {
  upstreamWriteStorage(key,key.startsWith('checkbox_')?assemblyRefString(value):value);
};
const upstreamGetStoredCheckboxRefs=getStoredCheckboxRefs;
getStoredCheckboxRefs=function(checkbox) {
  return new Set([...upstreamGetStoredCheckboxRefs(checkbox)].filter(i=>assemblyIndices.has(i)));
};
const upstreamSaveSettings=saveSettings;
saveSettings=function() {
  const old=settings.checkboxStoredRefs;
  settings.checkboxStoredRefs=Object.fromEntries(Object.entries(old).map(([k,v])=>[k,assemblyRefString(v)]));
  try {upstreamSaveSettings();} finally {settings.checkboxStoredRefs=old;}
};
// Native EXE and standalone exports use the same portable progress format.
function assemblyProgressBackup() {
  const saved={...settings, checkboxStoredRefs:Object.fromEntries(Object.entries(settings.checkboxStoredRefs).map(([k,v])=>[k,assemblyRefString(v)]))};
  return JSON.stringify({type:'InteractiveHtmlBom settings',version:1,pcbmetadata:pcbdata.metadata,settings:saved},null,2);
}
function assemblyRestoreProgress(data) {
  if(!data || data.type!=='InteractiveHtmlBom settings' || !data.settings || typeof data.settings!=='object' || Array.isArray(data.settings)) return false;
  if(data.pcbmetadata?.assembly_id!==pcbdata.metadata.assembly_id && !confirm('这份进度备份可能属于其他工程，仍要恢复到当前板子吗？')) return false;
  overwriteSettings(data.settings);
  return true;
}
window.addEventListener('load', function () {
  document.title = ASSEMBLY_META.project_name+' · 焊接助手';
  const words = {
    'Value':'值 / 型号','Footprint':'封装','References':'位号','Quantity':'数量',
    'Net':'网络','Net name':'网络名','Dark mode':'深色模式','Full Screen':'全屏',
    'Fab layer':'封装轮廓','Silkscreen':'丝印','Values':'器件值','Pads':'焊盘',
    'Tracks':'走线','Zones':'铜区','DNP outlined':'标出不焊器件',
    'Highlight first pin':'突出1脚','None':'无','All':'全部','Selected':'选中器件',
    'Redraw on drag':'拖动时重绘','Board rotation':'板图旋转','Offset back rotation':'背面旋转180°',
    'Bom checkboxes':'进度勾选列','Mark when checked':'以此列标记已完成',
    'Created using':'基于','Board stats':'板子统计','Front':'正面','Back':'背面','Total':'合计',
    'Components':'器件','Groups':'分组','SMD pads':'贴片焊盘','TH pads':'通孔焊盘',
    'Checkboxes':'焊接进度','Save board image':'导出板图',
    'Transparent background':'透明背景','Config and checkbox state':'配置和焊接进度',
    'Export':'导出','Import':'导入','Reset':'重置','Save bom table as':'导出当前BOM表',
    'Front only':'仅正面','Front and Back':'正反两面','Back only':'仅背面',
    'BOM only':'仅BOM表','BOM left, drawings right':'左BOM / 右板图',
    'BOM top, drawings bot':'上BOM / 下板图','Grouped BOM':'同料分组',
    'Ungrouped BOM':'逐个器件','Netlist':'网络检查','Copy bom table to clipboard':'复制BOM表',
    'Ref lookup':'搜索位号，如 C1 / U4','Filter':'筛选阻值、容值、封装或料号',
    'Width':'宽度','Height':'高度','Mouse and keyboard help':'鼠标和快捷键说明'
  };
  function translate(root) {
    if (!root || ['SCRIPT','STYLE','CANVAS'].includes(root.nodeName)) return;
    if (root.nodeType===Node.TEXT_NODE) {
      const key=root.nodeValue.trim();
      if (words[key]) root.nodeValue=root.nodeValue.replace(key,words[key]);
      return;
    }
    if (root.nodeType!==Node.ELEMENT_NODE) return;
    for (const attr of ['title','placeholder']) {
      const v=root.getAttribute(attr); if (words[v]) root.setAttribute(attr,words[v]);
    }
    for (const child of root.childNodes) translate(child);
  }
  const wrapper=document.getElementById('topmostdiv');
  if (readStorage('assemblyUiDefaultsV1')===null) {
    document.getElementById('valuesCheckbox').checked=false;
    valuesVisible(false);
    settings.hiddenColumns=['MPN','装配备注'];
    writeStorage('hiddenColumns',JSON.stringify(settings.hiddenColumns));
    writeStorage('assemblyUiDefaultsV1','1');
  }
  const total=pcbdata.footprints.length-pcbdata.bom.skipped.length;
  const header=document.createElement('div'); header.className='foc-head';
  header.innerHTML='<div class="foc-head-row"><span class="foc-brand">KiCad 焊接助手</span>'+
    '<span class="foc-tag">离线可用 · '+total+' 个器件</span>'+
    '<span class="foc-progress" id="foc-progress"></span>'+
    '<button id="foc-export" type="button">备份进度</button>'+
    '<button id="foc-import" type="button">恢复进度</button></div>'+
    '<div class="foc-help">点击BOM定位，点击板图反查；滚轮缩放、拖动平移。'+
    '<strong>已焊接</strong>勾选后变绿；同料分组勾选会标记整组，逐个焊接可切换“逐个器件”。'+
    '背面已按翻板视角镜像。进度保存在当前应用，换电脑前请备份。</div>'+
    '<div class="foc-meter"><span id="foc-meter"></span></div>';
  wrapper.prepend(header);
  const native=!!window.chrome?.webview;
  if(native) {
    // Send exports to the app's native Save dialog instead of browser downloads.
    saveFile=function(filename,blob) {
      const reader=new FileReader();
      reader.onload=()=>chrome.webview.postMessage({type:'assembly.export',filename,data:reader.result});
      reader.readAsDataURL(blob);
    };
  } else {
    const help=header.querySelector('.foc-help').lastChild;
    help.textContent=help.textContent.replace('当前应用','当前浏览器');
  }
  document.getElementById('foc-export').onclick=()=>native?chrome.webview.postMessage('assembly.backup'):saveSettings();
  document.getElementById('foc-import').onclick=()=>native?chrome.webview.postMessage('assembly.restore'):loadSettings();
  const front=document.getElementById('fl-btn'), back=document.getElementById('bl-btn'), both=document.getElementById('fb-btn');
  front.textContent='正面';back.textContent='背面';both.textContent='双面';
  for (const b of [front,back,both]) b.style.width='50px';
  function progress() {
    const soldered=getStoredCheckboxRefs('已焊接').size;
    const checked=getStoredCheckboxRefs('已检查').size;
    document.getElementById('foc-progress').textContent='已焊 '+soldered+' / '+total+'　已检查 '+checked+' / '+total;
    document.getElementById('foc-meter').style.width=(100*soldered/Math.max(total,1))+'%';
  }
  // Small shortcuts use the same native filters and highlighting as the BOM.
  const toolbar=document.createElement('div');toolbar.className='foc-filterbar';
  let category='all';
  const originalGetSelectedBomList=getSelectedBomList;
  getSelectedBomList=function () {
    const entries=originalGetSelectedBomList();
    if(category==='all'||settings.bommode==='netlist') return entries;
    return entries.map(e=>e.filter(r=>(r[0].match(/^[A-Z]+/)||[''])[0]===category)).filter(e=>e.length);
  };
  const originalEntryMatches=entryMatches;
  entryMatches=function(entry) {
    if(originalEntryMatches(entry)||settings.bommode==='netlist') return originalEntryMatches(entry);
    return entry.some(r=>pcbdata.bom.fields[r[1]].some(f=>String(f).toLowerCase().includes(filter)));
  };
  const controls=[['全部','all'],['电阻','R'],['电容','C'],['芯片','U'],['MOS管','Q'],['逐个器件','individual']];
  for (const [label,key] of controls) {
    const b=document.createElement('button');b.type='button';b.textContent=label;
    b.onclick=()=>{
      document.getElementById('reflookup').value='';updateRefLookup('');
      document.getElementById('filter').value='';updateFilter('');
      category=['all','individual'].includes(key)?'all':key;
      if(key==='individual') changeBomMode('ungrouped');
      else {
        changeBomMode('grouped');
      }
      toolbar.querySelectorAll('button').forEach(x=>x.classList.toggle('foc-active',x===b));
    };
    toolbar.appendChild(b);
  }
  toolbar.firstChild.classList.add('foc-active');
  document.getElementById('bomdiv').prepend(toolbar);
  const detail=document.createElement('div');detail.className='foc-detail';detail.id='foc-detail';
  detail.textContent='选择一个器件或BOM行，查看料号与装配备注。';
  toolbar.after(detail);
  EventHandler.registerCallback(IBOM_EVENT_TYPES.HIGHLIGHT_EVENT,event=>{
    const refs=event.args.refs;
    detail.replaceChildren();
    if(!refs||!refs.length) {detail.textContent=event.args.net||'选择一个器件查看详情';return;}
    const fs=pcbdata.bom.fields[refs[0][1]];
    const title=document.createElement('strong');title.textContent=refs.map(r=>r[0]).join(', ')+' · '+fs[0];detail.appendChild(title);
    const mpn=document.createElement('div');mpn.textContent='料号：'+(fs[2]||'未填写，按值 / 封装核对');detail.appendChild(mpn);
    if(fs[3]) {const note=document.createElement('div');note.textContent=fs[3];detail.appendChild(note);}
  });
  for(const input of [document.getElementById('reflookup'),document.getElementById('filter')]) {
    input.addEventListener('input',()=>{
      if(category!=='all') {
        category='all';toolbar.querySelectorAll('button').forEach((b,i)=>b.classList.toggle('foc-active',i===0));populateBomTable();
      }
    });
  }
  translate(document.body);
  const observer=new MutationObserver(records=>{
    for(const record of records) {
      if(record.type==='characterData') translate(record.target);
      else record.addedNodes.forEach(translate);
    }
  });
  observer.observe(document.body,{childList:true,subtree:true,characterData:true});
  EventHandler.registerCallback(IBOM_EVENT_TYPES.CHECKBOX_CHANGE_EVENT,progress);
  EventHandler.registerCallback(IBOM_EVENT_TYPES.BOM_BODY_CHANGE_EVENT,progress);
  progress();
  populateBomTable();
  resizeAll();
});
