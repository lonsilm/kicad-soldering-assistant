# KiCad 焊接助手

一个可拖入 KiCad 工程的 Windows 桌面应用，在应用窗口内查看交互式装配板图、BOM 和焊接进度。

## 下载与使用

下载仓库 `dist/` 目录中的 **[KiCad焊接助手.exe](dist/KiCad焊接助手.exe)**，保存到电脑后双击运行。只需复制这一个 EXE，不需要安装本工具或另装 Python。

1. 保存 KiCad PCB，将工程文件夹、`.kicad_pro`、`.kicad_pcb` 或根 `.kicad_sch` 拖入窗口。
2. 生成完成后，直接进入同一个 EXE 窗口的“焊接工作区”，不跳转到外部浏览器。目录内多块板时先选择目标 PCB。
3. 点击 BOM 定位器件，点击板图反查位号，记录“已备料 / 已焊接 / 已检查”。

**系统要求：Windows 10/11、.NET Framework 4.8、Microsoft Edge WebView2 Runtime，以及已安装的 KiCad（含其自带 Python）。** 已用 KiCad 10.0.6 验证；不承诺所有旧版本格式。工作区使用 WebView2 内嵌渲染；多数 Windows 电脑已安装该运行时，缺少时应用会显示安装入口。生成好的 HTML 仍可作为可选离线分享文件，无需 KiCad。

## 功能

- 原生桌面窗口、自定义图标、拖放工程、文件/文件夹选择、最近生成记录；双击记录在应用内继续焊接。
- 自动检测 KiCad；也可指定 `bin/python.exe` 或设置 `KICAD_PYTHON` 环境变量。
- 正反面板图、器件分组、位号/参数/料号搜索、进度备份和恢复。
- 不同工程独立记录进度，重新生成及重启应用后按位号保留；备份和恢复使用原生文件对话框。
- 默认输出到“文档 / KiCad焊接助手 / 输出”，可在窗口更改。
- 工程严格只读：不保存 PCB、不更新封装、不移动器件、不改布线、不重铺铜；生成前后校验源文件 SHA256。

器件位置、值、封装及不焊状态以磁盘上已保存的 PCB 为准。原理图仅补充匹配物料信息。DNP、不计入 BOM 的器件、机械孔及无编号机械焊盘不列入待焊计数。详细规则见 [使用说明](使用说明.md)。

应用偏好、最近记录和内置运行代码位于 `%LOCALAPPDATA%\KiCadSolderingAssistant`。应用内进度保存在该目录的 `webview-profile` 中，与系统浏览器分开；旧版浏览器进度请先备份，再在应用内恢复。每次启动检查内置运行代码的完整性，缺失或变化时从 EXE 重新提取。项目与焊接进度不包含在 EXE 中，也不上传网络。

## 从源码构建

使用 Windows 自带 .NET Framework C# 编译器，无需 NuGet、PyInstaller 或额外前端构建工具：

```powershell
python desktop/build_exe.py
```

生成 `dist/KiCad焊接助手.exe`。构建脚本使用 Python 标准库和 Windows PowerShell；首次构建从官方 NuGet 下载固定版本 WebView2 SDK，并校验锁定的 SHA256；运行生成器时使用 KiCad 自带 Python。`app/launcher.ps1` 和根目录 CMD 仍作为源码版启动方式保留。

## 验证

安装 KiCad 后，使用其自带 Python：

```powershell
& 'C:\Program Files\KiCad\10.0\bin\python.exe' -X utf8 tests/test_backend.py
& 'C:\Program Files\KiCad\10.0\bin\python.exe' -X utf8 tests/test_desktop.py
```

测试仅在 `tests/fixtures` 写入明确新建的合成板，验证不焊件排除、特殊字符路径、多板选择、同名工程隔离，以及单 EXE 复制运行、损坏缓存恢复。可选设置 `KICAD_TEST_BOARD` 对本机真实板执行只读生成测试；该文件不会加入仓库。

`tests/test_offline_ui.cjs` 可在安装 Playwright 后运行，验证搜索、定位、进度保存及离线页面。参见该文件头部的使用说明。

## 第三方组件

板图和 BOM 基于 [InteractiveHtmlBom](https://github.com/openscopeproject/InteractiveHtmlBom) v2.12.0（MIT）。完整上游许可与源码保留在 `vendor/InteractiveHtmlBom/`，也包含在 EXE 的内置运行文件中。内嵌工作区使用 [Microsoft WebView2 SDK](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.2903.40)，重分发许可见 [WebView2-LICENSE](docs/WebView2-LICENSE.txt)，EXE 内也包含该许可。桌面应用和附加逻辑采用 [MIT 许可证](LICENSE)。
