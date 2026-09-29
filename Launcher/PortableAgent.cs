using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

internal sealed class AgentError : Exception
{
    internal AgentError(string code, string message) : base(code + ": " + message) {}
}
internal sealed class ServerState
{
    public int pid { get; set; }
    public long startTicksUtc { get; set; }
    public string exe { get; set; }
    public int port { get; set; }
    public string project { get; set; }
    public string approvalMode { get; set; }
}
internal sealed class ModelsResponse
{
    public ModelEntry[] data { get; set; }
}
internal sealed class ModelEntry
{
    public string id { get; set; }
}
internal sealed class HealthResponse
{
    public bool healthy { get; set; }
    public string version { get; set; }
}

internal static class PortableAgent
{
    private const string PinnedVersion = "1.18.32";
    private const string PinnedHash = "DA86EED515D91A7B2D7DA9A8230A2BD095F68A89F0CF44EB6A9217BEAD81FFFC";
    private const string PinnedConfigHash = "57B2B36833676AF90ADD9CA2875196177F4DA38D336570825862374B12265472";
    private const string PinnedReadonlyConfigHash = "28A70F06EA4AC04C1542C75C67F60F52C9368D38301183EF3A0363BDDB1DB8A1";
    private const string PinnedWorkspaceConfigHash = "3A32DB90F754FFE764BE0AEA3FBDB44F78CD41BC74157A64965B68891AABD0EC";
    private const long MinimumFreeBytes = 128L * 1024L * 1024L;
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
    private static readonly string Exe = Path.Combine(Root, "Agent", "opencode.exe");
    private static readonly string Config = Path.Combine(Root, "Config", "opencode.json");
    private static readonly string ReadonlyConfig = Path.Combine(Root, "Config", "approval-readonly.json");
    private static readonly string WorkspaceConfig = Path.Combine(Root, "Config", "approval-workspace.json");
    private static readonly string KeyFile = Path.Combine(Root, "Config", "deepseek.key");
    private static readonly string Workspace = Path.Combine(Root, "Workspace");
    private static readonly string Data = Path.Combine(Root, "Data");
    private static readonly string StateFile = Path.Combine(Data, "run", "web-server.json");
    private static readonly string ApprovalFile = Path.Combine(Data, "run", "approval-mode.txt");
    private static readonly string ApprovalLockFile = Path.Combine(Data, "run", "approval.lock");
    private static readonly string LogFile = Path.Combine(Root, "Logs", "launcher.log");
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static string ApprovalMode = "manual";
    private static string ConfigText;
    private static string Key;

    [DllImport("kernel32.dll")]
    private static extern void GetNativeSystemInfo(IntPtr systemInfo);
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int addressFamily, int tableClass, uint reserved);

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch {}
        try
        {
            string mode = args.Length == 0 ? "web" : args[0].ToLowerInvariant();
            string projectArg = null;
            string apiBaseUrl = "https://api.deepseek.com";
            bool apiOverride = false;
            string approvalLevel = null;
            bool approvalShow = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--project" && i + 1 < args.Length) projectArg = args[++i];
                else if (args[i] == "--api-base-url" && i + 1 < args.Length) { apiBaseUrl = args[++i]; apiOverride = true; }
                else if (args[i] == "--show") approvalShow = true;
                else if (args[i] == "--level" && i + 1 < args.Length) approvalLevel = args[++i].ToLowerInvariant();
                else throw new AgentError("PCA100", "参数无效。用法：PortableAgent.exe web|tui|stop|diagnose|key|verify|preflight|approval [--project 目录] [--level 档位]");
            }
            if (mode == "approval")
            {
                if (projectArg != null || apiOverride || (approvalLevel != null && approvalShow))
                    throw new AgentError("PCA100", "审批设置参数无效。");
                ConfigureApproval(approvalLevel, approvalShow);
                return 0;
            }
            if (approvalLevel != null || approvalShow)
                throw new AgentError("PCA100", "--level 和 --show 只供 approval 使用。");
            if (mode == "key") { ConfigureKey(); return 0; }
            if (mode == "migrate") { CheckPlatform(); CheckStorage(); SessionRelocator.Prepare(Root, Data, Log); Console.WriteLine("会话路径检查完成。"); return 0; }
            if (mode == "verify") { VerifyPackage(); return 0; }
            if (mode == "diagnose") { Diagnose(); return 0; }
            if (mode == "stop") { StopWeb(); return 0; }
            if (mode != "web" && mode != "tui" && mode != "preflight")
                throw new AgentError("PCA100", "未知模式；可用 web、tui、stop、diagnose、key、verify、preflight、approval。");
            if (apiOverride && mode != "preflight")
                throw new AgentError("PCA108", "自定义 API 检查地址仅供隔离的 preflight 测试使用。");
            CheckPlatform();
            CheckExecutable();
            CheckStorage();
            ApprovalMode = ReadApprovalMode();
            CheckConfig();
            string project = GetProject(projectArg);
            CheckProjectConfiguration(project);
            ReadKey();
            CheckApi(apiBaseUrl);
            if (mode == "preflight")
            {
                FindFreePort();
                Log("完整启动预检通过。");
                Console.WriteLine("预检通过：程序、配置、密钥、U 盘写入、空间、API 和本地端口可用。");
                return 0;
            }
            SessionRelocator.Prepare(Root, Data, Log);
            if (mode == "tui") return RunTui(project);
            using (AcquireApprovalLock())
            {
                ApprovalMode = ReadApprovalMode();
                CheckConfig();
                StartWeb(project);
            }
            return 0;
        }
        catch (AgentError error)
        {
            Log(error.Message);
            Console.Error.WriteLine(error.Message);
            return 1;
        }
        catch
        {
            Log("PCA199: 未分类错误。");
            Console.Error.WriteLine("PCA199: 操作失败。请运行“查看诊断.cmd”检查环境。");
            return 1;
        }
        finally { Key = null; }
    }

    private static void ConfigureKey()
    {
        CheckPlatform();
        RejectLinkedPath(KeyFile);
        if (File.Exists(Path.Combine(Root, "Scripts", "Assemble-Release.ps1")))
            throw new AgentError("PCA305", "请先装配发布包或复制到 U 盘，再在发布包中配置密钥；源码目录不能写入密钥。");
        Console.WriteLine("请只在自己的电脑上配置专用 DeepSeek Key。输入时不会显示字符。");
        if (Console.IsInputRedirected) throw new AgentError("PCA301", "密钥配置需要可交互的控制台。");
        string first = ReadHiddenKey("输入新 Key：");
        string second = ReadHiddenKey("再次输入确认：");
        if (first != second) throw new AgentError("PCA302", "两次输入不一致，密钥未更改。");
        if (string.IsNullOrWhiteSpace(first) || first.Length > 1024 || first.IndexOf('\r') >= 0 || first.IndexOf('\n') >= 0)
            throw new AgentError("PCA303", "Key 格式不正确，密钥未更改。");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(KeyFile));
            RejectLinkedPath(KeyFile);
            string temporary = KeyFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, first + Environment.NewLine, new UTF8Encoding(false));
                SessionRelocator.ReplaceFileAtomically(temporary, KeyFile);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch { throw new AgentError("PCA304", "无法写入 U 盘密钥文件；请检查写保护和权限。"); }
        Log("专用 DeepSeek Key 已在个人电脑上配置或更换；内容未记录。");
        Console.WriteLine("密钥已保存到 U 盘。请勿把 U 盘交给不可信的人；遗失时立即在 DeepSeek 控制台撤销 Key。");
    }

    private static string ReadHiddenKey(string prompt)
    {
        Console.Write(prompt);
        StringBuilder buffer = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return buffer.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (buffer.Length > 0) buffer.Length--; continue; }
            if (key.KeyChar >= ' ' && key.KeyChar != '\u007f') buffer.Append(key.KeyChar);
        }
    }

    private static string ApprovalLabel(string level)
    {
        switch (level)
        {
            case "readonly": return "只读（编辑与命令禁止）";
            case "manual": return "逐项审批（默认）";
            case "workspace": return "工作区编辑自动，命令审批";
            default: throw new AgentError("PCA135", "未知审批档位。");
        }
    }

    private static string ReadApprovalMode()
    {
        RejectLinkedPath(ApprovalFile);
        if (!File.Exists(ApprovalFile)) return "manual";
        try
        {
            if (new FileInfo(ApprovalFile).Length > 64)
                throw new AgentError("PCA135", "审批设置文件过大；请检查 Data\\run\\approval-mode.txt。");
            string level = File.ReadAllText(ApprovalFile, Encoding.UTF8).Trim().ToLowerInvariant();
            ApprovalLabel(level);
            return level;
        }
        catch (AgentError) { throw; }
        catch { throw new AgentError("PCA135", "审批设置文件无法读取。"); }
    }

    private static void ConfigureApproval(string requested, bool showOnly)
    {
        CheckPlatform();
        CheckStorage();
        CheckConfig();
        string current = ReadApprovalMode();
        Console.WriteLine("当前审批档位：" + ApprovalLabel(current));
        if (showOnly) return;
        string selected = requested;
        if (selected == null)
        {
            if (Console.IsInputRedirected)
                throw new AgentError("PCA131", "请在交互式窗口选择档位，或使用 approval --level 档位。");
            Console.WriteLine("1. 只读：禁止编辑和运行命令");
            Console.WriteLine("2. 逐项审批：编辑和命令均需确认（默认）");
            Console.WriteLine("3. 工作区编辑自动：编辑自动通过，命令仍需确认");
            Console.Write("输入 1 至 3：");
            string answer = Console.ReadLine();
            switch (answer == null ? "" : answer.Trim())
            {
                case "1": selected = "readonly"; break;
                case "2": selected = "manual"; break;
                case "3": selected = "workspace"; break;
                default: throw new AgentError("PCA131", "未选择有效档位，设置未更改。");
            }
        }
        ApprovalLabel(selected);
        using (AcquireApprovalLock())
        {
            current = ReadApprovalMode();
            if (selected == current)
            {
                Console.WriteLine("审批档位未改变。");
                return;
            }
            RejectLinkedPath(StateFile);
            if (File.Exists(StateFile))
                throw new AgentError("PCA132", "Web 服务记录仍在；请先运行“退出 Agent.cmd”，再切换审批档位。");
            string temporary = ApprovalFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                RejectLinkedPath(ApprovalFile);
                File.WriteAllText(temporary, selected + Environment.NewLine, new UTF8Encoding(false));
                SessionRelocator.ReplaceFileAtomically(temporary, ApprovalFile);
            }
            catch (AgentError) { throw; }
            catch { throw new AgentError("PCA133", "无法保存审批档位；请检查 U 盘写入权限。"); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            Log("审批档位已切换为 " + selected + "。");
            Console.WriteLine("已设置为：" + ApprovalLabel(selected) + "。下次启动 Agent 生效。");
        }
    }

    private static FileStream AcquireApprovalLock()
    {
        RejectLinkedPath(ApprovalLockFile);
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try { return new FileStream(ApprovalLockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                if (DateTime.UtcNow >= deadline)
                    throw new AgentError("PCA134", "审批设置正被另一启动器使用；请稍后重试。");
                Thread.Sleep(100);
            }
            catch { throw new AgentError("PCA134", "无法锁定 U 盘审批设置。"); }
        }
    }

    private static void VerifyPackage()
    {
        CheckPlatform();
        CheckExecutable();
        ApprovalMode = ReadApprovalMode();
        CheckConfig();
        Console.WriteLine("离线包检查通过：OpenCode 1.18.32、SHA-256 和配置 JSON 有效。");
        Console.WriteLine("本检查未验证 Key、DeepSeek API、Web/TUI 或目标机房权限。");
    }

    private static void CheckPlatform()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT || !Environment.Is64BitOperatingSystem)
            throw new AgentError("PCA101", "需要 Windows x64 电脑。");
        IntPtr memory = Marshal.AllocHGlobal(64);
        try
        {
            GetNativeSystemInfo(memory);
            if (Marshal.ReadInt16(memory) != 9)
                throw new AgentError("PCA101", "需要原生 x64（AMD64）电脑；ARM64 不在首版支持范围。");
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    internal static void RejectLinkedPath(string path)
    {
        string cursor = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(cursor))
        {
            try
            {
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new AgentError("PCA121", "运行路径包含链接或联接点；请使用 U 盘上的实际目录与文件。");
            }
            catch (FileNotFoundException) {}
            catch (DirectoryNotFoundException) {}
            string parent = Path.GetDirectoryName(cursor);
            if (string.IsNullOrEmpty(parent) || parent == cursor) break;
            cursor = parent;
        }
    }

    private static bool IsHealthyWeb(int port)
    {
        try
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(
                "http://127.0.0.1:" + port + "/global/health");
            request.Proxy = null;
            request.Timeout = 1500;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream()))
            {
                HealthResponse health = Json.Deserialize<HealthResponse>(reader.ReadToEnd());
                return health != null && health.healthy && health.version == PinnedVersion;
            }
        }
        catch { return false; }
    }

    private static void CheckExecutable()
    {
        if (!File.Exists(Exe)) throw new AgentError("PCA102", "缺少 Agent\\opencode.exe；请在个人电脑装配官方程序。");
        RejectLinkedPath(Exe);
        try
        {
            FileInfo file = new FileInfo(Exe);
            if (file.Length < 1024 * 1024) throw new AgentError("PCA102", "OpenCode 程序文件不完整。");
            string actual;
            using (FileStream stream = File.Open(Exe, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 sha = SHA256.Create())
                actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            if (!string.Equals(actual, PinnedHash, StringComparison.OrdinalIgnoreCase))
                throw new AgentError("PCA102", "OpenCode SHA-256 校验失败；请重新装配官方程序。");
            ProcessStartInfo info = new ProcessStartInfo(Exe, "--version");
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            using (Process process = Process.Start(info))
            {
                if (!process.WaitForExit(15000))
                {
                    process.Kill();
                    throw new AgentError("PCA102", "OpenCode 版本检查超时。");
                }
                string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                if (process.ExitCode != 0 || !Regex.IsMatch(output, @"(^|\D)1\.18\.32(\D|$)"))
                    throw new AgentError("PCA102", "OpenCode 版本不匹配；本发布包要求 " + PinnedVersion + "。");
            }
        }
        catch (AgentError) { throw; }
        catch { throw new AgentError("PCA102", "无法检查或执行 OpenCode；请检查程序文件和电脑运行限制。"); }
    }

    private static string ReadPinnedConfig(string path, string expectedHash)
    {
        if (!File.Exists(path)) throw new AgentError("PCA103", "缺少固定配置文件：" + Path.GetFileName(path) + "。");
        try
        {
            RejectLinkedPath(path);
            byte[] bytes = File.ReadAllBytes(path);
            using (SHA256 sha = SHA256.Create())
            {
                string actual = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
                if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new AgentError("PCA103", "固定配置文件校验失败：" + Path.GetFileName(path) + "。");
            }
            string text;
            using (MemoryStream stream = new MemoryStream(bytes))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                text = reader.ReadToEnd();
            if (!(Json.DeserializeObject(text) is Dictionary<string, object>))
                throw new AgentError("PCA103", "固定配置文件不是有效 JSON 对象：" + Path.GetFileName(path) + "。");
            return text;
        }
        catch (AgentError) { throw; }
        catch { throw new AgentError("PCA103", "无法安全读取固定配置文件：" + Path.GetFileName(path) + "。"); }
    }

    private static void CheckConfig()
    {
        string manual = ReadPinnedConfig(Config, PinnedConfigHash);
        string readonlyText = ReadPinnedConfig(ReadonlyConfig, PinnedReadonlyConfigHash);
        string workspaceText = ReadPinnedConfig(WorkspaceConfig, PinnedWorkspaceConfigHash);
        switch (ApprovalMode)
        {
            case "manual": ConfigText = manual; break;
            case "readonly": ConfigText = readonlyText; break;
            case "workspace": ConfigText = workspaceText; break;
            default: throw new AgentError("PCA135", "未知审批档位。");
        }
    }

    private static void CheckStorage()
    {
        string[] dirs = { Workspace, Path.Combine(Root, "Logs"), Path.Combine(Data, "run"),
            Path.Combine(Data, "config"), Path.Combine(Data, "cache"),
            Path.Combine(Data, "sessions"), Path.Combine(Data, "sessions", "opencode"),
            Path.Combine(Data, "temp"), Path.Combine(Data, "home") };
        foreach (string dir in dirs)
        {
            try
            {
                RejectLinkedPath(dir);
                Directory.CreateDirectory(dir);
                RejectLinkedPath(dir);
                string probe = Path.Combine(dir, ".write-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
            }
            catch { throw new AgentError("PCA104", "U 盘目录不可写；请检查写保护和权限。"); }
        }
        foreach (string file in new string[] {
            Path.Combine(Data, "sessions", "opencode", "opencode.db"),
            Path.Combine(Data, "sessions", "opencode", "opencode.db-wal"),
            Path.Combine(Data, "sessions", "opencode", "opencode.db-shm"),
            Path.Combine(Data, "run", "last-root.txt") })
            RejectLinkedPath(file);
        try
        {
            DriveInfo drive = new DriveInfo(Path.GetPathRoot(Root));
            if (drive.AvailableFreeSpace < MinimumFreeBytes)
                throw new AgentError("PCA106", "U 盘剩余空间不足 128 MiB。");
        }
        catch (AgentError) { throw; }
        catch { throw new AgentError("PCA106", "无法确认 U 盘剩余空间。"); }
    }

    private static string GetProject(string requested)
    {
        string path = string.IsNullOrWhiteSpace(requested) ? Workspace :
            (Path.IsPathRooted(requested) ? requested : Path.Combine(Workspace, requested));
        try { path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); }
        catch { throw new AgentError("PCA107", "项目路径无效。"); }
        string basePath = Path.GetFullPath(Workspace).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(path)) throw new AgentError("PCA107", "选定项目目录不存在；请放入 Workspace。");
        if (!string.Equals(path, basePath, StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new AgentError("PCA107", "项目必须位于本 U 盘 Workspace 内。");
        string cursor = path;
        while (!string.IsNullOrEmpty(cursor))
        {
            try
            {
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new AgentError("PCA107", "项目或其上级路径含链接目录；请使用 U 盘上的实际目录。");
            }
            catch (AgentError) { throw; }
            catch { throw new AgentError("PCA107", "无法检查项目实际路径。"); }
            string parent = Path.GetDirectoryName(cursor);
            if (string.IsNullOrEmpty(parent) || parent == cursor) break;
            cursor = parent;
        }
        return path;
    }

    private static bool IsProjectConfigName(string name)
    {
        return string.Equals(name, ".opencode", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "opencode.json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "opencode.jsonc", StringComparison.OrdinalIgnoreCase);
    }

    private static void ScanConfigTree(string root, bool global)
    {
        Stack<string> pending = new Stack<string>();
        pending.Push(root);
        int entries = 0;
        try
        {
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                RejectLinkedPath(directory);
                foreach (FileSystemInfo item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    if (++entries > 500000)
                        throw new AgentError("PCA122", "Workspace 项目文件过多，无法在启动前完成安全检查。");
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new AgentError("PCA122", "项目或便携配置包含链接文件/目录，未启动 Agent。");
                    bool folder = (item.Attributes & FileAttributes.Directory) != 0;
                    // Dependency packages are not OpenCode configuration roots.
                    if (global && folder && string.Equals(item.Name, "node_modules", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (IsProjectConfigName(item.Name) ||
                        (global && string.Equals(item.Name, "config.json", StringComparison.OrdinalIgnoreCase)) ||
                        (global && folder &&
                         (string.Equals(item.Name, "plugin", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(item.Name, "plugins", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(item.Name, "agent", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(item.Name, "agents", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(item.Name, "command", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(item.Name, "commands", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(item.Name, "skill", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(item.Name, "skills", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(item.Name, "tool", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(item.Name, "tools", StringComparison.OrdinalIgnoreCase))))
                        throw new AgentError("PCA122", "项目或便携配置包含 OpenCode 配置/插件。请在个人电脑审查并移除后再启动。");
                    if (folder) pending.Push(item.FullName);
                }
            }
        }
        catch (AgentError) { throw; }
        catch { throw new AgentError("PCA122", "无法完整检查项目或便携配置目录；未读取 Key，未启动 Agent。"); }
    }

    private static void CheckProjectConfiguration(string project)
    {
        ScanConfigTree(Workspace, false);
        ScanConfigTree(Path.Combine(Data, "config"), true);
        string home = Path.Combine(Data, "home");
        foreach (string name in new string[] { ".opencode", "opencode.json", "opencode.jsonc" })
        {
            string candidate = Path.Combine(home, name);
            RejectLinkedPath(candidate);
            if (File.Exists(candidate) || Directory.Exists(candidate))
                throw new AgentError("PCA122", "便携 Home 目录含 OpenCode 配置/插件，未启动 Agent。");
        }
        string homeConfig = Path.Combine(home, ".config", "opencode");
        RejectLinkedPath(homeConfig);
        if (Directory.Exists(homeConfig)) ScanConfigTree(homeConfig, true);
        string cursor = project;
        while (!string.IsNullOrEmpty(cursor))
        {
            foreach (string name in new string[] { ".opencode", "opencode.json", "opencode.jsonc" })
            {
                string candidate = Path.Combine(cursor, name);
                RejectLinkedPath(candidate);
                if (File.Exists(candidate) || Directory.Exists(candidate))
                    throw new AgentError("PCA122", "项目上级目录含 OpenCode 配置/插件，未启动 Agent。");
            }
            string parent = Path.GetDirectoryName(cursor);
            if (string.IsNullOrEmpty(parent) || parent == cursor) break;
            cursor = parent;
        }
    }

    private static void ReadKey()
    {
        RejectLinkedPath(KeyFile);
        if (!File.Exists(KeyFile)) throw new AgentError("PCA105", "尚未配置 DeepSeek Key；请先在自己的电脑上运行“配置密钥.cmd”。");
        try
        {
            using (StreamReader reader = new StreamReader(KeyFile, Encoding.UTF8, true))
                Key = reader.ReadToEnd().Trim();
        }
        catch { throw new AgentError("PCA105", "无法读取 DeepSeek Key 文件。"); }
        if (string.IsNullOrWhiteSpace(Key) || Key.Length > 1024 || Key.IndexOf('\r') >= 0 || Key.IndexOf('\n') >= 0)
            throw new AgentError("PCA105", "DeepSeek Key 文件应只包含一行有效密钥。");
    }

    private static void CheckApi(string baseUrl)
    {
        Uri uri;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out uri))
            throw new AgentError("PCA108", "API 检查地址无效。");
        bool official = uri.Scheme == Uri.UriSchemeHttps &&
            string.Equals(uri.Host, "api.deepseek.com", StringComparison.OrdinalIgnoreCase);
        bool localTest = uri.Scheme == Uri.UriSchemeHttp &&
            (uri.Host == "127.0.0.1" || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
        if ((!official && !localTest) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 ||
            uri.Fragment.Length > 0 || uri.AbsolutePath != "/")
            throw new AgentError("PCA108", "API 检查地址只能是 DeepSeek 官方地址或本机测试服务。");
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(baseUrl.TrimEnd('/') + "/models");
        request.Method = "GET";
        request.Accept = "application/json";
        request.Timeout = 12000;
        request.ReadWriteTimeout = 12000;
        request.AllowAutoRedirect = false;
        request.Headers[HttpRequestHeader.Authorization] = "Bearer " + Key;
        if (localTest) request.Proxy = null;
        try
        {
            string body;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                body = reader.ReadToEnd();
            ModelsResponse models = Json.Deserialize<ModelsResponse>(body);
            if (models == null || models.data == null)
                throw new AgentError("PCA108", "DeepSeek 模型列表响应格式异常。");
            bool found = false;
            foreach (ModelEntry model in models.data)
                if (model != null && model.id == "deepseek-flash") found = true;
            if (!found) throw new AgentError("PCA109", "当前 API 未列出 deepseek-flash 模型。");
            Log("API 模型列表预检通过；未验证付费推理。");
        }
        catch (WebException error)
        {
            HttpWebResponse response = error.Response as HttpWebResponse;
            int status = response == null ? 0 : (int)response.StatusCode;
            if (response != null) response.Close();
            if (status == 401) throw new AgentError("PCA110", "DeepSeek Key 无效或已撤销；请在个人电脑上更换。");
            if (status == 403) throw new AgentError("PCA110", "DeepSeek 拒绝当前 Key；请检查账户权限。");
            if (status == 402) throw new AgentError("PCA111", "DeepSeek 账户余额不足或付款受限。");
            if (status == 429) throw new AgentError("PCA112", "DeepSeek 请求过多或达到速率限制。");
            if (status >= 500) throw new AgentError("PCA113", "DeepSeek 服务暂时不可用。");
            if (status != 0) throw new AgentError("PCA108", "DeepSeek API 检查失败，HTTP " + status + "。");
            if (error.Status == WebExceptionStatus.Timeout)
                throw new AgentError("PCA114", "连接 DeepSeek 超时；请检查机房网络、代理和防火墙。");
            if (error.Status == WebExceptionStatus.TrustFailure || error.Status == WebExceptionStatus.SecureChannelFailure)
                throw new AgentError("PCA115", "DeepSeek HTTPS/TLS 连接失败；请检查电脑时间和证书。");
            throw new AgentError("PCA114", "无法连接 DeepSeek API；请确认机房网络允许访问 api.deepseek.com。");
        }
        catch (AgentError) { throw; }
        catch { throw new AgentError("PCA108", "DeepSeek 模型列表响应无法解析。"); }
    }

    private static void SetChildEnvironment(ProcessStartInfo info)
    {
        // The verified full configuration stays in memory. Avoid a second disk read by OpenCode.
        info.EnvironmentVariables["OPENCODE_CONFIG"] = Path.Combine(Data, "run",
            ".verified-from-environment-" + Guid.NewGuid().ToString("N") + ".json");
        info.EnvironmentVariables["OPENCODE_CONFIG_CONTENT"] = ConfigText;
        info.EnvironmentVariables.Remove("OPENCODE_PERMISSION");
        info.EnvironmentVariables["OPENCODE_CONFIG_DIR"] = Path.Combine(Data, "config");
        info.EnvironmentVariables["XDG_CONFIG_HOME"] = Path.Combine(Data, "config");
        info.EnvironmentVariables["XDG_DATA_HOME"] = Path.Combine(Data, "sessions");
        info.EnvironmentVariables["XDG_CACHE_HOME"] = Path.Combine(Data, "cache");
        info.EnvironmentVariables["XDG_STATE_HOME"] = Path.Combine(Data, "sessions");
        info.EnvironmentVariables["HOME"] = Path.Combine(Data, "home");
        info.EnvironmentVariables["USERPROFILE"] = Path.Combine(Data, "home");
        info.EnvironmentVariables["APPDATA"] = Path.Combine(Data, "config");
        info.EnvironmentVariables["LOCALAPPDATA"] = Path.Combine(Data, "cache");
        info.EnvironmentVariables["TEMP"] = Path.Combine(Data, "temp");
        info.EnvironmentVariables["TMP"] = Path.Combine(Data, "temp");
        info.EnvironmentVariables["DEEPSEEK_API_KEY"] = Key;
        info.EnvironmentVariables["OPENCODE_DISABLE_AUTOUPDATE"] = "true";
        info.EnvironmentVariables["OPENCODE_DISABLE_MODELS_FETCH"] = "true";
        info.EnvironmentVariables["OPENCODE_DISABLE_DEFAULT_PLUGINS"] = "true";
        info.EnvironmentVariables["OPENCODE_DISABLE_LSP_DOWNLOAD"] = "true";
        info.EnvironmentVariables["OPENCODE_DISABLE_CLAUDE_CODE"] = "true";
        info.EnvironmentVariables["OPENCODE_DISABLE_PROJECT_CONFIG"] = "true";
    }

    private static int RunTui(string project)
    {
        Process process;
        try
        {
            using (AcquireApprovalLock())
            {
                ApprovalMode = ReadApprovalMode();
                CheckConfig();
                Log("启动 TUI，审批档位 " + ApprovalMode + "。");
                Console.WriteLine("正在打开终端界面，项目：" + project);
                Console.WriteLine("审批档位：" + ApprovalLabel(ApprovalMode));
                ProcessStartInfo info = new ProcessStartInfo(Exe, "--pure");
                info.UseShellExecute = false;
                info.WorkingDirectory = project;
                SetChildEnvironment(info);
                process = Process.Start(info);
            }
            using (process)
            {
                process.WaitForExit();
                Log("TUI 退出，代码 " + process.ExitCode + "。");
                return process.ExitCode;
            }
        }
        catch (AgentError) { throw; }
        catch { throw new AgentError("PCA119", "TUI 无法启动；请检查电脑执行限制。"); }
    }

    private static ServerState ReadState()
    {
        RejectLinkedPath(StateFile);
        if (!File.Exists(StateFile)) return null;
        try { return Json.Deserialize<ServerState>(File.ReadAllText(StateFile, Encoding.UTF8)); }
        catch { return null; }
    }

    private static Process ProcessForState(ServerState state)
    {
        if (state == null || state.pid <= 0 || state.startTicksUtc <= 0 ||
            state.port < 1 || state.port > 65535 ||
            !string.Equals(state.exe, Exe, StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            Process process = Process.GetProcessById(state.pid);
            if (process.HasExited ||
                !string.Equals(process.MainModule.FileName, Exe, StringComparison.OrdinalIgnoreCase) ||
                process.StartTime.ToUniversalTime().Ticks != state.startTicksUtc)
            {
                process.Dispose();
                return null;
            }
            return process;
        }
        catch { return null; }
    }

    private static int FindFreePort()
    {
        for (int port = 4096; port <= 4136; port++)
        {
            TcpListener listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                return port;
            }
            catch (SocketException) {}
            finally { if (listener != null) listener.Stop(); }
        }
        throw new AgentError("PCA116", "本机 4096 至 4136 端口均被占用。");
    }

    private static void WaitWeb(Process process, int port)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(45);
        string url = "http://127.0.0.1:" + port + "/global/health";
        while (DateTime.UtcNow < deadline)
        {
            process.Refresh();
            if (process.HasExited) throw new AgentError("PCA117", "OpenCode Web 服务启动后立即退出。");
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Proxy = null;
                request.Timeout = 600;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    HealthResponse health = Json.Deserialize<HealthResponse>(reader.ReadToEnd());
                    if (health != null && health.healthy && health.version == PinnedVersion &&
                        OwnsLoopbackPort(port, process.Id)) return;
                }
            }
            catch {}
            Thread.Sleep(300);
        }
        throw new AgentError("PCA117", "OpenCode Web 服务未在 45 秒内就绪。");
    }

    private static void StartWeb(string project)
    {
        bool stateExists = File.Exists(StateFile);
        ServerState old = ReadState();
        if (stateExists && old == null)
            throw new AgentError("PCA201", "Web 服务记录损坏；为避免遗留服务，未再次启动。请检查诊断并确认旧进程已结束。");
        Process running = ProcessForState(old);
        if (stateExists && running == null)
            throw new AgentError("PCA201", "Web 服务记录未匹配到当前进程；请先运行“退出 Agent.cmd”清理旧记录。");
        if (running != null)
        {
            using (running)
            {
                if (!string.Equals(old.project, project, StringComparison.OrdinalIgnoreCase))
                    throw new AgentError("PCA118", "Web 服务已在另一项目运行；请先退出 Agent 再切换项目。");
                if (!string.Equals(old.approvalMode, ApprovalMode, StringComparison.Ordinal))
                    throw new AgentError("PCA132", "现有 Web 服务使用另一审批档位；请先运行“退出 Agent.cmd”再启动。");
                if (!OwnsLoopbackPort(old.port, running.Id) || !IsHealthyWeb(old.port))
                    throw new AgentError("PCA118", "已有 Web 进程的端口或版本校验失败；未打开该地址。请先退出并重新启动。");
                try { Process.Start("http://127.0.0.1:" + old.port + "/"); }
                catch { throw new AgentError("PCA120", "服务运行中，但浏览器无法自动打开；请手动访问屏幕所示地址。"); }
                Console.WriteLine("已打开正在运行的 Agent：http://127.0.0.1:" + old.port + "/");
                return;
            }
        }
        int port = FindFreePort();
        ProcessStartInfo info = new ProcessStartInfo(Exe,
            "web --pure --hostname 127.0.0.1 --port " + port);
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.WorkingDirectory = project;
        SetChildEnvironment(info);
        Log("启动 Web，端口 " + port + "。");
        Process process;
        try { process = Process.Start(info); }
        catch { throw new AgentError("PCA117", "OpenCode Web 服务无法启动；请检查电脑执行限制。"); }
        using (process)
        {
            try
            {
                WaitWeb(process, port);
                ServerState state = new ServerState();
                state.pid = process.Id;
                state.startTicksUtc = process.StartTime.ToUniversalTime().Ticks;
                state.exe = Exe;
                state.port = port;
                state.project = project;
                state.approvalMode = ApprovalMode;
                Directory.CreateDirectory(Path.GetDirectoryName(StateFile));
                RejectLinkedPath(StateFile);
                string temporary = StateFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, Json.Serialize(state), new UTF8Encoding(false));
                    SessionRelocator.ReplaceFileAtomically(temporary, StateFile);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                Console.WriteLine("Agent 已启动：http://127.0.0.1:" + port + "/");
                Console.WriteLine("审批档位：" + ApprovalLabel(ApprovalMode));
                Console.WriteLine("OpenCode Web 会打开浏览器；关闭标签页不会退出服务。完成任务后双击“退出 Agent.cmd”。");
                Log("Web 就绪，端口 " + port + "，进程 " + process.Id + "。");
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(); } catch {}
                throw;
            }
        }
    }

    private static bool OwnsLoopbackPort(int port, int pid)
    {
        if (port < 4096 || port > 4136) return false;
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
        if (size < 4 || size > 1024 * 1024) return false;
        IntPtr table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, 2, 3, 0) != 0) return false;
            int count = Marshal.ReadInt32(table);
            if (count < 0 || count > (size - 4) / 24) return false;
            for (int i = 0; i < count; i++)
            {
                IntPtr row = IntPtr.Add(table, 4 + i * 24);
                int state = Marshal.ReadInt32(row);
                int localPort = (Marshal.ReadByte(row, 8) << 8) | Marshal.ReadByte(row, 9);
                int owner = Marshal.ReadInt32(row, 20);
                bool loopback = Marshal.ReadByte(row, 4) == 127 &&
                    Marshal.ReadByte(row, 5) == 0 &&
                    Marshal.ReadByte(row, 6) == 0 &&
                    Marshal.ReadByte(row, 7) == 1;
                if (state == 2 && loopback && localPort == port && owner == pid) return true;
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(table); }
    }

    private static void TryDispose(Process process, int port)
    {
        try
        {
            if (!OwnsLoopbackPort(port, process.Id)) return;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(
                "http://127.0.0.1:" + port + "/instance/dispose");
            request.Proxy = null;
            request.Method = "POST";
            request.ContentLength = 0;
            request.Timeout = 1500;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse()) {}
            Log("已请求 OpenCode 释放实例。");
            process.WaitForExit(3000);
        }
        catch { Log("实例释放请求未完成；继续关闭已核实的进程。"); }
    }

    private static void StopWeb()
    {
        if (!File.Exists(StateFile))
        {
            Console.WriteLine("没有由本 U 盘启动器记录的 Web 服务。");
            return;
        }
        ServerState state = ReadState();
        if (state == null || state.pid <= 0 || state.startTicksUtc <= 0)
            throw new AgentError("PCA201", "服务记录损坏，无法安全识别进程。");
        if (!string.Equals(state.exe, Exe, StringComparison.OrdinalIgnoreCase))
            throw new AgentError("PCA202", "服务记录中的程序路径与本 U 盘不符；未终止任何进程。");
        Process candidate;
        try { candidate = Process.GetProcessById(state.pid); }
        catch (ArgumentException)
        {
            File.Delete(StateFile);
            Log("清理已结束服务的记录。");
            Console.WriteLine("Web 服务已经退出；已清理旧记录。");
            return;
        }
        using (candidate)
        {
            Process checkedProcess = ProcessForState(state);
            if (checkedProcess == null)
                throw new AgentError("PCA202", "进程身份与服务记录不符；为避免误杀，未终止任何进程。");
            checkedProcess.Dispose();
            TryDispose(candidate, state.port);
            try
            {
                candidate.Refresh();
                if (!candidate.HasExited)
                {
                    candidate.Kill();
                    if (!candidate.WaitForExit(10000))
                        throw new AgentError("PCA203", "已请求退出，但进程仍在运行。");
                }
            }
            catch (AgentError) { throw; }
            catch { throw new AgentError("PCA203", "无法终止已识别的 Web 服务。"); }
            File.Delete(StateFile);
            Log("Web 服务已退出，进程 " + candidate.Id + "。");
            Console.WriteLine("Agent Web 服务已退出。请关闭浏览器并安全弹出 U 盘。");
        }
    }

    private static void Diagnose()
    {
        Console.WriteLine("Portable Coding Agent 诊断");
        Console.WriteLine("根目录：" + Root);
        Console.WriteLine("系统：Windows 构建号 " + GetWindowsBuild());
        Console.WriteLine("原生架构：" + GetArchitectureLabel());
        Console.WriteLine("OpenCode 程序：" + (File.Exists(Exe) ? "存在" : "缺失"));
        Console.WriteLine("配置文件：" + (File.Exists(Config) ? "存在" : "缺失"));
        try { Console.WriteLine("审批档位：" + ApprovalLabel(ReadApprovalMode())); }
        catch { Console.WriteLine("审批档位：设置文件无效"); }
        Console.WriteLine("密钥文件：" + (File.Exists(KeyFile) ? "已配置（内容不显示）" : "未配置"));
        if (File.Exists(Exe))
        {
            try { CheckExecutable(); Console.WriteLine("OpenCode SHA/版本：1.18.32，校验通过"); }
            catch { Console.WriteLine("OpenCode SHA/版本：校验失败或无法运行"); }
        }
        if (File.Exists(Config))
        {
            try { CheckConfig(); Console.WriteLine("配置 JSON：有效"); }
            catch { Console.WriteLine("配置 JSON：无效"); }
        }
        foreach (string name in new string[] { "config", "cache", "sessions", "temp", "home", "run" })
            Console.WriteLine("Data\\" + name + "：" +
                (Directory.Exists(Path.Combine(Data, name)) ? "存在" : "缺失"));
        try
        {
            DriveInfo drive = new DriveInfo(Path.GetPathRoot(Root));
            Console.WriteLine("磁盘剩余：" + (drive.AvailableFreeSpace / (1024 * 1024)) + " MiB");
        }
        catch { Console.WriteLine("磁盘剩余：无法读取"); }
        try
        {
            Dns.GetHostAddresses("api.deepseek.com");
            using (TcpClient client = new TcpClient())
            {
                IAsyncResult pending = client.BeginConnect("api.deepseek.com", 443, null, null);
                if (pending.AsyncWaitHandle.WaitOne(3000, false))
                {
                    client.EndConnect(pending);
                    Console.WriteLine("DeepSeek 网络：DNS/TCP 443 可达（未验证 TLS、Key 或余额）");
                }
                else Console.WriteLine("DeepSeek 网络：TCP 443 超时");
            }
        }
        catch { Console.WriteLine("DeepSeek 网络：DNS 或 TCP 443 不可达"); }
        ServerState state = ReadState();
        Process running = ProcessForState(state);
        if (running == null)
            Console.WriteLine(File.Exists(StateFile) ? "Web 服务：记录存在但进程未运行或身份不符" : "Web 服务：无运行记录");
        else
        {
            using (running) Console.WriteLine("Web 服务：运行中，端口 " + state.port);
        }
        if (File.Exists(LogFile))
        {
            Console.WriteLine();
            Console.WriteLine("最近启动器日志（已遮盖密钥）：");
            string secret = "";
            try { if (File.Exists(KeyFile)) secret = File.ReadAllText(KeyFile, Encoding.UTF8).Trim(); } catch {}
            try
            {
                string[] lines = File.ReadAllLines(LogFile, Encoding.UTF8);
                for (int i = Math.Max(0, lines.Length - 20); i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (secret.Length > 0) line = line.Replace(secret, "[REDACTED]");
                    line = Regex.Replace(line, @"(?i)Bearer\s+\S+", "Bearer [REDACTED]");
                    line = Regex.Replace(line, @"(?i)sk-[A-Za-z0-9_-]{8,}", "[REDACTED]");
                    Console.WriteLine(line);
                }
            }
            catch { Console.WriteLine("无法读取启动器日志。"); }
        }
        Console.WriteLine("浏览器缓存和 Windows 系统事件等本机痕迹不由此诊断覆盖。");
    }

    private static string GetWindowsBuild()
    {
        try
        {
            object build = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                "CurrentBuildNumber", null);
            return build == null ? "无法读取" : Convert.ToString(build);
        }
        catch { return "无法读取"; }
    }
    private static string GetArchitectureLabel()
    {
        try
        {
            IntPtr memory = Marshal.AllocHGlobal(64);
            try
            {
                GetNativeSystemInfo(memory);
                short architecture = Marshal.ReadInt16(memory);
                if (architecture == 9) return "x64";
                if (architecture == 12) return "ARM64";
                return "其他（" + architecture + "）";
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        catch { return "无法读取"; }
    }

    private static void Log(string message)
    {
        try
        {
            RejectLinkedPath(LogFile);
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile));
            RejectLinkedPath(LogFile);
            File.AppendAllText(LogFile,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch {}
    }
}

