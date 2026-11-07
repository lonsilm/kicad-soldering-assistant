using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace KiCadSolderingAssistant
{
    // The renderer lives inside our window; no shell navigation to HTML files.
    internal sealed class AssemblyWorkspace : UserControl
    {
        readonly WebView2 view = new WebView2 { Dock = DockStyle.Fill, AllowExternalDrop = false };
        readonly Label message = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.FromArgb(244, 247, 251) };
        readonly Button retry = new Button { Dock = DockStyle.Bottom, Height = 40, Text = "重试打开工作区", Visible = false };
        readonly LinkLabel install = new LinkLabel { Dock = DockStyle.Bottom, Height = 34, TextAlign = ContentAlignment.MiddleCenter, Text = "安装 Microsoft Edge WebView2 运行时", Visible = false };
        string currentHtml, allowedHost, testBackup, testRestore, testDownloadDirectory;
        bool opening;
        internal int BlockedRequests;
        internal string TransferError;
        internal AssemblyWorkspace()
        {
            Controls.Add(view); Controls.Add(message); Controls.Add(retry); Controls.Add(install);
            retry.Click += async delegate { try { await Open(currentHtml); } catch { /* Open keeps the diagnostic visible. */ } };
            install.LinkClicked += delegate { Core.Open("https://developer.microsoft.com/microsoft-edge/webview2/#download-section"); };
        }
        async Task Initialize()
        {
            if (view.CoreWebView2 != null) return;
            CoreWebView2Environment.SetLoaderDllFolderPath(Path.Combine(Core.RuntimeRoot, "desktop", "webview2", Environment.Is64BitProcess ? "x64" : "x86"));
            var options = new CoreWebView2EnvironmentOptions();
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Core.DataRoot, "webview-profile"), options);
            await view.EnsureCoreWebView2Async(environment);
            var core = view.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsZoomControlEnabled = true;
            core.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e) { e.Handled = true; };
            core.PermissionRequested += delegate(object sender, CoreWebView2PermissionRequestedEventArgs e) { e.State = CoreWebView2PermissionState.Deny; };
            core.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e)
            {
                Uri uri;
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out uri) || (uri.Host != allowedHost && uri.Scheme != "blob")) e.Cancel = true;
            };
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += delegate(object sender, CoreWebView2WebResourceRequestedEventArgs e)
            {
                Uri uri;
                if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out uri) && (uri.Scheme == "https" || uri.Scheme == "http") && uri.Host != allowedHost)
                {
                    BlockedRequests++;
                    e.Response = environment.CreateWebResourceResponse(new MemoryStream(new byte[0]), 403, "Offline workspace", "Content-Type: text/plain");
                }
            };
            core.DownloadStarting += Download;
            core.WebMessageReceived += delegate(object sender, CoreWebView2WebMessageReceivedEventArgs e)
            {
                Uri uri;
                if (!Uri.TryCreate(e.Source, UriKind.Absolute, out uri) || uri.Host != allowedHost) return;
                string json = e.WebMessageAsJson;
                string sourceHost = uri.Host;
                // Show native dialogs after returning from the WebView event: nested
                // message loops inside WebView callbacks are unsupported by WebView2.
                BeginInvoke(new Action(async delegate
                {
                    if (IsDisposed || allowedHost != sourceHost) return;
                    try
                    {
                        object data = Core.Json.DeserializeObject(json);
                        string command = data as string;
                        var file = data as Dictionary<string, object>;
                        if (file != null && file.ContainsKey("type") && Convert.ToString(file["type"]) == "assembly.export") Export(file);
                        if (command == "assembly.backup") await Backup();
                        if (command == "assembly.restore") await Restore();
                    }
                    catch (Exception ex)
                    {
                        TransferError = ex.Message;
                        if (testBackup == null && testRestore == null && testDownloadDirectory == null) MessageBox.Show(this, ex.Message, "进度操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }));
            };
            await core.AddScriptToExecuteOnDocumentCreatedAsync("window.assemblyErrors=[]; addEventListener('error',e=>assemblyErrors.push(e.message)); addEventListener('unhandledrejection',e=>assemblyErrors.push(String(e.reason))); ");
        }
        internal async Task Open(string html)
        {
            if (opening) throw new InvalidOperationException("工作区仍在载入，请稍候再打开工程。");
            opening = true; currentHtml = Path.GetFullPath(html);
            message.Text = "正在载入焊接工作区…"; message.Visible = true; message.BringToFront(); retry.Visible = install.Visible = false;
            try
            {
                if (!File.Exists(currentHtml)) throw new FileNotFoundException("装配结果已移动，请重新生成。", currentHtml);
                Core.UpdatePresentation(currentHtml);
                await Initialize();
                if (allowedHost != null) view.CoreWebView2.ClearVirtualHostNameToFolderMapping(allowedHost);
                allowedHost = "project-" + Core.Sha(Encoding.UTF8.GetBytes(currentHtml.ToLowerInvariant())).Substring(0, 24) + ".kicad.invalid";
                view.CoreWebView2.SetVirtualHostNameToFolderMapping(allowedHost, Path.GetDirectoryName(currentHtml), CoreWebView2HostResourceAccessKind.DenyCors);
                var loaded = new TaskCompletionSource<bool>();
                ulong navigationId = 0;
                EventHandler<CoreWebView2NavigationStartingEventArgs> starting = delegate(object sender, CoreWebView2NavigationStartingEventArgs e)
                {
                    Uri target;
                    if (Uri.TryCreate(e.Uri, UriKind.Absolute, out target) && target.Host == allowedHost) navigationId = e.NavigationId;
                };
                EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = delegate(object sender, CoreWebView2NavigationCompletedEventArgs e)
                {
                    if (navigationId == 0 || e.NavigationId != navigationId) return;
                    if (e.IsSuccess) loaded.TrySetResult(true);
                    else loaded.TrySetException(new IOException("工作区载入失败：" + e.WebErrorStatus));
                };
                view.CoreWebView2.NavigationStarting += starting;
                view.CoreWebView2.NavigationCompleted += handler;
                try
                {
                    view.CoreWebView2.Navigate("https://" + allowedHost + "/" + Uri.EscapeDataString(Path.GetFileName(currentHtml)));
                    if (await Task.WhenAny(loaded.Task, Task.Delay(30000)) != loaded.Task) throw new TimeoutException("工作区载入超时，请重试。");
                    await loaded.Task;
                    await WaitFor("!!document.getElementById('foc-progress')");
                    message.Visible = false; view.Visible = true; view.BringToFront();
                }
                finally { view.CoreWebView2.NavigationCompleted -= handler; view.CoreWebView2.NavigationStarting -= starting; }
            }
            catch (Exception ex)
            {
                message.Text = "焊接工作区未能载入\n\n" + ex.Message + "\n\n若本机缺少 WebView2，请先安装运行时，再点击重试。\n工程文件未修改。";
                message.Visible = true; message.BringToFront(); retry.Visible = install.Visible = true;
                throw;
            }
            finally { opening = false; }
        }
        void Download(object sender, CoreWebView2DownloadStartingEventArgs e)
        {
            e.Handled = true;
            var deferral = e.GetDeferral();
            BeginInvoke(new Action(delegate
            {
                try
                {
                    if (IsDisposed) { e.Cancel = true; return; }
                    using (var dialog = new SaveFileDialog { FileName = Path.GetFileName(e.ResultFilePath), Title = "保存导出文件", OverwritePrompt = true })
                    {
                        if (dialog.ShowDialog(this) == DialogResult.OK) e.ResultFilePath = dialog.FileName;
                        else e.Cancel = true;
                    }
                }
                finally { deferral.Complete(); }
            }));
        }
        void Export(Dictionary<string, object> file)
        {
            string content = Convert.ToString(file["data"]);
            if (content.Length > 90 * 1024 * 1024) throw new InvalidDataException("导出文件过大，请缩小板图尺寸。");
            int comma = content.IndexOf(',');
            if (!content.StartsWith("data:", StringComparison.Ordinal) || comma < 0 || !content.Substring(0, comma).EndsWith(";base64", StringComparison.Ordinal)) throw new InvalidDataException("导出数据格式错误。");
            byte[] bytes = Convert.FromBase64String(content.Substring(comma + 1));
            string filename = Path.GetFileName(Convert.ToString(file["filename"]));
            string target;
            if (testDownloadDirectory != null) target = Path.Combine(testDownloadDirectory, filename);
            else
            {
                using (var dialog = new SaveFileDialog { Title = "保存导出文件", FileName = filename, OverwritePrompt = true })
                { if (dialog.ShowDialog(this) != DialogResult.OK) return; target = dialog.FileName; }
            }
            File.WriteAllBytes(target, bytes);
        }
        async Task Backup()
        {
            string content = Core.Json.Deserialize<string>(await view.ExecuteScriptAsync("assemblyProgressBackup()"));
            string target = testBackup;
            if (target == null)
            {
                using (var dialog = new SaveFileDialog { FileName = "焊接进度.settings.json", Filter = "焊接进度 JSON|*.json", OverwritePrompt = true })
                { if (dialog.ShowDialog(this) != DialogResult.OK) return; target = dialog.FileName; }
            }
            File.WriteAllText(target, content, new UTF8Encoding(false));
        }
        async Task Restore()
        {
            string source = testRestore;
            if (source == null)
            {
                using (var dialog = new OpenFileDialog { Title = "恢复焊接进度（兼容旧版浏览器备份）", Filter = "焊接进度 JSON|*.json" })
                { if (dialog.ShowDialog(this) != DialogResult.OK) return; source = dialog.FileName; }
            }
            if (new FileInfo(source).Length > 16 * 1024 * 1024) throw new InvalidDataException("进度文件过大。");
            string result = await view.ExecuteScriptAsync("assemblyRestoreProgress(JSON.parse(" + Core.Json.Serialize(File.ReadAllText(source, Encoding.UTF8)) + "))");
            if (result != "true") throw new InvalidDataException("未恢复进度，请核对备份文件所属工程和格式。");
        }
        internal async Task WaitFor(string expression)
        {
            for (int i = 0; i < 150; i++)
            {
                if (await view.ExecuteScriptAsync("Boolean(" + expression + ")") == "true") return;
                await Task.Delay(100);
            }
            throw new InvalidOperationException("工作区验证未通过：" + expression);
        }
        async Task Check(string expression) { await WaitFor(expression); }
        internal async Task<Dictionary<string, object>> SelfCheck(string directory)
        {
            Directory.CreateDirectory(directory);
            int blocked = BlockedRequests;
            bool prior = await view.ExecuteScriptAsync("getStoredCheckboxRefs('已焊接').size>0") == "true";
            await view.ExecuteScriptAsync("localStorage.clear()");
            await Open(currentHtml);
            await Check("!!document.getElementById('foc-progress') && getStoredCheckboxRefs('已焊接').size===0");
            await view.ExecuteScriptAsync("window.testRef=pcbdata.bom.both[0][0][0];document.getElementById('reflookup').value=testRef;updateRefLookup(testRef)");
            await Check("highlightedFootprints.some(i=>pcbdata.footprints[i].ref===testRef)");
            await view.ExecuteScriptAsync("document.querySelectorAll('#bombody input[type=checkbox]')[1].click()");
            await Check("getStoredCheckboxRefs('已焊接').size===1");
            await Open(currentHtml);
            await Check("getStoredCheckboxRefs('已焊接').size===1");
            testBackup = Path.Combine(directory, "progress.settings.json");
            if (File.Exists(testBackup)) File.Delete(testBackup);
            await view.ExecuteScriptAsync("document.getElementById('foc-export').click()");
            for (int i = 0; !File.Exists(testBackup) && i < 100; i++) await Task.Delay(100);
            if (!File.Exists(testBackup)) throw new IOException("应用内备份按钮未能保存进度：" + TransferError);
            var backup = Core.Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(testBackup));
            if ((string)backup["type"] != "InteractiveHtmlBom settings") throw new InvalidDataException("备份格式错误。");
            await view.ExecuteScriptAsync("writeStorage('checkbox_已焊接','')");
            await Open(currentHtml);
            await Check("!!document.getElementById('foc-progress') && getStoredCheckboxRefs('已焊接').size===0");
            testRestore = testBackup;
            await view.ExecuteScriptAsync("document.getElementById('foc-import').click()");
            await Check("getStoredCheckboxRefs('已焊接').size===1");
            await view.ExecuteScriptAsync("document.getElementById('bl-btn').click()");
            await Check("settings.canvaslayout==='B'");
            await view.ExecuteScriptAsync("document.getElementById('fb-btn').click()");
            await Check("settings.canvaslayout==='FB'");
            testDownloadDirectory = directory;
            string exported = Path.Combine(directory, "workspace-export.txt");
            if (File.Exists(exported)) File.Delete(exported);
            await view.ExecuteScriptAsync("saveFile('workspace-export.txt',new Blob(['native-export-check'],{type:'text/plain'}))");
            for (int i = 0; i < 100; i++)
            {
                try { if (File.Exists(exported) && File.ReadAllText(exported) == "native-export-check") break; } catch (IOException) { }
                await Task.Delay(100);
            }
            if (!File.Exists(exported) || File.ReadAllText(exported) != "native-export-check") throw new IOException("应用内导出下载失败。");
            testDownloadDirectory = null;
            string original = currentHtml;
            string other = Path.Combine(directory, "other-project"); Directory.CreateDirectory(other);
            string otherHtml = Path.Combine(other, "焊接助手.html");
            string current = File.ReadAllText(original, Encoding.UTF8).Replace("\r\n", "\n");
            int skinStart = current.LastIndexOf("<style>", current.IndexOf("</head>"), StringComparison.Ordinal);
            int skinEnd = current.IndexOf("</style>", skinStart, StringComparison.Ordinal);
            string legacy = current.Substring(0, skinStart + 7) + "\n/* Local presentation only. */\n" + current.Substring(skinEnd);
            int metadataEnd = legacy.IndexOf(";\n", legacy.LastIndexOf("const ASSEMBLY_META = ", StringComparison.Ordinal), StringComparison.Ordinal);
            int lastScript = legacy.LastIndexOf("</script>", StringComparison.Ordinal);
            legacy = legacy.Substring(0, metadataEnd + 2) + "throw new Error('Legacy UI was not upgraded');\n" + legacy.Substring(lastScript);
            File.WriteAllText(otherHtml, legacy, new UTF8Encoding(false));
            await Open(otherHtml);
            if (File.ReadAllText(otherHtml, Encoding.UTF8).Replace("\r\n", "\n") != current) throw new InvalidDataException("旧界面升级改变了板图数据或元数据。");
            await view.ExecuteScriptAsync("localStorage.clear()");
            await Open(currentHtml);
            await Check("!!document.getElementById('foc-progress') && getStoredCheckboxRefs('已焊接').size===0");
            await Open(original);
            await Check("getStoredCheckboxRefs('已焊接').size===1");
            await Check("document.documentElement.scrollWidth<=innerWidth && assemblyErrors.length===0");
            // The hidden test window throttles CSS animations. Freeze transitions only
            // for its diagnostic capture, so a half-finished theme fade is not recorded.
            await view.ExecuteScriptAsync("var captureStyle=document.createElement('style');captureStyle.textContent='*,*::before,*::after{transition:none!important;animation:none!important}';document.head.append(captureStyle)");
            string visual = await view.ExecuteScriptAsync("JSON.stringify({width:innerWidth,height:innerHeight,dpr:devicePixelRatio,root:document.getElementById('topmostdiv').className,bg:getComputedStyle(document.getElementById('topmostdiv')).backgroundColor,body:getComputedStyle(document.body).backgroundColor,zoom:visualViewport.scale,forced:matchMedia('(forced-colors: active)').matches,inline:document.getElementById('topmostdiv').getAttribute('style'),css:document.styleSheets[0].cssRules[2].cssText,matches:[...document.styleSheets].flatMap(s=>[...s.cssRules].filter(r=>r.selectorText&&document.getElementById('topmostdiv').matches(r.selectorText)).map(r=>r.cssText))})");
            File.WriteAllText(Path.Combine(directory, "visual-metrics.json"), Core.Json.Deserialize<string>(visual));
            await Task.Delay(300);
            using (var image = File.Create(Path.Combine(directory, "workspace-preview.png")))
                await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
            if (BlockedRequests != blocked) throw new InvalidOperationException("页面意外请求了外部网络。");
            await view.ExecuteScriptAsync("fetch('https://offline-test.invalid/probe').catch(()=>{})");
            for (int i = 0; BlockedRequests == blocked && i < 50; i++) await Task.Delay(100);
            if (BlockedRequests == blocked) throw new InvalidOperationException("外部请求未被离线规则拦截。");
            testBackup = testRestore = null;
            return new Dictionary<string, object> { { "embedded", true }, { "external_browser_opened", false }, { "prior_session_progress", prior },
                { "search_highlight", true }, { "progress_reload", true }, { "native_backup_restore", true }, { "native_file_export", true }, { "front_back", true },
                { "project_isolation", true }, { "cached_ui_upgrade", true }, { "no_script_errors", true }, { "offline_requests_blocked", true }, { "profile", Path.Combine(Core.DataRoot, "webview-profile") } };
        }
        protected override void Dispose(bool disposing) { if (disposing) view.Dispose(); base.Dispose(disposing); }
    }
}
