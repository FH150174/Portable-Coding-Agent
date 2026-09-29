# Portable Coding Agent（Windows x64 U 盘版）

这是以 OpenCode v1.18.32 Windows x64 baseline 构建的便携式 Coding Agent。默认在浏览器打开本机 Web 界面，另有 TUI 入口；模型为 DeepSeek deepseek-flash。Release/Portable-Agent 是复制到 U 盘的目录，随包 Workspace 为空；把自己许可使用的可信项目另行复制到 U 盘的 Workspace。**发布包不含真实 API Key**，必须先在自己的电脑运行一次 配置密钥.cmd。

## 从 GitHub 获取

在本仓库的 Releases 下载 Portable-Agent-v1.18.32.zip，按 RELEASE-SHA256.txt 核对 SHA-256，再把 ZIP 的内容解压到 U 盘。Git 源码仓库不包含 OpenCode 的大型可执行文件、个人项目、运行数据或 API Key；可直接使用的完整程序在 Release ZIP 中。只下载源码不能直接启动，需要在有国际公网的个人电脑执行离线装配命令。
## 最短操作

复制 Release/Portable-Agent 到 U 盘 → 将可信项目复制到 Workspace → 在自己的电脑双击 配置密钥.cmd → 插入机房电脑 → 双击 启动 Agent.cmd → 用 退出 Agent.cmd 结束服务，再安全弹出 U 盘。

## 前提

- Windows x64，已安装 .NET Framework 4.x（CLR 4），普通账户可从 U 盘运行 CMD 和 EXE，并可创建本机回环服务。跨盘符迁移还依赖系统 winsqlite3.dll。日常五个入口调用原生 EXE，不要求学校电脑允许运行 PowerShell 脚本。学校策略若阻止程序或回环服务，启动器会报错，不能绕过学校限制。
- 机房能通过 HTTPS 访问 api.deepseek.com，DeepSeek 账户有可用余额和该模型权限。只需中国境内公网不保证 API 可用；首次在机房要实测。
- U 盘可写且有足够空间；浏览器可访问 http://127.0.0.1:4096。构建和测试项目所需的 Git、JDK、Maven、Python、Node 等由课程项目决定，首版没有打包这些工具。
- 使用前确认项目代码和命令输出允许发送给 DeepSeek 云服务。

## 目录和入口

- 启动 Agent.cmd：默认 Web，项目默认是 Workspace。
- 启动终端界面.cmd：用相同配置和项目打开 TUI。
- 退出 Agent.cmd：停止本套启动器创建的服务。关掉浏览器标签页不会停止服务。
- 查看诊断.cmd：查看不含密钥的启动诊断。
- 配置密钥.cmd：仅在自己的电脑运行，安全输入专用 DeepSeek Key，写入 Config/deepseek.key。
- Agent/opencode.exe：官方 Windows x64 baseline 可执行文件。
- Config/opencode.json：固定版模型与安全配置，启动器核对其 SHA-256；Key 不写入 JSON。手工修改配置后必须在个人电脑重编启动器、重新审查并装配发布包。
- Data、Logs、Workspace：持久数据、诊断和项目。不要在 Agent 会话里要求它读取 Config/deepseek.key。

启动器按自身目录定位文件，不依赖 U 盘盘符。首次启动会记录 U 盘根路径；路径变化时，在启动前备份 OpenCode 数据库并修复 Workspace 会话的项目路径。备份保存在 Data/sessions/opencode 下，名称以 opencode.db.before-relocation- 开头。迁移覆盖会话和项目目录元数据；历史事件和 Git 快照可能仍含旧绝对路径，两台电脑上的真实会话续聊仍需按验收手册实测。OpenCode 的数据、缓存、配置、状态及临时目录通过子进程环境变量指向本目录下的 Data，并禁用自动升级、模型目录抓取、默认插件和 LSP 下载。项目自身的构建工具仍可能写入电脑本地目录。

## Key 管理

请只使用为这只 U 盘单独创建的 Key，按账户可用功能设置消费限额。运行 配置密钥.cmd 时输入不会显示在屏幕上；启动器只在子进程环境中使用 Key，不把它写进命令行参数、日志或 Git。不要把含 Key 的 U 盘目录上传、截图或分享。

无密码即插即用意味着持有 U 盘或控制运行电脑的人能够提取 Key；本地加密无法同时消除该风险并保留无密码启动。丢失 U 盘或怀疑泄露时，立即到 DeepSeek 平台撤销该专用 Key，在自己的电脑创建新 Key 并重新运行 配置密钥.cmd。要弃用旧 Key，也应从平台撤销，单纯删除本地文件不足以撤销。

## 验证与限制

见 验收手册.md 和 Docs/技术记录.md。目前已在本机 Windows x64 验证固定版 EXE 可运行、模型 ID 可解析、主要数据路径可重定向；**尚未在学校机房或两台不同电脑完成实机验收，也未使用真实 Key 调用计费 API**。浏览器缓存、系统日志、杀毒扫描与进程痕迹不能保证留在 U 盘；会话和目录的实际落点需要在目标电脑复核。文件编辑和 Shell 命令默认要求审批；执行前须核对请求的路径与命令。审批规则不能阻止已批准命令间接操作其他文件，也不能视为沙箱隔离。

启动器为保护密钥会递归扫描整个 Workspace。任一项目中的 .opencode、opencode.json、opencode.jsonc、插件目录、目录联接点或超过 50 万个条目的大工作区可能使启动被拒绝（PCA122）；请先在自己的电脑整理成受控且可信的工作区。仅打开可信项目：Web 页面切换到启动器未检查的目录时，固定版 OpenCode 仍可能加载该目录的配置或插件。Web 服务仅绑定 127.0.0.1，但同一电脑上的其他进程仍可能访问其本地 API；离开共用电脑前必须运行退出入口。含 Key 的 U 盘和运行电脑都应视为可读取该明文 Key。

OpenCode 以 MIT 许可证分发，见 Docs/OpenCode-LICENSE.txt。官方发行包和校验值见技术记录。


## 在自己的电脑重新装配

仅在允许运行 PowerShell 脚本的个人电脑、于本项目源目录运行。学校电脑只使用发布包的 CMD 入口。已有经校验的 Agent/opencode.exe 或 Vendor ZIP 时：

    powershell.exe -NoLogo -NoProfile -File .\Scripts\Assemble-Release.ps1 -Zip

GitHub 源码检出没有 OpenCode 二进制时，使用有国际公网的个人电脑下载固定官方包并装配：

    powershell.exe -NoLogo -NoProfile -File .\Scripts\Assemble-Release.ps1 -Download -Zip

脚本从已经校验过的 Vendor 官方 ZIP 或 Agent/opencode.exe 取得固定二进制，输出 Release/Portable-Agent 和同目录的 ZIP，并在 Release-Manifest.json 中列出文件哈希。若两者都缺失，可在有国际公网的个人电脑对一个新的输出目录运行 -Download；脚本只从固定官方 URL 获取并校验大小与 SHA-256。在不联网时也会形成明确标记为 incomplete 的包，不能作为即插即用成品。若输出目录已含文件，脚本拒绝覆盖，以免破坏项目、会话或已配置的 Key。装配后的发布包不携带 Key，也不携带源目录中的 Workspace 项目；复制到 U 盘后放入可信项目，再运行 配置密钥.cmd。

