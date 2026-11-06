// Usage: npm install --no-save playwright
// node tests/test_offline_ui.cjs path/to/焊接助手.html
// Optional: PLAYWRIGHT_MODULE=absolute installed module; BROWSER_EXECUTABLE=browser.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const {pathToFileURL}=require('node:url');
const {chromium}=require(process.env.PLAYWRIGHT_MODULE||'playwright');
const html=path.resolve(process.argv[2]||'');
assert(fs.statSync(html).isFile(),'Supply a generated HTML path');
const manifest=JSON.parse(fs.readFileSync(path.join(path.dirname(html),'manifest.json'),'utf8'));
const results=path.resolve(__dirname,'results');fs.mkdirSync(results,{recursive:true});
async function main(){
  const executable=process.env.BROWSER_EXECUTABLE||[
    'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    'C:/Program Files/Microsoft/Edge/Application/msedge.exe'
  ].find(p=>fs.existsSync(p));
  const browser=await chromium.launch({headless:true,...(executable?{executablePath:executable}:{})});
  const errors=[],network=[];
  try{
    const context=await browser.newContext({viewport:{width:1280,height:800},acceptDownloads:true});
    const page=await context.newPage();page.on('pageerror',e=>errors.push(String(e)));
    page.on('request',r=>{if(/^https?:/.test(r.url()))network.push(r.url());});
    await page.goto(pathToFileURL(html).href);await page.locator('#foc-progress').waitFor();
    const refs=manifest.parts.filter(p=>!p.assembly_excluded).map(p=>p.ref).sort();
    assert.deepEqual(await page.evaluate(()=>pcbdata.bom.both.flat().map(r=>r[0]).sort()),refs);
    assert.equal(await page.locator('#bombody tr').count(),manifest.bom_groups);
    assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
    const ref=refs[0];assert(ref,'Test board needs at least one assembly component');
    await page.locator('#reflookup').fill(ref);
    assert((await page.evaluate(()=>highlightedFootprints.map(i=>pcbdata.footprints[i].ref))).includes(ref));
    await page.locator('#bombody input[type=checkbox]').nth(1).check();
    assert.equal(await page.evaluate(()=>readStorage('checkbox_已焊接')),ref);
    await page.reload();await page.locator('#foc-progress').waitFor();
    assert.deepEqual(await page.evaluate(()=>[...getStoredCheckboxRefs('已焊接')].map(i=>pcbdata.footprints[i].ref)),[ref]);
    const downloadEvent=page.waitForEvent('download');await page.locator('#foc-export').click();
    const download=await downloadEvent;const backup=path.join(results,'offline-ui-backup.json');await download.saveAs(backup);
    const state=JSON.parse(fs.readFileSync(backup,'utf8'));
    assert.equal(state.settings.checkboxStoredRefs['已焊接'],ref);
    await page.locator('#reflookup').fill(ref);await page.locator('#bombody input[type=checkbox]').nth(1).uncheck();
    const chooserEvent=page.waitForEvent('filechooser');await page.locator('#foc-import').click();
    await (await chooserEvent).setFiles(backup);await page.waitForFunction(()=>getStoredCheckboxRefs('已焊接').size===1);
    await page.locator('#bl-btn').click();assert.equal(await page.evaluate(()=>settings.canvaslayout),'B');
    await page.locator('#fb-btn').click();await page.getByRole('button',{name:'全部',exact:true}).click();
    assert.deepEqual(errors,[]);assert.deepEqual(network,[]);
    fs.writeFileSync(path.join(results,'OFFLINE_UI_VALIDATION.json'),JSON.stringify({passed:true,
      assembly_count:refs.length,checks:['actual PCB reference set','reference lookup and highlight','reference-based progress',
      'reload persistence','backup and restore','front/back','1280px layout','zero external requests'],errors,network},null,2));
    fs.unlinkSync(backup);console.log('PASS: generated offline HTML, highlight, persistence, backup/restore, front/back, no external requests');
  }finally{await browser.close();}
}
main().catch(e=>{console.error(e);process.exitCode=1;});
