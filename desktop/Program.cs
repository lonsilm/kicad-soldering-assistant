using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
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
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]

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
        internal static void Open(string path) { if (Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("装配结果必须在应用内打开。"); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
    }

    internal sealed class DropCard : Panel
    {
        internal bool Hover;
        internal DropCard() { DoubleBuffered = true; BackColor = Color.White; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Hover ? Color.FromArgb(24, 150, 118) : Color.FromArgb(190, 204, 219), Hover ? 2 : 1))
            {
                pen.DashStyle = DashStyle.Dash;
                e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
            }
        }
    }

    internal sealed class MainForm : Form
    {
        readonly Color Ink = Color.FromArgb(24, 43, 63);
        readonly Color Muted = Color.FromArgb(105, 122, 139);
        readonly Color Accent = Color.FromArgb(18, 139, 107);
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
        readonly TabControl pages = new TabControl();
        readonly TabPage home = new TabPage("工程");
        readonly TabPage assembly = new TabPage("焊接工作区");
        AssemblyWorkspace workspace;

        internal MainForm(Preferences prefs)
        {
            Preferences = prefs;
            Text = "KiCad 焊接助手";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = new Font("Microsoft YaHei UI", 13.333F, FontStyle.Regular, GraphicsUnit.Pixel);
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1000, 750); MinimumSize = new Size(960, 760);
            StartPosition = FormStartPosition.CenterScreen; BackColor = Color.FromArgb(244, 247, 251);
            AllowDrop = true;

            var sidebar = new Panel { Dock = DockStyle.Left, Width = 220, BackColor = Color.FromArgb(14, 30, 46) };
            Controls.Add(sidebar);
            var logo = new PictureBox { Image = Icon.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(26, 32, 50, 50) };
            sidebar.Controls.Add(logo);
            sidebar.Controls.Add(Label("KiCad\n焊接助手", new Rectangle(24, 100, 180, 90), 22, Color.White, true));
            sidebar.Controls.Add(Label("从工程到装配，\n每一步都看得清。", new Rectangle(27, 204, 170, 58), 11, Color.FromArgb(165, 190, 206), false));
            string[] steps = { "01   拖入工程", "02   定位与取料", "03   记录焊接进度" };
            for (int i = 0; i < steps.Length; i++) sidebar.Controls.Add(Label(steps[i], new Rectangle(27, 316 + 51*i, 180, 30), 11, i == 0 ? Color.FromArgb(112, 227, 191) : Color.FromArgb(187, 204, 217), i == 0));
            var foot = Label("离线使用  ·  工程只读\nv1.2.0", new Rectangle(27, 657, 180, 64), 9, Color.FromArgb(135, 163, 183), false);
            foot.Anchor = AnchorStyles.Left | AnchorStyles.Bottom; sidebar.Controls.Add(foot);

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28, 24, 28, 22), ColumnCount = 1, RowCount = 8 };
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 186));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 83));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            Controls.Add(body); body.BringToFront();
            var heading = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            heading.Controls.Add(Label("准备开始焊接", new Rectangle(0, 0, 540, 40), 23, Ink, true));
            heading.Controls.Add(Label("拖入工程，生成可交互的装配清单与板图。", new Rectangle(2, 47, 620, 28), 10, Muted, false));
            body.Controls.Add(heading, 0, 0);

            drop.Dock = DockStyle.Fill; drop.Margin = new Padding(0, 0, 0, 4); drop.AllowDrop = true;
            dropTitle = Label("拖入 KiCad 工程", new Rectangle(30, 24, 580, 38), 18, Ink, true);
            dropTitle.TextAlign = ContentAlignment.MiddleCenter;
            var formats = Label("文件夹  ·  .kicad_pro  ·  .kicad_pcb  ·  根原理图", new Rectangle(20, 71, 600, 25), 10, Muted, false);
            formats.TextAlign = ContentAlignment.MiddleCenter;
            drop.Controls.Add(dropTitle); drop.Controls.Add(formats);
            chooseFile = Button("选择文件", true); chooseFile.Size = new Size(124, 37);
            chooseFolder = Button("选择文件夹", false); chooseFolder.Size = new Size(124, 37);
            drop.Controls.Add(chooseFile); drop.Controls.Add(chooseFolder);
            drop.Resize += delegate { dropTitle.SetBounds(12, 26, drop.Width-24, 38); formats.SetBounds(12, 72, drop.Width-24, 25); chooseFile.Location = new Point(drop.Width/2-130, 117); chooseFolder.Location = new Point(drop.Width/2+6, 117); };
            chooseFile.Click += async delegate { await PickFile(); };
            chooseFolder.Click += async delegate { using (var dialog = new FolderBrowserDialog { Description = "选择包含 PCB 的工程文件夹" }) if (dialog.ShowDialog(this) == DialogResult.OK) await GenerateInput(dialog.SelectedPath); };
            body.Controls.Add(drop, 0, 1);

            var status = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 9, 0, 8), BackColor = Color.FromArgb(229, 242, 237) };
            statusTitle.SetBounds(16, 10, 620, 23); statusTitle.Font = new Font(Font, FontStyle.Bold); statusTitle.ForeColor = Accent;
            statusDetail.SetBounds(16, 35, 620, 23); statusDetail.ForeColor = Muted; statusDetail.AutoEllipsis = true;
            statusTitle.Anchor = statusDetail.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            status.Controls.Add(statusTitle); status.Controls.Add(statusDetail);
            progress.SetBounds(0, 0, 10, 3); progress.Dock = DockStyle.Bottom; progress.Style = ProgressBarStyle.Marquee; progress.MarqueeAnimationSpeed = 26; progress.Visible = false; status.Controls.Add(progress);
            body.Controls.Add(status, 0, 2);

            var outputRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = new Padding(0) };
            outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62)); outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            var outputLabel = Label("输出到", new Rectangle(0, 8, 58, 25), 10, Muted, false); outputLabel.Dock = DockStyle.Fill; outputLabel.TextAlign = ContentAlignment.MiddleLeft;
            outputRow.Controls.Add(outputLabel, 0, 0);
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
            recentHeader.Controls.Add(Label("最近生成", new Rectangle(0, 5, 200, 25), 10, Ink, true));
            var toggleLog = new LinkLabel { Text = "显示日志", AutoSize = true, Location = new Point(540, 6), LinkColor = Accent, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            recentHeader.Controls.Add(toggleLog); recentHeader.Resize += delegate { toggleLog.Left = recentHeader.Width-toggleLog.Width; };
            body.Controls.Add(recentHeader, 0, 5);
            var listArea = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 10) };
            recent.Dock = DockStyle.Fill; recent.BorderStyle = BorderStyle.None; recent.BackColor = Color.White; recent.ForeColor = Ink; recent.ItemHeight = 29; recent.DrawMode = DrawMode.OwnerDrawFixed;
            recent.DrawItem += delegate(object sender, DrawItemEventArgs e) { if (e.Index < 0) return; e.DrawBackground(); TextRenderer.DrawText(e.Graphics, recent.Items[e.Index].ToString(), Font, new Rectangle(e.Bounds.X+10,e.Bounds.Y+4,e.Bounds.Width-20,e.Bounds.Height-4), (e.State & DrawItemState.Selected) != 0 ? SystemColors.HighlightText : Ink, TextFormatFlags.EndEllipsis); };
            recent.DoubleClick += async delegate { var r = recent.SelectedItem as RecentProject; if (r != null) await OpenAssembly(r.Html); };
            logs.Multiline = true; logs.ReadOnly = true; logs.ScrollBars = ScrollBars.Vertical; logs.Dock = DockStyle.Fill; logs.Font = new Font("Consolas", 12, FontStyle.Regular, GraphicsUnit.Pixel); logs.Visible = false;
            listArea.Controls.Add(recent); listArea.Controls.Add(logs);
            toggleLog.Click += delegate { logs.Visible = !logs.Visible; recent.Visible = !logs.Visible; toggleLog.Text = logs.Visible ? "最近工程" : "显示日志"; };
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
            home.Controls.Add(sidebar); home.Controls.Add(body); body.BringToFront();
            pages.Dock = DockStyle.Fill; pages.TabPages.Add(home); pages.TabPages.Add(assembly);
            Controls.Add(pages);
            UpdateRecent(); SetStatus("准备就绪", "先保存 PCB，再拖入工程。源文件保持只读。", false);
        }
        Label Label(string text, Rectangle rect, float size, Color color, bool bold)
        {
            return new Label { Text = text, Bounds = rect, ForeColor = color, Font = new Font("Microsoft YaHei UI", size * 96F / 72F, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel) };
        }
        Button Button(string text, bool primary)
        {
            var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Color.White, ForeColor = primary ? Color.White : Ink, Cursor = Cursors.Hand, Margin = new Padding(0,0,10,0) };
            b.FlatAppearance.BorderSize = primary ? 0 : 1; b.FlatAppearance.BorderColor = Color.FromArgb(204, 215, 226); return b;
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
        void UpdateRecent() { recent.Items.Clear(); foreach (var item in Preferences.Recent) recent.Items.Add(item); }
        internal void SetStatus(string title, string detail, bool error)
        {
            statusTitle.Text = title; statusTitle.ForeColor = error ? Color.FromArgb(172, 61, 56) : Accent; statusDetail.Text = detail; tips.SetToolTip(statusDetail, detail);
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
                pages.SelectedTab = assembly;
                if (!SelfTest && WindowState != FormWindowState.Maximized) WindowState = FormWindowState.Maximized;
                if (workspace == null) { workspace = new AssemblyWorkspace(); workspace.Dock = DockStyle.Fill; assembly.Controls.Add(workspace); }
                await workspace.Open(html);
                Text = Path.GetFileName(Path.GetDirectoryName(html)) + " · KiCad 焊接助手";
            }
            catch (Exception ex) { AppendLog(ex.ToString()); SetStatus("无法打开焊接工作区", ex.Message, true); if (workspace == null) pages.SelectedTab = home; if (SelfTest) throw; }
        }
        internal Task<Dictionary<string, object>> TestWorkspace()
        {
            return workspace.SelfCheck(Path.Combine(Core.DataRoot, "workspace-test"));
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
            string testRoot = Option(args, "--test-root");
            if (test && String.IsNullOrWhiteSpace(testRoot)) return 2;
            Core.DataRoot = test ? Path.GetFullPath(testRoot) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KiCadSolderingAssistant");
            string report = Path.Combine(Core.DataRoot, "SELF_TEST.json");
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
                    if (test) { form.Opacity = 0; form.ShowInTaskbar = false; form.ClientSize = new Size(1800, 1100); }
                    form.Shown += async delegate
                    {
                        try
                        {
                            await form.DetectRuntime();
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
                            if (test) File.WriteAllText(report, Core.Json.Serialize(new { passed = false, error = ex.ToString() }), new UTF8Encoding(false));
                            else form.SetStatus("启动时遇到问题", ex.Message, true);
                        }
                        finally { if (test) { form.Busy = false; form.Close(); } }
                    };
                    Application.Run(form); return exitCode;
                }
            }
            catch (Exception ex)
            {
                if (test) { Directory.CreateDirectory(Core.DataRoot); File.WriteAllText(report, Core.Json.Serialize(new { passed = false, error = ex.ToString() }), new UTF8Encoding(false)); }
                else MessageBox.Show("焊接助手启动失败：\n" + ex.Message, "KiCad 焊接助手", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
