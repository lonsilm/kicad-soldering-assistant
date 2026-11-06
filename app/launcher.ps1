[CmdletBinding(PositionalBinding=$false)]
param(
    [switch]$Probe,
    [switch]$SelfTest,
    [string]$Python,
    [Parameter(Position=0,ValueFromRemainingArguments=$true)][string[]]$Paths
)
$ErrorActionPreference = 'Stop'
$toolRoot = Split-Path -Parent $PSScriptRoot
$configFile = Join-Path $toolRoot 'settings.json'

# Quote an argument according to CommandLineToArgvW / Python's Windows rules.
function Quote-Argument([string]$Value) {
    '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}
function New-ChildProcess([string]$Executable, [string[]]$Arguments) {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $Executable
    $info.Arguments = ($Arguments | ForEach-Object { Quote-Argument $_ }) -join ' '
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $info.StandardErrorEncoding = New-Object System.Text.UTF8Encoding($false)
    $info.WorkingDirectory = $toolRoot
    $info.EnvironmentVariables['PYTHONUTF8'] = '1'
    $info.EnvironmentVariables['INTERACTIVE_HTML_BOM_CLI_MODE'] = '1'
    $info.EnvironmentVariables['INTERACTIVE_HTML_BOM_NO_DISPLAY'] = '1'
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $info
    return $process
}
function Test-KiCadPython([string]$Candidate) {
    if (-not $Candidate -or -not (Test-Path -LiteralPath $Candidate -PathType Leaf)) { return $null }
    $process = New-ChildProcess $Candidate @('-X','utf8','-c','import pcbnew; print(pcbnew.Version())')
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(8000)) { $process.Kill(); return $null }
        if ($process.ExitCode -eq 0) {
            return [pscustomobject]@{path=(Get-Item -LiteralPath $Candidate).FullName;version=$stdout.Result.Trim()}
        }
    } catch { return $null } finally { $process.Dispose() }
    return $null
}
function Find-KiCadPython {
    $candidates = New-Object 'System.Collections.Generic.List[string]'
    if ($Python) { $candidates.Add($Python) }
    if ($env:KICAD_PYTHON) { $candidates.Add($env:KICAD_PYTHON) }
    if (Test-Path -LiteralPath $configFile) {
        try { $saved = Get-Content -LiteralPath $configFile -Raw -Encoding UTF8 | ConvertFrom-Json
              if ($saved.python) { $candidates.Add([string]$saved.python) } } catch {}
    }
    $cli = Get-Command kicad-cli.exe -ErrorAction SilentlyContinue
    if ($cli) { $candidates.Add((Join-Path (Split-Path $cli.Source) 'python.exe')) }
    foreach ($key in @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
                       'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
                       'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*')) {
        foreach ($entry in @(Get-ItemProperty -Path $key -ErrorAction SilentlyContinue)) {
            if ($entry.DisplayName -like '*KiCad*' -and $entry.InstallLocation) {
                $candidates.Add((Join-Path $entry.InstallLocation 'bin\python.exe'))
            }
        }
    }
    foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, (Join-Path $env:LOCALAPPDATA 'Programs'))) {
        if ($base) {
            foreach ($file in @(Get-ChildItem -Path (Join-Path $base 'KiCad\*\bin\python.exe') -ErrorAction SilentlyContinue | Sort-Object FullName -Descending)) {
                $candidates.Add($file.FullName)
            }
            $candidates.Add((Join-Path $base 'KiCad\bin\python.exe'))
        }
    }
    $candidates.Add('B:\kicad\bin\python.exe')
    foreach ($candidate in @($candidates | Select-Object -Unique)) {
        $found = Test-KiCadPython $candidate
        if ($found) { return $found }
    }
    return $null
}
if ($Probe) {
    $runtime = Find-KiCadPython
    if (-not $runtime) { throw '未找到可用的 KiCad Python；请安装 KiCad 或设置 KICAD_PYTHON。' }
    $result = [ordered]@{python=$runtime.path;version=$runtime.version;paths=@($Paths)}
    if ($Paths.Count -gt 0) {
        $process = New-ChildProcess $runtime.path @('-X','utf8',(Join-Path $PSScriptRoot 'generate.py'),$Paths[0],'--list-boards')
        [void]$process.Start()
        $out = $process.StandardOutput.ReadToEndAsync()
        $err = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) { $process.Kill(); throw '板文件检查超时。' }
        if ($process.ExitCode -ne 0) { throw $err.Result }
        $result.boards = [string[]](ConvertFrom-Json -InputObject ([string]$out.Result))
        $process.Dispose()
    }
    $result | ConvertTo-Json -Depth 4
    exit 0
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()
$form = New-Object System.Windows.Forms.Form
$form.Text = 'KiCad 焊接助手 · 拖入工程即可生成'
$form.Size = New-Object System.Drawing.Size(790,650)
$form.MinimumSize = New-Object System.Drawing.Size(790,650)
$form.StartPosition = 'CenterScreen'
$form.BackColor = [System.Drawing.Color]::FromArgb(244,247,251)
$form.Font = New-Object System.Drawing.Font('Microsoft YaHei UI',10)
$form.AllowDrop = $true

function Add-Label([string]$Text,[int]$X,[int]$Y,[int]$W,[int]$H) {
    $label = New-Object System.Windows.Forms.Label
    $label.Text = $Text
    $label.SetBounds($X,$Y,$W,$H)
    $form.Controls.Add($label)
    return $label
}
function Add-Button([string]$Text,[int]$X,[int]$Y,[int]$W,[scriptblock]$Click) {
    $button = New-Object System.Windows.Forms.Button
    $button.Text = $Text
    $button.SetBounds($X,$Y,$W,36)
    $button.Add_Click($Click)
    $form.Controls.Add($button)
    return $button
}
$heading = Add-Label '把 KiCad 工程拖到这里' 24 20 710 40
$heading.Font = New-Object System.Drawing.Font('Microsoft YaHei UI',19,[System.Drawing.FontStyle]::Bold)
$hint = Add-Label "工程文件夹、.kicad_pro、.kicad_pcb 或根原理图均可。`r`n生成离线板图、BOM与焊接进度；源工程只读。" 26 66 710 54
$drop = New-Object System.Windows.Forms.Label
$drop.Text = "＋  拖放工程到此处`r`n正反面定位 · 料号搜索 · 逐个勾选 · 进度备份"
$drop.TextAlign = 'MiddleCenter'
$drop.BorderStyle = 'FixedSingle'
$drop.BackColor = [System.Drawing.Color]::FromArgb(225,238,254)
$drop.Font = New-Object System.Drawing.Font('Microsoft YaHei UI',13)
$drop.SetBounds(26,128,720,120)
$drop.Anchor = 'Top,Left,Right'
$drop.AllowDrop = $true
$form.Controls.Add($drop)
$select = Add-Button '选择工程 / PCB' 26 260 170 {
    $dialog = New-Object System.Windows.Forms.OpenFileDialog
    $dialog.Filter = 'KiCad 工程/板文件/原理图|*.kicad_pro;*.kicad_pcb;*.kicad_sch'
    if ($dialog.ShowDialog($form) -eq 'OK') { Start-Guide $dialog.FileName }
    $dialog.Dispose()
}
$folder = Add-Button '选择文件夹' 206 260 145 {
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = '选择包含 PCB 的工程文件夹'
    if ($dialog.ShowDialog($form) -eq 'OK') { Start-Guide $dialog.SelectedPath }
    $dialog.Dispose()
}
$open = Add-Button '打开生成结果' 361 260 160 {
    if ($script:lastHtml -and (Test-Path -LiteralPath $script:lastHtml)) { Start-Process -FilePath $script:lastHtml }
}
$open.Enabled = $false
$runtimeButton = Add-Button '指定 KiCad Python' 531 260 215 {
    $dialog = New-Object System.Windows.Forms.OpenFileDialog
    $dialog.Title = '选择 KiCad 安装目录 bin 内的 python.exe'
    $dialog.Filter = 'KiCad Python|python.exe'
    if ($dialog.ShowDialog($form) -eq 'OK') {
        $candidate = Test-KiCadPython $dialog.FileName
        if ($candidate) { Set-Runtime $candidate } else { $status.Text = '此 Python 无法导入 pcbnew，请选择 KiCad 自带版本。' }
    }
    $dialog.Dispose()
}
$outputLabel = Add-Label '输出：' 26 310 58 28
$outputBox = New-Object System.Windows.Forms.TextBox
$outputBox.Text = Join-Path $toolRoot 'output'
$outputBox.SetBounds(85,307,543,29)
$outputBox.Anchor = 'Top,Left,Right'
$form.Controls.Add($outputBox)
$outputButton = Add-Button '更改' 640 303 106 {
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = '选择输出父目录，每个工程会分别建立子目录'
    if ($dialog.ShowDialog($form) -eq 'OK') { $outputBox.Text = $dialog.SelectedPath }
    $dialog.Dispose()
}
$outputButton.Anchor = 'Top,Right'
$runtimeLabel = Add-Label '正在检测 KiCad…' 26 349 720 30
$status = Add-Label '就绪。请先保存 PCB；本工具按磁盘上保存的板子生成。' 26 383 720 50
$status.Anchor = 'Top,Left,Right'
$status.ForeColor = [System.Drawing.Color]::FromArgb(25,79,133)
$log = New-Object System.Windows.Forms.TextBox
$log.Multiline = $true
$log.ReadOnly = $true
$log.ScrollBars = 'Vertical'
$log.SetBounds(26,439,720,146)
$log.Anchor = 'Top,Bottom,Left,Right'
$log.Font = New-Object System.Drawing.Font('Microsoft YaHei UI',9)
$form.Controls.Add($log)
$script:runtime = $null
$script:job = $null
$script:lastHtml = $null
function Set-Runtime($Runtime) {
    $script:runtime = $Runtime
    $runtimeLabel.Text = 'KiCad ' + $Runtime.version + '  |  ' + $Runtime.path
    @{python=$Runtime.path} | ConvertTo-Json | Set-Content -LiteralPath $configFile -Encoding UTF8
    $status.Text = '就绪。拖入工程或点击选择按钮。'
}
function Select-Board([string]$InputPath) {
    $item = Get-Item -LiteralPath $InputPath
    if (-not $item.PSIsContainer) {
        if ($item.Extension -ieq '.kicad_pcb') { return $item.FullName }
        if ($item.Extension -notin @('.kicad_pro','.kicad_sch')) { throw '请选择 KiCad 工程、PCB、根原理图或工程文件夹。' }
        $same = [System.IO.Path]::ChangeExtension($item.FullName,'.kicad_pcb')
        if (Test-Path -LiteralPath $same -PathType Leaf) { return $same }
        $InputPath = $item.DirectoryName
    }
    $boards = @(Get-ChildItem -LiteralPath $InputPath -Filter '*.kicad_pcb' -File | Where-Object { -not $_.Name.StartsWith('~') } | Sort-Object Name)
    if ($boards.Count -eq 0) { throw '这个文件夹没有 PCB 文件；请选择包含实际板子的目录。' }
    if ($boards.Count -eq 1) { return $boards[0].FullName }
    $picker = New-Object System.Windows.Forms.Form
    $picker.Text = '工程中有多块板，请选择本次要焊的板子'
    $picker.Size = New-Object System.Drawing.Size(640,340)
    $picker.StartPosition = 'CenterParent'
    $picker.Font = $form.Font
    $list = New-Object System.Windows.Forms.ListBox
    $list.SetBounds(16,16,590,220)
    $list.Anchor = 'Top,Bottom,Left,Right'
    foreach ($b in $boards) { [void]$list.Items.Add($b.Name) }
    $list.SelectedIndex = 0
    $picker.Controls.Add($list)
    $ok = New-Object System.Windows.Forms.Button
    $ok.Text = '生成此板'
    $ok.SetBounds(480,247,126,34)
    $ok.DialogResult = 'OK'
    $picker.Controls.Add($ok)
    $picker.AcceptButton = $ok
    $list.Add_DoubleClick({ $picker.DialogResult = 'OK'; $picker.Close() })
    $choice = $null
    if ($picker.ShowDialog($form) -eq 'OK') { $choice = $boards[$list.SelectedIndex].FullName }
    $picker.Dispose()
    return $choice
}
function Start-Guide([string]$InputPath) {
    if ($script:job) { $status.Text = '当前工程仍在生成，完成后可继续拖入下一份工程。'; return }
    try {
        if (-not $script:runtime) { throw '没有找到可用的 KiCad Python。请点击“指定 KiCad Python”，选择安装目录 bin\python.exe。' }
        $board = Select-Board $InputPath
        if (-not $board) { return }
        if (-not $outputBox.Text.Trim()) { throw '请先选择输出目录。' }
        $script:lastHtml = $null
        $open.Enabled = $false
        $process = New-ChildProcess $script:runtime.path @('-X','utf8',(Join-Path $PSScriptRoot 'generate.py'),$board,'--output-root',$outputBox.Text,'--no-open')
        [void]$process.Start()
        $script:job = [pscustomobject]@{process=$process;stdout=$process.StandardOutput.ReadToEndAsync();stderr=$process.StandardError.ReadToEndAsync()}
        $status.Text = '正在生成：' + [System.IO.Path]::GetFileName($board)
        $log.Text = '只读解析 PCB 和可选原理图信息，完成后自动打开离线页面…'
        foreach ($b in @($select,$folder,$runtimeButton,$outputButton)) { $b.Enabled = $false }
        $outputBox.Enabled = $false
        $timer.Start()
    } catch { $status.Text = '未生成：' + $_.Exception.Message; $log.Text = $_.ToString() }
}
$dragEnter = {
    param($sender,$eventArgs)
    if ($eventArgs.Data.GetDataPresent([System.Windows.Forms.DataFormats]::FileDrop)) { $eventArgs.Effect = 'Copy' }
}
$dragDrop = {
    param($sender,$eventArgs)
    $files = @($eventArgs.Data.GetData([System.Windows.Forms.DataFormats]::FileDrop))
    if ($files.Count -eq 1) { Start-Guide $files[0] }
    else { $status.Text = '请每次拖入一个工程或一个文件夹；目录内多块板会提示选择。' }
}
foreach ($surface in @($form,$drop)) { $surface.Add_DragEnter($dragEnter); $surface.Add_DragDrop($dragDrop) }
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 300
$timer.Add_Tick({
    if (-not $script:job -or -not $script:job.process.HasExited) { return }
    $timer.Stop()
    $job = $script:job
    $script:job = $null
    try {
        $stdout = $job.stdout.Result
        $stderr = $job.stderr.Result
        $log.Text = $stdout + "`r`n" + $stderr
        if ($job.process.ExitCode -ne 0) { throw '生成未完成，请查看下方日志；原工程没有被写回。' }
        $match = [regex]::Match($stdout,'(?m)^HTML=(.+)\r?$')
        if (-not $match.Success) { throw '没有找到结果路径，请查看日志。' }
        $script:lastHtml = $match.Groups[1].Value.Trim()
        $open.Enabled = $true
        $status.Text = '生成完成，已打开焊接助手。可继续拖入其他工程。'
        if (-not $SelfTest) { Start-Process -FilePath $script:lastHtml }
    } catch { $status.Text = $_.Exception.Message }
    finally {
        $job.process.Dispose()
        foreach ($b in @($select,$folder,$runtimeButton,$outputButton)) { $b.Enabled = $true }
        $outputBox.Enabled = $true
    }
})
$form.Add_Shown({
    $form.Refresh()
    $runtime = Find-KiCadPython
    if ($runtime) { Set-Runtime $runtime }
    else { $runtimeLabel.Text = '未找到 KiCad Python'; $status.Text = '请安装 KiCad，或点击“指定 KiCad Python”选择 bin\python.exe。' }
    if ($Paths.Count -eq 1) { Start-Guide $Paths[0] }
    elseif ($Paths.Count -gt 1) { $status.Text = '请每次拖入一个工程或文件夹。' }
})
$form.Add_FormClosing({
    param($sender,$eventArgs)
    if ($script:job) { $eventArgs.Cancel = $true; $status.Text = '生成仍在进行，请等完成后关闭窗口。' }
})
if ($SelfTest) {
    if ($Paths.Count -ne 1) { throw 'SelfTest 需要一个工程路径。' }
    $runtime = Find-KiCadPython
    if (-not $runtime) { throw 'SelfTest 未找到 KiCad Python。' }
    Set-Runtime $runtime
    Start-Guide $Paths[0]
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while ($script:job -and [DateTime]::UtcNow -lt $deadline) {
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 100
    }
    if ($script:job) { $script:job.process.Kill(); throw 'SelfTest 生成超时。' }
    if (-not $script:lastHtml -or -not $open.Enabled) { throw ($status.Text + "`n" + $log.Text) }
    [ordered]@{passed=$true;html=$script:lastHtml;drag_enabled=$drop.AllowDrop;status=$status.Text} | ConvertTo-Json
} else {
    [void]$form.ShowDialog()
}
$timer.Dispose()
$form.Dispose()
