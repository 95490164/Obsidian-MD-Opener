# 构建、测试与使用说明

## 构建与测试

在仓库根目录用 Windows PowerShell 5.1 运行以下命令。构建使用 Windows 的 .NET Framework C# 编译器，无需下载项目依赖。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\test.ps1
```

构建输出在 `bin\ObsidianMdOpener.exe`，测试程序在 `test-output\`。测试用例创建独立的临时目录，并在退出时尝试清理。

## 使用边界

- 文件格式为 `.md`。普通目录直接以文件的父目录作为仓库。目录内容多时，打开器会在本地创建映射文件，优先使用硬链接，失败时尝试符号链接。如果两种链接都建不了，会改用原文件夹。
- 对于轻量仓库，源笔记和可解析的相对附件会按路径映射，保存笔记会影响原文件。删除、移动原文件或由外部程序整体替换文件后，可从原路径重新打开，刷新映射。
- 打开器在目标仓库安装配套侧栏插件。轻量模式下深层文件的打开依赖 Obsidian 桌面版的文件索引行为。若从侧栏打开失败，可以从资源管理器双击原文件。
- 首次登记仓库时，如果 Obsidian 已运行，打开器会尝试其命令行接口，并在默认的用户安装位置查找该程序。如果登记失败，可从 Obsidian 的托盘菜单完全退出，再双击原文件。Obsidian 命令行设置见[官方说明](https://obsidian.md/help/cli)。
- 启用配套插件后，程序会监听已打开仓库的部分设置文件。通过打开器进入仓库时应用中央版本。两边都改了同一份设置并发生冲突时，可以选择保留哪一版。窗口布局和仓库独有的忽略规则各自保留。已打开窗口中的部分设置可能要重新载入仓库后生效。这里的同步限于本机。
- 中央配置和日志在 `%APPDATA%\ObsidianMdOpener`，轻量仓库及配置备份在 `%LOCALAPPDATA%\ObsidianMdOpener`。

## 反馈问题

请在仓库的 Issues 里说明 Windows 和 Obsidian 版本、打开入口（双击、多选或侧栏）、可复现的操作步骤、预期结果和实际结果。优先使用虚构文件名或最小示例，分享日志前请去掉个人路径和笔记内容。
