using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("KiCad 焊接助手")]
[assembly: AssemblyDescription("离线 PCB 装配定位与焊接进度助手")]
[assembly: AssemblyCompany("KiCad Soldering Assistant contributors")]
[assembly: AssemblyProduct("KiCad 焊接助手")]
[assembly: AssemblyVersion("1.4.0.0")]
[assembly: AssemblyFileVersion("1.4.0.0")]

namespace KiCadSolderingAssistant
{
    public sealed class RecentProject
    {
        public string Board { get; set; }
        public string Html { get; set; }
        public int Count { get; set; }
        public override string ToString() { return Path.GetFileNameWithoutExtension(Board) + "   ·   " + Count + " 个器件"; }
    }
    public sealed class Preferences
    {
        public string Python { get; set; }
        public string OutputRoot { get; set; }
        public string Theme { get; set; }
        public List<RecentProject> Recent { get; set; }
        public Preferences() { Recent = new List<RecentProject>(); }
    }
    public sealed class ProcessResult
    {
        public int Code;
        public string Output;
        public string Error;
    }
    public sealed class RuntimeInfo
    {
        public string Python;
        public string Version;
    }
    public sealed class GenerationResult
    {
        public string Board;
        public string Html;
        public int Count;
        public int Groups;
        public Dictionary<string, object> Manifest;
    }

    internal static class Core
    {
        internal static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
        internal static string DataRoot;
        internal static string RuntimeRoot;
        internal static string SettingsFile { get { return Path.Combine(DataRoot, "settings.json"); } }
        internal static string Sha(byte[] data)
        {
            using (var sha = SHA256.Create()) { return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant(); }
        }
        static bool RuntimeValid(string root, string hash, Dictionary<string, string> files)
        {
            try
            {
                string marker = Path.Combine(root, ".ready");
                if (!File.Exists(marker) || File.ReadAllText(marker) != hash) return false;
                string prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
                foreach (var file in files)
                {
                    string path = Path.GetFullPath(Path.Combine(root, file.Key.Replace('/', Path.DirectorySeparatorChar)));
                    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path) || Sha(File.ReadAllBytes(path)) != file.Value) return false;
                }
                return true;
            }
            catch { return false; }
        }
        internal static string ExtractRuntime()
        {
            byte[] payload;
            using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("Runtime.zip"))
            using (var buffer = new MemoryStream())
            {
                if (source == null) throw new InvalidOperationException("应用内的运行文件缺失，请重新下载 EXE。");
                source.CopyTo(buffer); payload = buffer.ToArray();
            }
            string hash = Sha(payload);
            Dictionary<string, string> checks;
            using (var memory = new MemoryStream(payload))
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Read))
            using (var reader = new StreamReader(archive.GetEntry("runtime_manifest.json").Open(), Encoding.UTF8))
                checks = Json.Deserialize<Dictionary<string, string>>(reader.ReadToEnd());
            string parent = Path.Combine(DataRoot, "runtime");
            string target = Path.Combine(parent, hash.Substring(0, 16));
            string marker = Path.Combine(target, ".ready");
            if (RuntimeValid(target, hash, checks)) return target;
            Directory.CreateDirectory(parent);
            string staging = Path.Combine(parent, ".extract-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            string prefix = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            using (var stream = new MemoryStream(payload))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries)
                {
                    string path = Path.GetFullPath(Path.Combine(staging, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("运行文件路径异常。");
                    if (String.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(path); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var input = entry.Open()) using (var output = File.Create(path)) { input.CopyTo(output); }
                }
            }
            File.WriteAllText(Path.Combine(staging, ".ready"), hash, Encoding.ASCII);
            if (Directory.Exists(target))
            {
                if (RuntimeValid(target, hash, checks))
                {
                    // Another instance finished extracting this same payload. Keep this private
                    // temporary directory for later cleanup; never remove an unverified path.
                    return target;
                }
                Directory.Move(target, target + ".incomplete-" + Guid.NewGuid().ToString("N"));
            }
            try { Directory.Move(staging, target); }
            catch (IOException)
            {
                if (!RuntimeValid(target, hash, checks)) throw;
            }
            return target;
        }
        internal static Preferences ReadPreferences()
        {
            Preferences p = null;
            try { if (File.Exists(SettingsFile)) p = Json.Deserialize<Preferences>(File.ReadAllText(SettingsFile, Encoding.UTF8)); } catch { }
            if (p == null) p = new Preferences();
            if (p.Recent == null) p.Recent = new List<RecentProject>();
            p.Recent = p.Recent.Where(r => r != null && !String.IsNullOrWhiteSpace(r.Board) && !String.IsNullOrWhiteSpace(r.Html)).Take(8).ToList();
            if (String.IsNullOrWhiteSpace(p.OutputRoot))
                p.OutputRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "KiCad焊接助手", "输出");
            return p;
        }
        internal static void SavePreferences(Preferences prefs)
        {
            Directory.CreateDirectory(DataRoot);
            string temporary = SettingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, Json.Serialize(prefs), new UTF8Encoding(false));
            if (File.Exists(SettingsFile)) File.Replace(temporary, SettingsFile, null);
            else File.Move(temporary, SettingsFile);
        }
        internal static string Quote(string value)
        {
            var b = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { b.Append('\\', slashes * 2 + 1); b.Append('"'); }
                else { b.Append('\\', slashes); b.Append(c); }
                slashes = 0;
            }
            b.Append('\\', slashes * 2); b.Append('"'); return b.ToString();
        }
        internal static async Task<ProcessResult> Run(string executable, string[] arguments, int timeoutMs)
        {
            var info = new ProcessStartInfo(executable, String.Join(" ", arguments.Select(Quote)))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = RuntimeRoot ?? DataRoot
            };
            info.EnvironmentVariables["PYTHONUTF8"] = "1";
            info.EnvironmentVariables["INTERACTIVE_HTML_BOM_CLI_MODE"] = "1";
            info.EnvironmentVariables["INTERACTIVE_HTML_BOM_NO_DISPLAY"] = "1";
            using (var process = new Process { StartInfo = info })
            {
                process.Start();
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                Task exited = Task.Run(() => process.WaitForExit());
                if (timeoutMs > 0 && await Task.WhenAny(exited, Task.Delay(timeoutMs)).ConfigureAwait(false) != exited)
                {
                    try { process.Kill(); } catch { }
                    await exited.ConfigureAwait(false);
                    throw new TimeoutException("KiCad Python 检测超时。");
                }
                await exited.ConfigureAwait(false);
                return new ProcessResult { Code = process.ExitCode, Output = await stdout.ConfigureAwait(false), Error = await stderr.ConfigureAwait(false) };
            }
        }
        internal static async Task<RuntimeInfo> Probe(string python)
        {
            if (String.IsNullOrWhiteSpace(python) || !File.Exists(python)) return null;
            try
            {
                var result = await Run(python, new[] { "-X", "utf8", "-c", "import pcbnew; print(pcbnew.Version())" }, 8000).ConfigureAwait(false);
                if (result.Code == 0 && !String.IsNullOrWhiteSpace(result.Output))
                    return new RuntimeInfo { Python = Path.GetFullPath(python), Version = result.Output.Trim() };
            }
            catch { }
            return null;
        }
        internal static IEnumerable<string> Candidates(string saved)
        {
            var candidates = new List<string>();
            if (!String.IsNullOrWhiteSpace(saved)) candidates.Add(saved);
            string custom = Environment.GetEnvironmentVariable("KICAD_PYTHON");
            if (!String.IsNullOrWhiteSpace(custom)) candidates.Add(custom);
            foreach (string part in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    string directory = part.Trim().Trim('"');
                    if (File.Exists(Path.Combine(directory, "kicad-cli.exe"))) candidates.Add(Path.Combine(directory, "python.exe"));
                }
                catch { }
            }
            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                    using (var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                    {
                        if (uninstall == null) continue;
                        foreach (string name in uninstall.GetSubKeyNames())
                        using (var key = uninstall.OpenSubKey(name))
                        {
                            if (key == null) continue;
                            string label = key.GetValue("DisplayName") as string;
                            string location = key.GetValue("InstallLocation") as string;
                            if (label != null && label.IndexOf("KiCad", StringComparison.OrdinalIgnoreCase) >= 0 && !String.IsNullOrWhiteSpace(location))
                                candidates.Add(Path.Combine(location, "bin", "python.exe"));
                        }
                    }
                }
                catch { }
            }
            foreach (string parent in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") })
            {
                try
                {
                    string kicad = Path.Combine(parent, "KiCad");
                    if (!Directory.Exists(kicad)) continue;
                    candidates.Add(Path.Combine(kicad, "bin", "python.exe"));
                    foreach (string version in Directory.GetDirectories(kicad).OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase))
                        candidates.Add(Path.Combine(version, "bin", "python.exe"));
                }
                catch { }
            }
            foreach (var drive in DriveInfo.GetDrives())
            {
                try { if (drive.IsReady && (drive.DriveType == DriveType.Fixed || drive.DriveType == DriveType.Removable)) candidates.Add(Path.Combine(drive.RootDirectory.FullName, "KiCad", "bin", "python.exe")); }
                catch { }
            }
            return candidates.Distinct(StringComparer.OrdinalIgnoreCase);
        }
        internal static async Task<RuntimeInfo> FindRuntime(string saved)
        {
            foreach (string candidate in Candidates(saved))
            {
                var result = await Probe(candidate).ConfigureAwait(false);
                if (result != null) return result;
            }
            return null;
        }
        internal static List<string> Boards(string input)
        {
            string path = Path.GetFullPath(input);
            if (File.Exists(path))
            {
                string extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension == ".kicad_pcb") return new List<string> { path };
                if (extension != ".kicad_pro" && extension != ".kicad_sch") throw new InvalidOperationException("请选择 KiCad 工程、PCB、根原理图或工程文件夹。");
                string board = Path.ChangeExtension(path, ".kicad_pcb");
                if (File.Exists(board)) return new List<string> { board };
                path = Path.GetDirectoryName(path);
            }
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("找不到这个文件或文件夹。");
            var found = Directory.GetFiles(path, "*.kicad_pcb").Where(p => !Path.GetFileName(p).StartsWith("~")).OrderBy(p => p).ToList();
            if (found.Count == 0) throw new InvalidOperationException("该文件夹顶层没有 PCB 文件。请选择包含实际板子的目录。");
            return found;
        }
        internal static async Task<GenerationResult> Generate(RuntimeInfo runtime, string board, string outputRoot, Action<string> log)
        {
            log("读取已保存的 PCB，生成正反面板图、BOM 和进度页面…");
            var result = await Run(runtime.Python, new[] { "-X", "utf8", Path.Combine(RuntimeRoot, "app", "generate.py"), board, "--output-root", outputRoot, "--no-open" }, 0).ConfigureAwait(false);
            log(result.Output + Environment.NewLine + result.Error);
            if (result.Code != 0) throw new InvalidOperationException("生成未完成，请查看日志。" + Environment.NewLine + result.Error.Trim());
            string html = result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Where(line => line.StartsWith("HTML=", StringComparison.Ordinal)).Select(line => line.Substring(5)).LastOrDefault();
            if (html == null || !File.Exists(html)) throw new InvalidDataException("结果路径无效，请查看日志。");
            var manifest = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(html), "manifest.json"), Encoding.UTF8));
            if (!Convert.ToBoolean(manifest["source_hashes_unchanged"])) throw new InvalidDataException("源工程校验失败，停止打开结果。");
            return new GenerationResult { Board = board, Html = html, Count = Convert.ToInt32(manifest["assembly_count"]), Groups = Convert.ToInt32(manifest["bom_groups"]), Manifest = manifest };
        }
        // Upgrade our generated presentation in place. Board data, metadata and the
        // stable output URL stay intact, so existing assembly progress is retained.
        internal static void UpdatePresentation(string html)
        {
            string original = File.ReadAllText(html, Encoding.UTF8).Replace("\r\n", "\n");
            int headEnd = original.IndexOf("</head>", StringComparison.Ordinal);
            int styleStart = headEnd < 0 ? -1 : original.LastIndexOf("<style>", headEnd, StringComparison.Ordinal);
            int styleEnd = styleStart < 0 ? -1 : original.IndexOf("</style>", styleStart, StringComparison.Ordinal);
            int metaStart = original.LastIndexOf("const ASSEMBLY_META = ", StringComparison.Ordinal);
            int metaEnd = metaStart < 0 ? -1 : original.IndexOf(";\n", metaStart, StringComparison.Ordinal);
            int scriptEnd = original.LastIndexOf("</script>", StringComparison.Ordinal);
            if (styleStart < 0 || styleEnd < styleStart || metaEnd < 0 || scriptEnd < metaEnd) throw new InvalidDataException("这份结果格式较旧，请重新拖入工程生成。");
            string currentStyle = original.Substring(styleStart, styleEnd - styleStart);
            if (!currentStyle.Contains("/* Local presentation only.") && !currentStyle.Contains("/* Assembly workspace skin.")) throw new InvalidDataException("无法识别装配界面，请重新生成工程。");
            string css = File.ReadAllText(Path.Combine(RuntimeRoot, "app", "assistant.css"), Encoding.UTF8).Replace("\r\n", "\n");
            string js = File.ReadAllText(Path.Combine(RuntimeRoot, "app", "assistant.js"), Encoding.UTF8).Replace("\r\n", "\n");
            string updated = original.Substring(0, metaEnd + 2) + js + "\n" + original.Substring(scriptEnd);
            updated = updated.Substring(0, styleStart + 7) + "\n" + css + "\n" + updated.Substring(styleEnd);
            if (updated == original) return;
            string temporary = html + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, updated, new UTF8Encoding(false));
            File.Replace(temporary, html, null);
        }
        internal static void Open(string path) { if (Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("装配结果必须在应用内打开。"); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
    }

    internal sealed class DropCard : Panel
    {
        internal bool Hover;
        internal Palette Colors = Palette.Get(null);
        internal DropCard() { DoubleBuffered = true; BackColor = Colors.Surface; }
        protected override void OnPaintBackground(PaintEventArgs e) { e.Graphics.Clear(Parent == null ? BackColor : Parent.BackColor); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Hover ? Colors.Accent : Colors.Border, Hover ? 2 : 1))
            {
                pen.DashStyle = DashStyle.Dash;
                using (var path = new GraphicsPath())
                {
                    int r = 20, right = Width-2, bottom = Height-2;
                    path.AddArc(1,1,r,r,180,90); path.AddArc(right-r,1,r,r,270,90);
                    path.AddArc(right-r,bottom-r,r,r,0,90); path.AddArc(1,bottom-r,r,r,90,90); path.CloseFigure();
                    using (var brush = new SolidBrush(BackColor)) e.Graphics.FillPath(brush,path);
                    e.Graphics.DrawPath(pen,path);
                }
            }
        }
    }

    internal sealed class AppButton : Button
    {
        internal bool Primary, Active;
        internal Palette Colors = Palette.Get(null);
        bool hover;
        internal AppButton() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? Colors.Background : Parent.BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width-1, Height-1);
            using (var path = new GraphicsPath())
            {
                int r = 14;
                path.AddArc(rect.Left,rect.Top,r,r,180,90); path.AddArc(rect.Right-r,rect.Top,r,r,270,90);
                path.AddArc(rect.Right-r,rect.Bottom-r,r,r,0,90); path.AddArc(rect.Left,rect.Bottom-r,r,r,90,90); path.CloseFigure();
                Color fill = Primary ? Colors.Accent : Active ? Colors.Tint : hover ? Colors.Soft : Colors.Surface;
                if (!Enabled) fill = Colors.Soft;
                using (var brush = new SolidBrush(fill)) e.Graphics.FillPath(brush,path);
                using (var pen = new Pen(Primary ? fill : Colors.Border)) e.Graphics.DrawPath(pen,path);
                Color text = !Enabled ? Colors.Muted : Primary ? Color.White : Active ? Colors.Accent : Colors.Ink;
                TextRenderer.DrawText(e.Graphics,Text,Font,rect,text,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
                if (Focused && ShowFocusCues) { using(var pen = new Pen(Colors.Accent)) e.Graphics.DrawRectangle(pen,3,3,Width-7,Height-7); }
            }
        }
    }

    internal sealed class MainForm : Form
    {
        Palette palette = Palette.Get(null);
        Color Ink { get { return palette.Ink; } }
        Color Muted { get { return palette.Muted; } }
        Color Accent { get { return palette.Accent; } }
        readonly ComboBox themePicker = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "界面主题" };
        Label navigationBrand;
        Panel status;
        bool statusError;
        internal Preferences Preferences;
        internal RuntimeInfo Runtime;
        internal GenerationResult LastResult;
        internal bool Busy;
        internal bool SelfTest;
        readonly Label statusTitle = new Label();
        readonly Label statusDetail = new Label();
        readonly Label runtimeLabel = new Label();
        readonly TextBox outputPath = new TextBox();
        readonly TextBox logs = new TextBox();
        readonly ListBox recent = new ListBox();
        readonly Label recentEmpty = new Label();
        readonly Button chooseFile;
        readonly Button chooseFolder;
        readonly Button pythonButton;
        readonly Button outputButton;
        readonly Button openButton;
        readonly Button folderButton;
        readonly ProgressBar progress = new ProgressBar();
        readonly DropCard drop = new DropCard();
        readonly ToolTip tips = new ToolTip();
        readonly Label dropTitle;
        readonly Panel pages = new Panel();
        readonly Panel home = new Panel { Dock = DockStyle.Fill };
        readonly Panel assembly = new Panel { Dock = DockStyle.Fill };
        Button homeNav, assemblyNav;
        AssemblyWorkspace workspace;
        Panel sidebar, heading, navigation;
        TableLayoutPanel body, shell;
        Label headingTitle, headingSubtitle, formats;
        float uiScale = 1F;
        bool adaptiveReady, reflowing;
        readonly Dictionary<Control, Font> logicalFonts = new Dictionary<Control, Font>();
        int Unit(float value) { return Math.Max(1, (int)Math.Round(value * uiScale)); }

        internal MainForm(Preferences prefs)
        {
            Preferences = prefs;
            palette = Palette.Get(prefs.Theme);
            Text = "KiCad 焊接助手";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = new Font("Microsoft YaHei UI", 13.333F, FontStyle.Regular, GraphicsUnit.Pixel);
            AutoScaleMode = AutoScaleMode.None; // Explicit per-monitor scaling is shared with the layout checks.
            ClientSize = new Size(1100, 760);
            StartPosition = FormStartPosition.CenterScreen; BackColor = palette.Background;
            AllowDrop = true;

            sidebar = new Panel { Dock = DockStyle.Left, Width = 224, BackColor = palette.Soft };
            Controls.Add(sidebar);
            var logo = new PictureBox { Image = Icon.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(26, 32, 50, 50) };
            sidebar.Controls.Add(logo);
            sidebar.Controls.Add(Label("焊接助手", new Rectangle(25, 102, 180, 45), 21, Ink, true));
            sidebar.Controls.Add(Label("每一颗器件\n\n都离完成更近一点", new Rectangle(27, 156, 170, 85), 10, Muted, false));
            string[] steps = { "选择一份工程", "找到要焊的器件", "记下你的进度" };
            for (int i = 0; i < steps.Length; i++) sidebar.Controls.Add(Label(steps[i], new Rectangle(27, 316 + 51*i, 180, 30), 11, i == 0 ? Accent : Muted, i == 0));
            var foot = Label("离线使用  ·  工程只读\nv1.4.0", new Rectangle(27, 657, 180, 64), 9, Muted, false);
            foot.Anchor = AnchorStyles.Left | AnchorStyles.Bottom; sidebar.Controls.Add(foot);

            body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(32, 28, 32, 24), ColumnCount = 1, RowCount = 8 };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 186));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 83));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            Controls.Add(body); body.BringToFront();
            heading = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            headingTitle = Label("开始今天的装配", new Rectangle(0, 0, 540, 40), 23, Ink, true);
            headingSubtitle = Label("打开工程，板图、物料和进度尽在一处。", new Rectangle(2, 47, 620, 28), 10, Muted, false);
            headingTitle.AutoEllipsis = headingSubtitle.AutoEllipsis = true;
            heading.Controls.Add(headingTitle); heading.Controls.Add(headingSubtitle);
            body.Controls.Add(heading, 0, 0);

            drop.Dock = DockStyle.Fill; drop.Margin = new Padding(0, 0, 0, 4); drop.AllowDrop = true;
            dropTitle = Label("把 KiCad 工程拖进来", new Rectangle(30, 24, 580, 38), 17, Ink, true);
            dropTitle.TextAlign = ContentAlignment.MiddleCenter;
            formats = Label("文件夹  ·  .kicad_pro  ·  .kicad_pcb  ·  根原理图", new Rectangle(20, 71, 600, 25), 10, Muted, false);
            formats.TextAlign = ContentAlignment.MiddleCenter;
            drop.Controls.Add(dropTitle); drop.Controls.Add(formats);
            chooseFile = Button("选择文件", true); chooseFile.Size = new Size(124, 37);
            chooseFolder = Button("选择文件夹", false); chooseFolder.Size = new Size(124, 37);
            drop.Controls.Add(chooseFile); drop.Controls.Add(chooseFolder);
            drop.Resize += delegate { LayoutDrop(); };
            chooseFile.Click += async delegate { await PickFile(); };
            chooseFolder.Click += async delegate { using (var dialog = new FolderBrowserDialog { Description = "选择包含 PCB 的工程文件夹" }) if (dialog.ShowDialog(this) == DialogResult.OK) await GenerateInput(dialog.SelectedPath); };
            body.Controls.Add(drop, 0, 1);

            status = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6), BackColor = palette.Tint };
            statusTitle.SetBounds(16, 7, 620, 22); statusTitle.Font = new Font(Font, FontStyle.Bold); statusTitle.ForeColor = Accent;
            statusDetail.SetBounds(16, 31, 620, 22); statusDetail.ForeColor = Muted; statusDetail.AutoEllipsis = true;
            statusTitle.Anchor = statusDetail.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            status.Controls.Add(statusTitle); status.Controls.Add(statusDetail);
            progress.SetBounds(0, 0, 10, 3); progress.Dock = DockStyle.Bottom; progress.Style = ProgressBarStyle.Marquee; progress.MarqueeAnimationSpeed = 26; progress.Visible = false; status.Controls.Add(progress);
            body.Controls.Add(status, 0, 2);

            var outputRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = new Padding(0) };
            outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62)); outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            var outputLabel = Label("输出到", new Rectangle(0, 8, 58, 25), 10, Muted, false); outputLabel.Dock = DockStyle.Fill; outputLabel.TextAlign = ContentAlignment.MiddleLeft;
            outputRow.Controls.Add(outputLabel, 0, 0);
            outputPath.BackColor = palette.Surface; outputPath.ForeColor = Ink; outputPath.BorderStyle = BorderStyle.FixedSingle;
            outputPath.Text = prefs.OutputRoot; outputPath.Dock = DockStyle.Fill; outputPath.Margin = new Padding(0, 8, 8, 0); outputRow.Controls.Add(outputPath, 1, 0);
            outputButton = Button("更改", false); outputButton.Dock = DockStyle.Fill; outputButton.Margin = new Padding(0, 3, 0, 4); outputRow.Controls.Add(outputButton, 2, 0);
            outputButton.Click += delegate { using (var dialog = new FolderBrowserDialog { Description = "选择输出父目录，每个工程会分别建立子目录" }) if (dialog.ShowDialog(this) == DialogResult.OK) { outputPath.Text = dialog.SelectedPath; Preferences.OutputRoot = dialog.SelectedPath; Save(); } };
            body.Controls.Add(outputRow, 0, 3);

            var runtimeRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0) };
            runtimeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); runtimeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 157));
            runtimeLabel.Dock = DockStyle.Fill; runtimeLabel.TextAlign = ContentAlignment.MiddleLeft; runtimeLabel.AutoEllipsis = true; runtimeLabel.ForeColor = Muted; runtimeLabel.Text = "正在寻找本机 KiCad…";
            runtimeRow.Controls.Add(runtimeLabel, 0, 0);
            pythonButton = Button("指定 KiCad Python", false); pythonButton.Dock = DockStyle.Fill; pythonButton.Margin = new Padding(0, 4, 0, 3); runtimeRow.Controls.Add(pythonButton, 1, 0);
            pythonButton.Click += async delegate { await PickRuntime(); };
            body.Controls.Add(runtimeRow, 0, 4);

            var recentHeader = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            recentHeader.Controls.Add(Label("继续上次的装配", new Rectangle(0, 5, 200, 25), 10, Ink, true));
            var toggleLog = new LinkLabel { Text = "显示日志", AutoSize = true, Location = new Point(540, 6), LinkColor = Accent, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            recentHeader.Controls.Add(toggleLog); recentHeader.Resize += delegate { toggleLog.Left = Math.Max(0, recentHeader.Width-toggleLog.Width-Unit(2)); };
            body.Controls.Add(recentHeader, 0, 5);
            var listArea = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 10) };
            recent.Dock = DockStyle.Fill; recent.BorderStyle = BorderStyle.None; recent.BackColor = palette.Surface; recent.ForeColor = Ink; recent.ItemHeight = 29; recent.DrawMode = DrawMode.OwnerDrawFixed;
            recent.DrawItem += delegate(object sender, DrawItemEventArgs e) { if (e.Index < 0) return; using (var brush = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? palette.Tint : palette.Surface)) e.Graphics.FillRectangle(brush,e.Bounds); TextRenderer.DrawText(e.Graphics, recent.Items[e.Index].ToString(), Font, new Rectangle(e.Bounds.X+10,e.Bounds.Y+4,e.Bounds.Width-20,e.Bounds.Height-4), Ink, TextFormatFlags.EndEllipsis); };
            recent.DoubleClick += async delegate { var r = recent.SelectedItem as RecentProject; if (r != null) await OpenAssembly(r.Html); };
            logs.BackColor = palette.Surface; logs.ForeColor = Muted; logs.BorderStyle = BorderStyle.None; logs.Multiline = true; logs.ReadOnly = true; logs.ScrollBars = ScrollBars.Vertical; logs.Dock = DockStyle.Fill; logs.Font = new Font("Consolas", 12, FontStyle.Regular, GraphicsUnit.Pixel); logs.Visible = false;
            listArea.Controls.Add(recent); listArea.Controls.Add(logs);
            recentEmpty.Dock = DockStyle.Fill; recentEmpty.Text = "还没有装配记录\n\n打开第一份工程，就可以从这里继续上次的工作。".Replace("\n", Environment.NewLine);
            recentEmpty.TextAlign = ContentAlignment.MiddleCenter; recentEmpty.ForeColor = Muted; recentEmpty.BackColor = recent.BackColor;
            listArea.Controls.Add(recentEmpty);
            toggleLog.Click += delegate { logs.Visible = !logs.Visible; recent.Visible = !logs.Visible; recentEmpty.Visible = !logs.Visible && recent.Items.Count == 0; toggleLog.Text = logs.Visible ? "最近工程" : "显示日志"; };
            body.Controls.Add(listArea, 0, 6);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
            openButton = Button("进入焊接工作区", true); openButton.Size = new Size(153, 36); openButton.Enabled = false;
            folderButton = Button("打开输出目录", false); folderButton.Size = new Size(153, 36);
            footer.Controls.Add(openButton); footer.Controls.Add(folderButton);
            openButton.Click += async delegate { if (LastResult != null) await OpenAssembly(LastResult.Html); };
            folderButton.Click += delegate { string path = outputPath.Text.Trim(); if (Directory.Exists(path)) OpenSafely(path); else SetStatus("还没有输出目录", "生成第一份工程后即可打开。", false); };
            body.Controls.Add(footer, 0, 7);

            AttachDrop(this); AttachDrop(drop); AttachDrop(dropTitle); AttachDrop(formats);
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (Busy) { e.Cancel = true; SetStatus("工程仍在生成", "请等生成完成后再关闭窗口。", false); } };
            // Keep project selection and assembly in the same native window.
            Controls.Remove(sidebar); Controls.Remove(body);
            home.AutoScroll = true; home.Controls.Add(sidebar); home.Controls.Add(body); body.BringToFront();
            pages.Dock = DockStyle.Fill; pages.Margin = new Padding(0); pages.Controls.Add(home); pages.Controls.Add(assembly);
            navigation = new Panel { Dock = DockStyle.Top, Height = 54, Padding = new Padding(16, 9, 16, 9), BackColor = palette.Soft };
            navigationBrand = Label("KiCad  /  焊接助手", new Rectangle(23, 17, 190, 24), 10, Ink, true);
            navigation.Controls.Add(navigationBrand);
            var navButtons = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 402, FlowDirection = FlowDirection.LeftToRight, BackColor = navigation.BackColor };
            homeNav = Button("工程", false); homeNav.Size = new Size(90, 34);
            assemblyNav = Button("焊接工作区", false); assemblyNav.Size = new Size(128, 34);
            themePicker.Size = new Size(138, 32); themePicker.Margin = new Padding(0, 1, 12, 0);
            themePicker.Items.AddRange(Palette.Names);
            themePicker.SelectedIndex = Array.IndexOf(Palette.Ids, palette.Id);
            themePicker.SelectedIndexChanged += async delegate {
                ApplyTheme(Palette.Ids[themePicker.SelectedIndex], true);
                try { if (workspace != null) await workspace.SetTheme(palette.Id); }
                catch (Exception ex) { AppendLog("主题同步失败：" + ex.Message); }
            };
            navButtons.Controls.Add(themePicker); navButtons.Controls.Add(homeNav); navButtons.Controls.Add(assemblyNav); navigation.Controls.Add(navButtons);
            homeNav.Click += delegate { ShowPage(home); };
            assemblyNav.Click += delegate { if (workspace != null) ShowPage(assembly); else SetStatus("先打开一个工程", "选择工程后即可进入焊接工作区。", false); };
            shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0), Padding = new Padding(0) };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 54)); shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            navigation.Dock = DockStyle.Fill; navigation.Margin = new Padding(0); shell.Controls.Add(navigation, 0, 0); shell.Controls.Add(pages, 0, 1);
            Controls.Add(shell); ShowPage(home);
            Resize += delegate { if (adaptiveReady) Reflow(); };
            home.Resize += delegate { if (adaptiveReady) Reflow(); };
            ResizeEnd += delegate { if (adaptiveReady) FitWindow(Screen.FromHandle(Handle).WorkingArea, false); };
            heading.Resize += delegate { if (adaptiveReady) LayoutHeading(); };
            CaptureFonts(this); adaptiveReady = true; ApplyTheme(palette.Id, false);
            UpdateRecent(); SetStatus("准备就绪", "先保存 PCB，再拖入工程。源文件保持只读。", false);
        }
        void CaptureFonts(Control root)
        {
            logicalFonts[root] = (Font)root.Font.Clone();
            foreach (Control child in root.Controls) CaptureFonts(child);
        }
        internal void ApplyDpi(int dpi)
        {
            float next = Math.Max(0.75F, Math.Min(4F, dpi / 96F));
            if (Math.Abs(next - uiScale) < 0.001F) return;
            SuspendLayout(); reflowing = true; MinimumSize = Size.Empty;
            float ratio = next / uiScale;
            uiScale = next;
            Scale(new SizeF(ratio, ratio));
            foreach (var pair in logicalFonts)
            {
                Font f = pair.Value;
                pair.Key.Font = new Font(f.FontFamily, f.Size * next, f.Style, f.Unit);
            }
            recent.ItemHeight = Unit(29);
            ResumeLayout(true); reflowing = false;
            Reflow();
        }
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
        internal int WindowDpi()
        {
            try { uint dpi = GetDpiForWindow(Handle); if (dpi > 0) return (int)dpi; } catch { }
            using (var graphics = CreateGraphics()) return (int)Math.Round(graphics.DpiX);
        }
        protected override void OnShown(EventArgs e)
        {
            ApplyDpi(WindowDpi());
            FitWindow(Screen.FromHandle(Handle).WorkingArea, true);
            base.OnShown(e);
        }
        internal void FitWindow(Rectangle area, bool initial)
        {
            // WorkingArea already excludes the taskbar. These are physical pixels.
            MinimumSize = new Size(Math.Min(Unit(760), (int)(area.Width * .80)), Math.Min(Unit(480), (int)(area.Height * .75)));
            if (WindowState != FormWindowState.Normal) return;
            int width = initial ? Math.Min(Unit(1100), (int)(area.Width * .92)) : Math.Min(Width, area.Width);
            int height = initial ? Math.Min(Unit(800), (int)(area.Height * .96)) : Math.Min(Height, area.Height);
            int x = initial ? area.Left + (area.Width-width)/2 : Math.Max(area.Left, Math.Min(Left, area.Right-width));
            int y = initial ? area.Top + (area.Height-height)/2 : Math.Max(area.Top, Math.Min(Top, area.Bottom-height));
            Bounds = new Rectangle(x,y,width,height);
            Reflow();
        }
        void LayoutHeading()
        {
            bool compact = home.ClientSize.Height / uiScale < 680;
            headingTitle.SetBounds(0,0,Math.Max(1,heading.Width),Unit(44));
            headingSubtitle.SetBounds(0,Unit(compact ? 45 : 47),Math.Max(1,heading.Width),Unit(22));
        }
        void LayoutDrop()
        {
            if (dropTitle == null || formats == null || chooseFile == null) return;
            int width = drop.ClientSize.Width, height = drop.ClientSize.Height;
            int buttonY = Math.Max(Unit(77), height - Unit(45));
            dropTitle.SetBounds(Unit(12), Unit(12), Math.Max(1,width-Unit(24)), Unit(32));
            formats.SetBounds(Unit(12), Unit(49), Math.Max(1,width-Unit(24)), Unit(22));
            int gap = Unit(10), buttonWidth = Unit(124);
            chooseFile.SetBounds((width-buttonWidth*2-gap)/2, buttonY, buttonWidth, Unit(33));
            chooseFolder.SetBounds(chooseFile.Right+gap, buttonY, buttonWidth, Unit(33));
        }
        internal void Reflow()
        {
            if (!adaptiveReady || reflowing) return;
            reflowing = true;
            try
            {
                navigationBrand.Visible = navigation.ClientSize.Width / uiScale >= 660;
                bool compact = home.ClientSize.Height / uiScale < 680;
                bool showSidebar = home.ClientSize.Width / uiScale >= 980 && !compact;
                sidebar.Visible = showSidebar;
                sidebar.Width = Unit(224);
                int pad = compact ? 16 : 26;
                body.Padding = new Padding(Unit(pad),Unit(compact ? 12 : 20),Unit(pad),Unit(compact ? 12 : 20));
                float[] heights = compact ? new float[] {70,122,68,40,40,28,0,40} : new float[] {74,158,78,44,44,32,0,44};
                for (int i = 0; i < heights.Length; i++)
                    if (i != 6) body.RowStyles[i].Height = Unit(heights[i]);
                int contentHeight = Math.Max(home.ClientSize.Height, Unit(compact ? 572 : 664));
                body.MinimumSize = Size.Empty; body.Dock = DockStyle.None; sidebar.Dock = DockStyle.None;
                home.AutoScrollMinSize = new Size(0, contentHeight);
                int sideWidth = showSidebar ? Unit(224) : 0;
                Point scroll = home.AutoScrollPosition;
                sidebar.SetBounds(scroll.X,scroll.Y,sideWidth,contentHeight);
                body.SetBounds(sideWidth+scroll.X,scroll.Y,Math.Max(1,home.ClientSize.Width-sideWidth),contentHeight);
                shell.RowStyles[0].Height = Unit(54);
                LayoutHeading(); LayoutDrop();
            }
            finally { reflowing = false; }
        }
        [StructLayout(LayoutKind.Sequential)]
        struct DpiRect { internal int Left, Top, Right, Bottom; }
        protected override void WndProc(ref Message message)
        {
            const int WM_DPICHANGED = 0x02E0;
            bool changed = message.Msg == WM_DPICHANGED && adaptiveReady;
            DpiRect suggested = changed ? (DpiRect)Marshal.PtrToStructure(message.LParam, typeof(DpiRect)) : new DpiRect();
            base.WndProc(ref message);
            if (changed)
            {
                int dpi = (int)((long)message.WParam & 0xffff);
                ApplyDpi(dpi);
                Bounds = Rectangle.FromLTRB(suggested.Left,suggested.Top,suggested.Right,suggested.Bottom);
                FitWindow(Screen.FromHandle(Handle).WorkingArea, false);
            }
        }
        internal Dictionary<string, object> CheckLayout(Rectangle area, int dpi, string screenshot, bool compactWindow = false)
        {
            WindowState = FormWindowState.Normal;
            ShowPage(home); home.AutoScrollPosition = Point.Empty; ApplyDpi(dpi); FitWindow(area, true);
            if (compactWindow) Size = new Size(Math.Min(Unit(780),(int)(area.Width*.90)),Math.Min(Unit(520),(int)(area.Height*.90)));
            PerformLayout(); Reflow();
            ShowPage(assembly); Reflow(); ShowPage(home);
            if (sidebar.Visible && body.Left < sidebar.Right) throw new InvalidOperationException("切回工程页后侧栏与内容重叠。");
            foreach (Control control in new[] { shell, pages, home, body, heading, drop, navigation }) control.PerformLayout();
            LayoutDrop(); LayoutHeading();
            var checks = new List<object>();
            if (!themePicker.Parent.ClientRectangle.Contains(themePicker.Bounds)) throw new InvalidOperationException("主题选择器超出工具栏。");
            if (!area.Contains(Bounds)) throw new InvalidOperationException("启动窗口超出可用桌面：" + Bounds + " / " + area);
            foreach (Button button in new[] { chooseFile,chooseFolder,pythonButton,outputButton,openButton,folderButton,homeNav,assemblyNav })
            {
                Size text = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                if (text.Width > button.ClientSize.Width-Unit(6) || text.Height > button.ClientSize.Height)
                    throw new InvalidOperationException("按钮文字被裁切：" + button.Text + " text=" + text + " control=" + button.ClientSize + " dpi=" + dpi);
                Rectangle box = RectangleToClient(button.RectangleToScreen(button.ClientRectangle));
                // On a short desktop, the startup page may scroll. Navigation and
                // file selection must be visible immediately; the rest stay reachable.
                bool required = button == homeNav || button == assemblyNav || button == chooseFile || button == chooseFolder;
                if ((button == homeNav || button == assemblyNav) && !button.Parent.ClientRectangle.Contains(button.Bounds))
                    throw new InvalidOperationException("工具栏内按钮被裁切：" + button.Text + " " + button.Bounds + " parent=" + button.Parent.ClientRectangle + " dpi=" + dpi);
                if (required && !ClientRectangle.Contains(box)) throw new InvalidOperationException("启动操作超出窗口：" + button.Text + " " + box + " dpi=" + dpi + " window=" + Bounds + " client=" + ClientSize + " area=" + area);
                checks.Add(new { text = button.Text, bounds = box.ToString(), text_size = text.ToString(), visible_at_start = ClientRectangle.Contains(box) });
            }
            if (home.VerticalScroll.Visible)
            {
                home.ScrollControlIntoView(openButton);
                Rectangle reachable = RectangleToClient(openButton.RectangleToScreen(openButton.ClientRectangle));
                if (!ClientRectangle.Contains(reachable)) throw new InvalidOperationException("小屏幕下无法滚动到启动页操作按钮。");
                home.AutoScrollPosition = Point.Empty;
            }
            Size title = TextRenderer.MeasureText(headingTitle.Text, headingTitle.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            if (title.Height > headingTitle.Height) throw new InvalidOperationException("启动标题被裁切：dpi=" + dpi + " text=" + title + " bounds=" + headingTitle.Bounds + " font=" + headingTitle.Font);
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0,0,Width,Height)); bitmap.Save(screenshot); }
            return new Dictionary<string, object> { {"dpi",dpi},{"working_area",area.ToString()},{"window",Bounds.ToString()},{"client",ClientSize.ToString()},
                {"sidebar",sidebar.Visible},{"home_scroll",home.VerticalScroll.Visible},{"buttons",checks},{"screenshot",screenshot} };
        }
        internal void ApplyTheme(string id, bool save)
        {
            palette = Palette.Get(id);
            Preferences.Theme = palette.Id;
            PaintTheme(this, palette.Background);
            runtimeLabel.ForeColor = statusDetail.ForeColor = logs.ForeColor = recentEmpty.ForeColor = Muted;
            statusTitle.ForeColor = statusError ? Color.FromArgb(172,61,56) : Accent;
            int index = Array.IndexOf(Palette.Ids, palette.Id);
            if (themePicker.SelectedIndex != index) themePicker.SelectedIndex = index;
            if (save) Save();
            Invalidate(true);
        }
        void PaintTheme(Control control, Color parentBackground)
        {
            // The embedded workspace owns its board canvas; its palette is sent
            // through the explicit bridge, not by walking WebView child windows.
            if (control is AssemblyWorkspace) return;
            Color background = parentBackground;
            if (control == sidebar || control == navigation) background = palette.Soft;
            if (control == drop || control == recent || control == recentEmpty || control == logs || control == outputPath || control == themePicker) background = palette.Surface;
            if (control == status) background = palette.Tint;
            control.BackColor = background;
            string role = control.Tag as string;
            control.ForeColor = role == "muted" ? Muted : role == "accent" ? Accent : Ink;
            var button = control as AppButton;
            if (button != null) button.Colors = palette;
            var card = control as DropCard;
            if (card != null) card.Colors = palette;
            var link = control as LinkLabel;
            if (link != null) { link.LinkColor = Accent; link.ActiveLinkColor = Accent; link.VisitedLinkColor = Accent; }
            foreach (Control child in control.Controls) PaintTheme(child, background);
        }
        Label Label(string text, Rectangle rect, float size, Color color, bool bold)
        {
            return new Label { Text = text, Bounds = rect, Tag = color == Muted ? "muted" : color == Accent ? "accent" : "ink", ForeColor = color, Font = new Font("Microsoft YaHei UI", size * 96F / 72F, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel) };
        }
        Button Button(string text, bool primary)
        {
            return new AppButton { Text = text, Primary = primary, Cursor = Cursors.Hand, Margin = new Padding(0,0,10,0), Font = Font };
        }
        void ShowPage(Panel page)
        {
            home.Visible = page == home; assembly.Visible = page == assembly;
            page.BringToFront();
            if (adaptiveReady) Reflow();
            if (homeNav != null) { ((AppButton)homeNav).Active = page == home; homeNav.Invalidate(); }
            if (assemblyNav != null) { ((AppButton)assemblyNav).Active = page == assembly; assemblyNav.Invalidate(); }
        }
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int dark = 0; DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch { }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var font in logicalFonts.Values) font.Dispose();
            base.Dispose(disposing);
        }
        void AttachDrop(Control control)
        {
            control.AllowDrop = true;
            control.DragEnter += delegate(object sender, DragEventArgs e)
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (!Busy && files != null && files.Length == 1) { e.Effect = DragDropEffects.Copy; drop.Hover = true; drop.Invalidate(); }
            };
            control.DragLeave += delegate { drop.Hover = false; drop.Invalidate(); };
            control.DragDrop += async delegate(object sender, DragEventArgs e)
            {
                drop.Hover = false; drop.Invalidate(); var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length == 1) await GenerateInput(files[0]);
            };
        }
        internal void AppendLog(string value)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action<string>(AppendLog), value); return; }
            logs.AppendText(value + Environment.NewLine);
        }
        void Save() { try { Core.SavePreferences(Preferences); } catch (Exception ex) { AppendLog("无法保存应用偏好：" + ex.Message); } }
        void UpdateRecent() { recent.Items.Clear(); foreach (var item in Preferences.Recent) recent.Items.Add(item); recentEmpty.Visible = recent.Items.Count == 0 && !logs.Visible; }
        internal void SetStatus(string title, string detail, bool error)
        {
            statusError = error; statusTitle.Text = title; statusTitle.ForeColor = error ? Color.FromArgb(172, 61, 56) : Accent; statusDetail.Text = detail; tips.SetToolTip(statusDetail, detail);
        }
        void SetBusy(bool value)
        {
            Busy = value; progress.Visible = value;
            foreach (var b in new[] { chooseFile, chooseFolder, pythonButton, outputButton }) b.Enabled = !value;
            outputPath.Enabled = !value; recent.Enabled = !value;
        }
        internal async Task DetectRuntime()
        {
            Runtime = await Core.FindRuntime(Preferences.Python);
            if (Runtime == null) { runtimeLabel.Text = "需要安装 KiCad（包含自带 Python）"; SetStatus("尚未找到 KiCad", "点击“指定 KiCad Python”，选择安装目录中的 bin\\python.exe。", true); }
            else { Preferences.Python = Runtime.Python; runtimeLabel.Text = "KiCad " + Runtime.Version + "  ·  已就绪"; tips.SetToolTip(runtimeLabel, Runtime.Python); Save(); }
        }
        async Task PickRuntime()
        {
            using (var dialog = new OpenFileDialog { Title = "选择 KiCad 安装目录 bin 下的 python.exe", Filter = "KiCad Python|python.exe" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                SetBusy(true);
                try { var runtime = await Core.Probe(dialog.FileName); if (runtime == null) throw new InvalidOperationException("这个 Python 无法读取 KiCad PCB，请选择 KiCad 自带版本。"); Preferences.Python = runtime.Python; Runtime = runtime; runtimeLabel.Text = "KiCad " + runtime.Version + "  ·  已就绪"; tips.SetToolTip(runtimeLabel, runtime.Python); Save(); SetStatus("KiCad 已就绪", "可以拖入工程。", false); }
                catch (Exception ex) { SetStatus("无法使用这个 Python", ex.Message, true); }
                finally { SetBusy(false); }
            }
        }
        async Task PickFile()
        {
            using (var dialog = new OpenFileDialog { Filter = "KiCad 工程 / PCB / 原理图|*.kicad_pro;*.kicad_pcb;*.kicad_sch", Title = "选择工程或板文件" })
                if (dialog.ShowDialog(this) == DialogResult.OK) await GenerateInput(dialog.FileName);
        }
        string ChooseBoard(List<string> boards)
        {
            if (boards.Count == 1) return boards[0];
            if (SelfTest) throw new InvalidOperationException("目录包含多块板，需要用户选择具体 PCB。");
            using (var picker = new Form { Text = "选择本次要焊接的板子", ClientSize = new Size(620, 320), StartPosition = FormStartPosition.CenterParent, Font = Font, MinimizeBox = false, MaximizeBox = false, Icon = Icon })
            {
                var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
                foreach (string path in boards) list.Items.Add(Path.GetFileName(path)); list.SelectedIndex = 0;
                var ok = Button("生成这块板", true); ok.Dock = DockStyle.Bottom; ok.Height = 42; ok.DialogResult = DialogResult.OK;
                picker.Controls.Add(list); picker.Controls.Add(ok); picker.AcceptButton = ok;
                list.DoubleClick += delegate { picker.DialogResult = DialogResult.OK; picker.Close(); };
                return picker.ShowDialog(this) == DialogResult.OK ? boards[list.SelectedIndex] : null;
            }
        }
        internal async Task GenerateInput(string input)
        {
            if (Busy) { SetStatus("正在处理上一份工程", "完成后可继续拖入下一份。", false); return; }
            SetBusy(true); openButton.Enabled = false; logs.Clear();
            try
            {
                if (Runtime == null) { SetStatus("正在检测 KiCad", "首次启动可能需要几秒钟。", false); await DetectRuntime(); }
                if (Runtime == null) throw new InvalidOperationException("未找到可用的 KiCad Python，请先指定安装目录。生成需要安装 KiCad。");
                string board = ChooseBoard(Core.Boards(input)); if (board == null) return;
                string output = Path.GetFullPath(outputPath.Text.Trim());
                SetStatus("正在生成焊接页面", Path.GetFileName(board), false);
                LastResult = await Core.Generate(Runtime, board, output, AppendLog);
                Preferences.OutputRoot = output;
                Preferences.Recent.RemoveAll(p => String.Equals(p.Board, board, StringComparison.OrdinalIgnoreCase));
                Preferences.Recent.Insert(0, new RecentProject { Board = board, Html = LastResult.Html, Count = LastResult.Count });
                Preferences.Recent = Preferences.Recent.Take(8).ToList(); Save(); UpdateRecent(); openButton.Enabled = true;
                SetStatus("焊接页面已准备好", LastResult.Count + " 个器件 · " + LastResult.Groups + " 组物料 · 源文件校验通过", false);
                await OpenAssembly(LastResult.Html);
            }
            catch (Exception ex) { AppendLog(ex.ToString()); SetStatus("这次没有生成完成", ex.Message.Replace(Environment.NewLine, " "), true); if (SelfTest) throw; }
            finally { SetBusy(false); }
        }
        internal async Task OpenAssembly(string html)
        {
            try
            {
                if (!File.Exists(html)) throw new FileNotFoundException("找不到装配结果，请重新拖入工程生成。", html);
                ShowPage(assembly);
                if (!SelfTest && WindowState != FormWindowState.Maximized) WindowState = FormWindowState.Maximized;
                if (workspace == null) { workspace = new AssemblyWorkspace(); workspace.ThemeChanged += id => ApplyTheme(id, true); workspace.Dock = DockStyle.Fill; assembly.Controls.Add(workspace); }
                await workspace.SetTheme(palette.Id);
                await workspace.Open(html);
                Text = Path.GetFileName(Path.GetDirectoryName(html)) + " · KiCad 焊接助手";
            }
            catch (Exception ex) { AppendLog(ex.ToString()); SetStatus("无法打开焊接工作区", ex.Message, true); if (workspace == null) ShowPage(home); if (SelfTest) throw; }
        }
        internal async Task<Dictionary<string, object>> TestWorkspace()
        {
            foreach (string id in Palette.Ids)
            {
                themePicker.SelectedIndex = Array.IndexOf(Palette.Ids, id);
                await workspace.WaitFor("document.getElementById('topmostdiv').dataset.theme===" + Core.Json.Serialize(id));
                if (Core.ReadPreferences().Theme != id) throw new InvalidOperationException("主题未保存。");
            }
            var result = await workspace.SelfCheck(Path.Combine(Core.DataRoot, "workspace-test"));
            if (Preferences.Theme != "peach" || themePicker.SelectedIndex != 0) throw new InvalidOperationException("工作区主题未同步到启动页。");
            result["native_theme_sync_and_persistence"] = true;
            return result;
        }
        void OpenSafely(string path) { try { Core.Open(path); } catch (Exception ex) { AppendLog(ex.Message); SetStatus("打开文件失败", ex.Message, true); } }
    }

    internal static class Program
    {
        static string Option(string[] args, string name)
        {
            int index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index+1] : null;
        }
        [STAThread]
        static int Main(string[] args)
        {
            bool test = Array.IndexOf(args, "--self-test") >= 0;
            bool layoutTest = Array.IndexOf(args, "--layout-test") >= 0;
            string testRoot = Option(args, "--test-root");
            if ((test || layoutTest) && String.IsNullOrWhiteSpace(testRoot)) return 2;
            Core.DataRoot = (test || layoutTest) ? Path.GetFullPath(testRoot) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KiCadSolderingAssistant");
            string report = Path.Combine(Core.DataRoot, layoutTest ? "LAYOUT_TEST.json" : "SELF_TEST.json");
            try
            {
                Directory.CreateDirectory(Core.DataRoot); Core.RuntimeRoot = Core.ExtractRuntime();
                AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e)
                {
                    string name = new AssemblyName(e.Name).Name;
                    if (name != "Microsoft.Web.WebView2.Core" && name != "Microsoft.Web.WebView2.WinForms") return null;
                    return Assembly.LoadFrom(Path.Combine(Core.RuntimeRoot, "desktop", "webview2", name + ".dll"));
                };
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                var prefs = Core.ReadPreferences();
                if (test) prefs.OutputRoot = Path.Combine(Core.DataRoot, "output");
                using (var form = new MainForm(prefs))
                {
                    int exitCode = 0;
                    form.SelfTest = test;
                    if (test || layoutTest) { form.Opacity = 0; form.ShowInTaskbar = false; }
                    form.Shown += async delegate
                    {
                        try
                        {
                            if (layoutTest)
                            {
                                var layouts = new List<object>();
                                foreach (var area in new[] { new Rectangle(0,0,1920,1040),new Rectangle(0,0,1366,728),new Rectangle(1920,0,2560,1400),new Rectangle(0,0,3840,2080) })
                                foreach (int dpi in new[] {96,120,144,192})
                                {
                                    string name = "home-" + area.Width + "x" + area.Height + "-" + dpi + "dpi.png";
                                    layouts.Add(form.CheckLayout(area,dpi,Path.Combine(Core.DataRoot,name)));
                                }
                                foreach (int dpi in new[] {96,144}) layouts.Add(form.CheckLayout(new Rectangle(0,0,1920,1040),dpi,Path.Combine(Core.DataRoot,"home-resized-"+dpi+"dpi.png"),true));
                                foreach (string theme in Palette.Ids) { form.ApplyTheme(theme, true); form.CheckLayout(new Rectangle(0,0,1920,1040),144,Path.Combine(Core.DataRoot,"theme-"+theme+".png")); if (Core.ReadPreferences().Theme != theme) throw new InvalidOperationException("主题偏好未保存。"); }
                                File.WriteAllText(report, Core.Json.Serialize(new { passed = true, themes = Palette.Ids, layouts = layouts, actual_window_dpi = form.WindowDpi(), scaling = "same explicit per-monitor layout routine used by the application" }), new UTF8Encoding(false));
                                return;
                            }
                            await form.DetectRuntime();
                            if (test)
                            {
                                form.PerformLayout();
                                using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(Path.Combine(Core.DataRoot, "home-preview.png")); }
                                form.ClientSize = new Size(1800, 1100);
                            }
                            string input = test ? Option(args, "--self-test") : args.FirstOrDefault(a => !a.StartsWith("--"));
                            if (!String.IsNullOrWhiteSpace(input)) await form.GenerateInput(input);
                            if (test)
                            {
                                if (form.LastResult == null) throw new InvalidOperationException("未生成结果。");
                                var data = new Dictionary<string, object> { { "passed", true }, { "executable", Application.ExecutablePath }, { "runtime_root", Core.RuntimeRoot }, { "python", form.Runtime.Python }, { "kicad_version", form.Runtime.Version }, { "board", form.LastResult.Board }, { "html", form.LastResult.Html }, { "assembly_count", form.LastResult.Count }, { "bom_groups", form.LastResult.Groups }, { "source_hashes_unchanged", form.LastResult.Manifest["source_hashes_unchanged"] }, { "native_window", true }, { "no_console", true }, { "embedded_runtime", true } };
                                data["workspace"] = await form.TestWorkspace();
                                File.WriteAllText(report, Core.Json.Serialize(data), new UTF8Encoding(false));
                                form.PerformLayout();
                                using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(Path.Combine(Core.DataRoot, "native_preview.png")); }
                            }
                        }
                        catch (Exception ex)
                        {
                            exitCode = 1;
                            if (test || layoutTest) File.WriteAllText(report, Core.Json.Serialize(new { passed = false, error = ex.ToString() }), new UTF8Encoding(false));
                            else form.SetStatus("启动时遇到问题", ex.Message, true);
                        }
                        finally { if (test || layoutTest) { form.Busy = false; form.Close(); } }
                    };
                    Application.Run(form); return exitCode;
                }
            }
            catch (Exception ex)
            {
                if (test || layoutTest) { Directory.CreateDirectory(Core.DataRoot); File.WriteAllText(report, Core.Json.Serialize(new { passed = false, error = ex.ToString() }), new UTF8Encoding(false)); }
                else MessageBox.Show("焊接助手启动失败：\n" + ex.Message, "KiCad 焊接助手", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
