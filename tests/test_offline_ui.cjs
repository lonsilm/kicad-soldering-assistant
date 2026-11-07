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
    // Check the redesigned panes at actual desktop viewport sizes. In particular,
    // prevent split-gutter styling from wrapping the entire PCB below the BOM.
    for(const viewport of [{width:960,height:465},{width:1280,height:636},{width:1536,height:794},{width:1024,height:768},{width:1280,height:800},{width:1920,height:1080}]) {
      await page.setViewportSize(viewport);
      for(const [id,layout] of [['lr-btn','left-right'],['tb-btn','top-bottom']]) {
        await page.locator('#'+id).click();
        const panes=await page.evaluate(()=>{
          const b=document.getElementById('bomdiv').getBoundingClientRect(),c=document.getElementById('canvasdiv').getBoundingClientRect();
          const f=document.getElementById('frontcanvas').getBoundingClientRect();
          return {bom:{x:b.x,y:b.y,width:b.width,height:b.height},canvas:{x:c.x,y:c.y,width:c.width,height:c.height},front:{width:f.width,height:f.height},width:innerWidth,height:innerHeight,scroll:document.documentElement.scrollHeight};
        });
        assert(panes.canvas.width>250 && panes.canvas.height>120,JSON.stringify({viewport,layout,panes}));
        assert(panes.front.width>100 && panes.front.height>80,'Board must remain usable');
        assert(panes.canvas.x+panes.canvas.width<=panes.width+1,'Canvas exceeds window width');
        assert(panes.canvas.y+panes.canvas.height<=panes.height+1,'Canvas exceeds window height');
        assert(panes.scroll<=panes.height+1,'Workspace must scroll within panes, not the outer window');
        if(layout==='left-right') assert(Math.abs(panes.bom.y-panes.canvas.y)<2 && panes.canvas.x>panes.bom.x+panes.bom.width-2,'Canvas wrapped below BOM');
        else assert(panes.canvas.y>=panes.bom.y+panes.bom.height-2,'Top/bottom panes overlap');
      }
    }
    await page.setViewportSize({width:1280,height:800});await page.locator('#lr-btn').click();
    const countBeforeTheme=await page.evaluate(()=>getStoredCheckboxRefs('已焊接').size);
    for(const [theme,color] of [['peach','rgb(68, 59, 56)'],['blue','rgb(52, 68, 90)'],['lilac','rgb(75, 61, 89)']]) {
      await page.getByRole('combobox',{name:'界面主题'}).selectOption(theme);
      assert.equal(await page.evaluate(()=>document.getElementById('topmostdiv').dataset.theme),theme);
      assert.equal(await page.evaluate(()=>getComputedStyle(document.getElementById('topmostdiv')).color),color);
      assert.equal(await page.evaluate(()=>settings.darkMode),false);
      const contrasts=await page.evaluate(()=>{
        const style=getComputedStyle(document.documentElement);
        const lum=key=>{const h=style.getPropertyValue(key).trim().slice(1);const c=[0,2,4].map(i=>parseInt(h.slice(i,i+2),16)/255).map(v=>v<=.04045?v/12.92:((v+.055)/1.055)**2.4);return c[0]*.2126+c[1]*.7152+c[2]*.0722;};
        return [['--ui-muted','--ui-raised'],['--ui-accent','--ui-active'],['--ui-text','--ui-panel']].map(([fg,bg])=>(Math.max(lum(fg),lum(bg))+.05)/(Math.min(lum(fg),lum(bg))+.05));
      });
      assert(contrasts.every(r=>r>=4.5),JSON.stringify({theme,contrasts}));
      assert.equal(await page.evaluate(()=>getStoredCheckboxRefs('已焊接').size),countBeforeTheme);
      await page.waitForFunction(()=>getComputedStyle(document.getElementById('filter')).backgroundColor===getComputedStyle(document.getElementById('topmostdiv')).backgroundColor);
      await page.screenshot({path:path.join(results,'theme-'+theme+'-workspace.png')});
      await page.reload();await page.locator('#foc-progress').waitFor();
      assert.equal(await page.getByRole('combobox',{name:'界面主题'}).inputValue(),theme,'Theme must survive restart');
    }
    await page.getByRole('combobox',{name:'界面主题'}).selectOption('peach');
    await page.evaluate(()=>{assemblySetTheme('invalid-theme');setDarkMode(true);});
    assert.equal(await page.evaluate(()=>document.getElementById('topmostdiv').dataset.theme),'peach');
    assert.equal(await page.evaluate(()=>settings.darkMode),false);
    await page.keyboard.press('Alt+r');assert.equal(await page.evaluate(()=>document.activeElement.id),'reflookup');
    await page.screenshot({path:path.join(results,'redesigned-workspace-1280.png')});
    assert.deepEqual(errors,[]);assert.deepEqual(network,[]);
    fs.writeFileSync(path.join(results,'OFFLINE_UI_VALIDATION.json'),JSON.stringify({passed:true,
      assembly_count:refs.length,checks:['actual PCB reference set','reference lookup and highlight','reference-based progress',
      'reload persistence','backup and restore','front/back','1080p effective viewport at 100/125/150/200 percent scale','1024/1280/1920 viewport sizes','left/right and top/bottom pane geometry','three palette contrast, persistence and progress retention','keyboard focus','zero external requests'],errors,network},null,2));
    fs.unlinkSync(backup);console.log('PASS: generated offline HTML, highlight, persistence, backup/restore, front/back, no external requests');
  }finally{await browser.close();}
}
main().catch(e=>{console.error(e);process.exitCode=1;});
