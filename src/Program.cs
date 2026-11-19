using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("Obsidian MD Opener")]
[assembly: AssemblyDescription("Opens Markdown files in fast, configuration-synchronized Obsidian vaults.")]
[assembly: AssemblyCompany("Personal utility")]
[assembly: AssemblyProduct("Obsidian MD Opener")]
[assembly: AssemblyCopyright("Copyright © 2026")]
[assembly: AssemblyVersion("2.1.10.0")]
[assembly: AssemblyFileVersion("2.1.10.0")]

namespace ObsidianMdOpener
{
    internal static class Program
    {
        private const string AppTitle = "Obsidian MD Opener";
        private const string ConfigMutexName =
            @"Local\ObsidianMdOpener.ObsidianConfig";
        private const string OperationMutexName =
            @"Local\ObsidianMdOpener.Operation";

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 1 &&
                string.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
            {
                return SelfTests.Run();
            }
            if (args.Length == 2 &&
                string.Equals(args[0], "--diagnose", StringComparison.OrdinalIgnoreCase))
            {
                return Diagnostics.PrintPlan(args[1]);
            }
            if (args.Length == 2 &&
                string.Equals(args[0], "--prepare-only", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    PreparedOpen prepared = PrepareMarkdownFile(
                        args[1],
                        false,
                        null,
                        false);
                    WriteUtf8StandardOutput(
                        "仓库：" + prepared.VaultPath + Environment.NewLine +
                        "打开路径：" + prepared.OpenPath + Environment.NewLine +
                        "模式：" + (prepared.UsedShadowVault ? "轻量仓库" : "直接父目录仓库"));
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex.Message);
                    return 1;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 3 &&
                string.Equals(
                    args[0],
                    "--sync-config",
                    StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    AppPaths paths = AppPaths.Create();
                    ActivityLog.Initialize(paths);
                    CentralConfigManager manager =
                        new CentralConfigManager(paths);
                    manager.SynchronizeChangedFile(args[1], args[2]);
                    return 0;
                }
                catch (Exception ex)
                {
                    ActivityLog.Write("配置实时同步失败：" + ex.Message);
                    return 1;
                }
            }
            if ((args.Length == 3 || args.Length == 4) &&
                string.Equals(
                    args[0],
                    "--prepare-from-browser",
                    StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    PreparedOpen prepared = PrepareMarkdownFromBrowser(
                        args[1],
                        args[2],
                        args.Length == 4 ? args[3] : null);
                    WriteUtf8StandardOutput(prepared.OpenPath);
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex.Message);
                    return 1;
                }
            }
            if ((args.Length == 3 || args.Length == 4) &&
                string.Equals(
                    args[0],
                    "--open-from-browser",
                    StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    OpenMarkdownFromBrowser(
                        args[1],
                        args[2],
                        args.Length == 4 ? args[3] : null);
                    return 0;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        "无法在 Obsidian 中打开文件。\r\n\r\n" + ex.Message,
                        AppTitle,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return 1;
                }
            }
            if (args.Length == 0)
            {
                MessageBox.Show(
                    "这是 Markdown 文件的 Obsidian 智能启动器。\r\n\r\n" +
                    "双击 Markdown 文件时，它会自动选择快速的仓库边界、" +
                    "同步常用配置，并在 Obsidian 中打开原始文件。",
                    AppTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return 0;
            }

            int failures = 0;
            foreach (string argument in args)
            {
                try
                {
                    OpenMarkdownFile(argument);
                }
                catch (Exception ex)
                {
                    failures++;
                    ActivityLog.Write("打开文件失败 | " + ex.Message);
                    MessageBox.Show(
                        "无法在 Obsidian 中打开文件。\r\n\r\n" + ex.Message,
                        AppTitle,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
            return failures == 0 ? 0 : 1;
        }

        internal static void OpenMarkdownFile(string inputPath)
        {
            ExecuteSerializedOperation(delegate
            {
                PreparedOpen prepared = PrepareMarkdownFile(
                    inputPath,
                    false,
                    null,
                    true);
                EnsureFolderIsVault(prepared.VaultPath);
                LaunchPreparedFile(prepared);
            });
        }

        internal static void OpenMarkdownFromBrowser(
            string inputPath,
            string currentVaultPath,
            string browserRoot)
        {
            PreparedOpen prepared = PrepareMarkdownFromBrowser(
                inputPath,
                currentVaultPath,
                browserRoot);
            LaunchPreparedFile(prepared);
        }

        private static PreparedOpen PrepareMarkdownFromBrowser(
            string inputPath,
            string currentVaultPath,
            string browserRoot)
        {
            PreparedOpen prepared = null;
            ExecuteSerializedOperation(delegate
            {
                prepared = PrepareMarkdownFile(
                    inputPath,
                    true,
                    browserRoot,
                    false);
                bool alreadyInCurrentVault =
                    !string.IsNullOrWhiteSpace(currentVaultPath) &&
                    PathsEqual(prepared.VaultPath, currentVaultPath);
                if (!alreadyInCurrentVault)
                {
                    EnsureFolderIsVault(prepared.VaultPath);
                }
            });
            return prepared;
        }

        internal static void ExecuteSerializedOperation(Action operation)
        {
            bool lockTaken = false;
            using (Mutex mutex = new Mutex(false, OperationMutexName))
            {
                try
                {
                    try
                    {
                        lockTaken = mutex.WaitOne(TimeSpan.FromMinutes(10));
                    }
                    catch (AbandonedMutexException)
                    {
                        lockTaken = true;
                    }
                    if (!lockTaken)
                    {
                        throw new TimeoutException(
                            "多个文件正在排队打开，请稍后重试。");
                    }
                    operation();
                }
                finally
                {
                    if (lockTaken)
                    {
                        mutex.ReleaseMutex();
                    }
                }
            }
        }

        private static void LaunchPreparedFile(PreparedOpen prepared)
        {
            string configPath = GetObsidianConfigPath();
            Dictionary<string, object> config = ReadConfig(configPath);
            string vaultId = FindRegisteredVaultId(GetVaults(config, false), prepared.VaultPath);
            if (vaultId == null)
            {
                throw new InvalidOperationException("找不到已登记的仓库：" + prepared.VaultPath);
            }

            string relativePath = GetVaultRelativePath(prepared.VaultPath, prepared.OpenPath);
            string cliPath = GetObsidianCliPath();
            if (IsCliEnabled(config) && File.Exists(cliPath))
            {
                OpenThroughCli(cliPath, vaultId, relativePath, prepared.SourcePath);
            }
            else
            {
                OpenThroughUriAndWait(prepared, relativePath);
            }

            ActivityLog.Write(
                "确认打开文件 | 模式=" + (prepared.UsedShadowVault ? "轻量" : "直接") +
                " | 原文件=" + prepared.SourcePath + " | 仓库=" + prepared.VaultPath);
        }

        internal static string GetVaultRelativePath(string vaultPath, string openPath)
        {
            string prefix = NormalizeDirectoryPath(vaultPath).TrimEnd('\\') + "\\";
            if (!openPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("文件不在目标仓库里：" + openPath);
            }
            return openPath.Substring(prefix.Length).Replace('\\', '/');
        }

        internal static string BuildOpenCliArguments(string vaultId, string relativePath)
        {
            return QuoteWindowsArgument("vault=" + vaultId) +
                   " open " + QuoteWindowsArgument("path=" + relativePath) + " newtab";
        }

        internal static bool IsOpenConfirmation(string result, string relativePath)
        {
            foreach (string line in result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(line, "Opened: " + relativePath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        internal static void OpenThroughCli(
            string cliPath, string vaultId, string relativePath, string sourcePath)
        {
            const int timeoutMilliseconds = 90000;
            Stopwatch timer = Stopwatch.StartNew();
            string arguments = BuildOpenCliArguments(vaultId, relativePath);
            bool vaultLaunchRequested = false;
            string result;
            do
            {
                int exitCode;
                result = RunObsidianCli(
                    cliPath, arguments,
                    Math.Max(1, timeoutMilliseconds - (int)timer.ElapsedMilliseconds),
                    out exitCode);
                if (exitCode == 0 && IsOpenConfirmation(result, relativePath))
                {
                    return;
                }
                if (IsObsidianCliUnreachable(result))
                {
                    if (!vaultLaunchRequested)
                    {
                        OpenObsidianUri(BuildObsidianVaultUri(vaultId));
                        vaultLaunchRequested = true;
                    }
                }
                else if (!WasOpenRejectedDuringStartup(result))
                {
                    break;
                }
                Thread.Sleep(200);
            }
            while (timer.ElapsedMilliseconds < timeoutMilliseconds);

            throw new InvalidOperationException(
                "Obsidian 未确认打开：" + sourcePath + "\r\n\r\n" +
                (result.Length == 0 ? "命令行接口没有返回结果。" : result));
        }

        internal static bool WasOpenRejectedDuringStartup(string result)
        {
            // The CLI can answer before Obsidian has registered its file commands.
            return result.StartsWith("Error: Command \"open\" not found.", StringComparison.Ordinal);
        }

        internal static bool IsObsidianCliUnreachable(string result)
        {
            return result.StartsWith(
                       "The CLI is unable to find Obsidian.", StringComparison.OrdinalIgnoreCase) ||
                   result.StartsWith(
                       "Unable to connect to main process", StringComparison.OrdinalIgnoreCase);
        }

        internal static string BuildObsidianVaultUri(string vaultId)
        {
            return "obsidian://open?vault=" + Uri.EscapeDataString(vaultId);
        }

        private static void OpenThroughUriAndWait(PreparedOpen prepared, string relativePath)
        {
            string workspacePath = Path.Combine(prepared.VaultPath, ".obsidian", "workspace.json");
            DateTime beforeOpen = File.GetLastWriteTimeUtc(workspacePath);
            OpenObsidianUri(BuildObsidianUri(prepared.OpenPath));

            Stopwatch timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 90000)
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(workspacePath) != beforeOpen &&
                        WorkspaceContainsFile(File.ReadAllText(workspacePath), relativePath))
                    {
                        return;
                    }
                }
                catch (IOException)
                {
                    // The app may be halfway through replacing its workspace file.
                }
                catch (ArgumentException)
                {
                    // The renderer can be writing JSON when the file is read.
                }
                Thread.Sleep(150);
            }
            throw new TimeoutException("Obsidian 未确认打开：" + prepared.SourcePath);
        }

        private static void OpenObsidianUri(string uri)
        {
            using (Process process = Process.Start(new ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true
            }))
            {
            }
        }

        internal static bool WorkspaceContainsFile(string json, string relativePath)
        {
            Dictionary<string, object> workspace =
                new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                    .DeserializeObject(json) as Dictionary<string, object>;
            object main;
            object floating;
            return workspace != null &&
                ((workspace.TryGetValue("main", out main) && LayoutContainsFile(main, relativePath)) ||
                 (workspace.TryGetValue("floating", out floating) && LayoutContainsFile(floating, relativePath)));
        }

        private static bool LayoutContainsFile(object layout, string relativePath)
        {
            Dictionary<string, object> node = layout as Dictionary<string, object>;
            if (node == null)
            {
                return false;
            }
            object state;
            if (node.TryGetValue("state", out state))
            {
                Dictionary<string, object> view = state as Dictionary<string, object>;
                object viewState;
                if (view != null && view.TryGetValue("state", out viewState))
                {
                    Dictionary<string, object> data = viewState as Dictionary<string, object>;
                    object file;
                    if (data != null && data.TryGetValue("file", out file) &&
                        string.Equals(file as string, relativePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            object children;
            if (node.TryGetValue("children", out children) && children is object[])
            {
                foreach (object child in (object[])children)
                {
                    if (LayoutContainsFile(child, relativePath))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static void WriteUtf8StandardOutput(string value)
        {
            using (Stream output = Console.OpenStandardOutput())
            using (StreamWriter writer = new StreamWriter(
                output,
                new UTF8Encoding(false)))
            {
                writer.WriteLine(value);
                writer.Flush();
            }
        }

        internal static PreparedOpen PrepareMarkdownFile(string inputPath)
        {
            return PrepareMarkdownFile(inputPath, false, null, true);
        }

        internal static PreparedOpen PrepareMarkdownFile(
            string inputPath,
            bool forceShadowVault,
            string browserRoot,
            bool synchronizeCentralConfig)
        {
            string fullPath = ValidateMarkdownPath(inputPath);
            AppPaths paths = AppPaths.Create();
            ActivityLog.Initialize(paths);

            CentralConfigManager central = new CentralConfigManager(paths);
            VaultPlan plan = forceShadowVault
                ? new VaultPlan
                {
                    UseShadowVault = true,
                    Reason = "按需浏览器保持在共享轻量库"
                }
                : VaultPlanner.Create(fullPath, paths);
            string folderPath;
            string openPath;
            if (plan.UseShadowVault)
            {
                ShadowVaultResult shadow = ShadowVault.Prepare(fullPath, paths);
                if (shadow.Succeeded)
                {
                    folderPath = shadow.VaultPath;
                    openPath = shadow.OpenPath;
                }
                else
                {
                    folderPath = NormalizeDirectoryPath(Path.GetDirectoryName(fullPath));
                    openPath = fullPath;
                    ActivityLog.Write("轻量仓库创建失败，回退到直接父目录：" + shadow.Error);
                }
            }
            else
            {
                folderPath = NormalizeDirectoryPath(Path.GetDirectoryName(fullPath));
                openPath = fullPath;
            }

            if (synchronizeCentralConfig)
            {
                RefreshCentralConfigIfDue(central, paths, folderPath);
                central.ApplyToVault(folderPath);
            }
            string sourceRoot = ResolveBrowserRoot(fullPath, browserRoot);
            LazyBrowserPlugin.Install(
                folderPath,
                sourceRoot,
                Assembly.GetExecutingAssembly().Location);
            return new PreparedOpen
            {
                SourcePath = fullPath,
                VaultPath = folderPath,
                OpenPath = openPath,
                UsedShadowVault = plan.UseShadowVault &&
                    !Program.PathsEqual(folderPath, Path.GetDirectoryName(fullPath))
            };
        }

        private static void RefreshCentralConfigIfDue(
            CentralConfigManager central,
            AppPaths paths,
            string vaultPath)
        {
            string stamp = Path.Combine(
                paths.StateRoot,
                "last-central-refresh-" + ComputePathId(vaultPath) + ".stamp");
            if (File.Exists(stamp) &&
                DateTime.UtcNow - File.GetLastWriteTimeUtc(stamp) <
                    TimeSpan.FromSeconds(5))
            {
                ActivityLog.Write(
                    "中央配置检查跳过 | 仓库=" + vaultPath +
                    " | 原因=并发打开复用最近结果");
                return;
            }

            central.RefreshFromVaultPaths(new[] { vaultPath });
            File.WriteAllText(
                stamp,
                DateTime.UtcNow.ToString("O"),
                new UTF8Encoding(false));
        }

        private static string ComputePathId(string path)
        {
            string normalized = NormalizeDirectoryPath(path).ToUpperInvariant();
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                StringBuilder result = new StringBuilder(16);
                for (int index = 0; index < 8; index++)
                {
                    result.Append(hash[index].ToString("x2"));
                }
                return result.ToString();
            }
        }

        private static string ResolveBrowserRoot(
            string markdownPath,
            string requestedRoot)
        {
            string fallback = NormalizeDirectoryPath(
                Path.GetDirectoryName(markdownPath));
            if (string.IsNullOrWhiteSpace(requestedRoot) ||
                !Directory.Exists(requestedRoot))
            {
                return fallback;
            }

            string normalizedRoot = NormalizeDirectoryPath(requestedRoot);
            string prefix = normalizedRoot + Path.DirectorySeparatorChar;
            return markdownPath.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase)
                ? normalizedRoot
                : fallback;
        }

        internal static string ValidateMarkdownPath(string inputPath)
        {
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                throw new InvalidOperationException("没有收到文件路径。");
            }

            string fullPath = Path.GetFullPath(inputPath.Trim().Trim('"'));
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException(
                    "文件不存在或已经被移动：\r\n" + fullPath);
            }
            if (!string.Equals(
                    Path.GetExtension(fullPath),
                    ".md",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("该启动器只支持 .md 文件。");
            }
            return fullPath;
        }

        internal static void EnsureFolderIsVault(string folderPath)
        {
            string configPath = GetObsidianConfigPath();
            if (IsExactVaultRegistered(configPath, folderPath))
            {
                return;
            }
            if (!IsObsidianRunning())
            {
                RegisterVaultInConfig(configPath, folderPath);
                return;
            }
            if (!IsCliEnabled(configPath))
            {
                throw new InvalidOperationException(
                    "首次自动登记新仓库需要启用 Obsidian 命令行接口。\r\n\r\n" +
                    "请进入 Obsidian：设置 → 常规 → 高级，打开“命令行接口”，" +
                    "然后重新双击文件。");
            }

            OpenVaultThroughCli(folderPath);
            if (!WaitForRegistration(configPath, folderPath, 3000))
            {
                throw new InvalidOperationException(
                    "Obsidian 已收到仓库打开请求，但没有及时保存仓库登记。\r\n" +
                    "请完全退出 Obsidian 后重新双击该文件。");
            }
        }

        private static bool IsObsidianRunning()
        {
            Process[] processes = Process.GetProcessesByName("Obsidian");
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (Process process in processes)
                {
                    process.Dispose();
                }
            }
        }

        private static void OpenVaultThroughCli(string folderPath)
        {
            string cliPath = GetObsidianCliPath();
            if (!File.Exists(cliPath))
            {
                throw new FileNotFoundException(
                    "找不到 Obsidian 自带的命令行程序：\r\n" + cliPath +
                    "\r\n\r\n请重新安装 Obsidian 1.12.7 或更高版本。");
            }

            string code =
                "window.electron.ipcRenderer.sendSync('vault-open'," +
                ToJavaScriptString(folderPath) +
                ",false)";
            int exitCode;
            string result = RunObsidianCli(
                cliPath, "eval " + QuoteWindowsArgument("code=" + code), 15000, out exitCode);
            if (exitCode != 0 ||
                result.IndexOf(
                    "Command line interface is not enabled",
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                result.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                throw new InvalidOperationException(
                    "Obsidian 命令行接口没有成功登记仓库。\r\n\r\n" +
                    (result.Length == 0 ? "没有返回详细错误。" : result));
            }
        }

        private static string GetObsidianCliPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "Obsidian",
                "Obsidian.com");
        }

        private static string RunObsidianCli(
            string cliPath, string arguments, int timeoutMilliseconds, out int exitCode)
        {
            ProcessStartInfo info = new ProcessStartInfo
            {
                FileName = cliPath,
                Arguments = arguments,
                WorkingDirectory = Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using (Process process = Process.Start(info))
            {
                if (process == null)
                {
                    throw new InvalidOperationException("无法启动 Obsidian 命令行接口。");
                }
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> error = process.StandardError.ReadToEndAsync();
                Stopwatch timer = Stopwatch.StartNew();
                if (!process.WaitForExit(timeoutMilliseconds) ||
                    !Task.WaitAll(
                        new Task[] { output, error },
                        Math.Max(0, timeoutMilliseconds - (int)timer.ElapsedMilliseconds)))
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException("等待 Obsidian 命令行接口返回时超时。");
                }

                exitCode = process.ExitCode;
                return (output.Result + "\r\n" + error.Result).Trim();
            }
        }

        internal static string GetObsidianConfigPath()
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "obsidian",
                "obsidian.json");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "找不到 Obsidian 仓库配置：\r\n" + path);
            }
            return path;
        }

        internal static Dictionary<string, object> ReadConfig(string configPath)
        {
            Exception lastError = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    string json;
                    using (FileStream stream = new FileStream(
                        configPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite))
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        json = reader.ReadToEnd();
                    }

                    JavaScriptSerializer serializer = new JavaScriptSerializer();
                    serializer.MaxJsonLength = int.MaxValue;
                    Dictionary<string, object> root =
                        serializer.DeserializeObject(json) as Dictionary<string, object>;
                    if (root == null)
                    {
                        throw new InvalidDataException(
                            "Obsidian 配置不是有效的 JSON 对象。");
                    }
                    return root;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Thread.Sleep(100);
                }
            }
            throw new InvalidDataException("无法读取 Obsidian 仓库配置。", lastError);
        }

        internal static Dictionary<string, object> GetVaults(
            Dictionary<string, object> root,
            bool create)
        {
            object value;
            Dictionary<string, object> vaults = null;
            if (root.TryGetValue("vaults", out value))
            {
                vaults = value as Dictionary<string, object>;
            }
            if (vaults == null && create)
            {
                vaults = new Dictionary<string, object>();
                root["vaults"] = vaults;
            }
            return vaults;
        }

        private static bool IsCliEnabled(string configPath)
        {
            return IsCliEnabled(ReadConfig(configPath));
        }

        private static bool IsCliEnabled(Dictionary<string, object> root)
        {
            object value;
            return root.TryGetValue("cli", out value) &&
                   value is bool &&
                   (bool)value;
        }

        private static bool IsExactVaultRegistered(
            string configPath,
            string folderPath)
        {
            return FindRegisteredVaultId(GetVaults(ReadConfig(configPath), false), folderPath) != null;
        }

        private static string FindRegisteredVaultId(
            Dictionary<string, object> vaults,
            string folderPath)
        {
            if (vaults == null)
            {
                return null;
            }

            foreach (KeyValuePair<string, object> entry in vaults)
            {
                Dictionary<string, object> vault =
                    entry.Value as Dictionary<string, object>;
                object pathValue;
                if (vault != null &&
                    vault.TryGetValue("path", out pathValue) &&
                    pathValue is string &&
                    PathsEqual((string)pathValue, folderPath))
                {
                    return entry.Key;
                }
            }
            return null;
        }

        private static bool WaitForRegistration(
            string configPath,
            string folderPath,
            int timeoutMilliseconds)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < timeoutMilliseconds)
            {
                try
                {
                    if (IsExactVaultRegistered(configPath, folderPath))
                    {
                        return true;
                    }
                }
                catch (IOException)
                {
                }
                Thread.Sleep(100);
            }
            return false;
        }

        private static bool RegisterVaultInConfig(
            string configPath,
            string folderPath)
        {
            bool lockTaken = false;
            using (Mutex mutex = new Mutex(false, ConfigMutexName))
            {
                try
                {
                    try
                    {
                        lockTaken = mutex.WaitOne(TimeSpan.FromSeconds(10));
                    }
                    catch (AbandonedMutexException)
                    {
                        lockTaken = true;
                    }
                    if (!lockTaken)
                    {
                        throw new TimeoutException(
                            "另一个启动器正在更新 Obsidian 仓库，请稍后重试。");
                    }

                    Dictionary<string, object> root = ReadConfig(configPath);
                    Dictionary<string, object> vaults = GetVaults(root, true);
                    foreach (object value in vaults.Values)
                    {
                        Dictionary<string, object> vault =
                            value as Dictionary<string, object>;
                        object pathValue;
                        if (vault != null &&
                            vault.TryGetValue("path", out pathValue) &&
                            pathValue is string &&
                            PathsEqual((string)pathValue, folderPath))
                        {
                            return false;
                        }
                    }

                    string id;
                    do
                    {
                        id = Guid.NewGuid().ToString("N").Substring(0, 16);
                    }
                    while (vaults.ContainsKey(id));
                    vaults[id] = new Dictionary<string, object>
                    {
                        { "path", folderPath },
                        { "ts", GetUnixTimeMilliseconds() }
                    };

                    WriteConfigAtomically(configPath, root);
                    return true;
                }
                finally
                {
                    if (lockTaken)
                    {
                        mutex.ReleaseMutex();
                    }
                }
            }
        }

        private static void WriteConfigAtomically(
            string configPath,
            Dictionary<string, object> root)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            string temporaryPath = configPath + ".ObsidianMdOpener.tmp";
            string backupPath = configPath + ".ObsidianMdOpener.bak";

            File.Copy(configPath, backupPath, true);
            try
            {
                File.WriteAllText(
                    temporaryPath,
                    serializer.Serialize(root),
                    new UTF8Encoding(false));
                File.Replace(temporaryPath, configPath, null, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        private static long GetUnixTimeMilliseconds()
        {
            return (long)(DateTime.UtcNow -
                new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                .TotalMilliseconds;
        }

        internal static string NormalizeDirectoryPath(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string root = Path.GetPathRoot(fullPath);
            if (fullPath.Length > root.Length)
            {
                fullPath = fullPath.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            }
            return fullPath;
        }

        internal static bool PathsEqual(string first, string second)
        {
            try
            {
                return string.Equals(
                    NormalizeDirectoryPath(first),
                    NormalizeDirectoryPath(second),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string ToJavaScriptString(string value)
        {
            return "'" + value
                .Replace("\\", "\\\\")
                .Replace("'", "\\'")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n") + "'";
        }

        private static string QuoteWindowsArgument(string value)
        {
            if (value.Length > 0 &&
                value.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            {
                return value;
            }

            StringBuilder result = new StringBuilder();
            result.Append('"');
            int backslashes = 0;
            foreach (char character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                }
                else if (character == '"')
                {
                    result.Append('\\', backslashes * 2 + 1);
                    result.Append('"');
                    backslashes = 0;
                }
                else
                {
                    result.Append('\\', backslashes);
                    result.Append(character);
                    backslashes = 0;
                }
            }

            result.Append('\\', backslashes * 2);
            result.Append('"');
            return result.ToString();
        }

        internal static string BuildObsidianUri(string fullPath)
        {
            string normalizedPath = fullPath.Replace('\\', '/');
            return "obsidian://open?path=" +
                   Uri.EscapeDataString(normalizedPath) +
                   "&paneType=tab";
        }

    }

    internal sealed class PreparedOpen
    {
        public string SourcePath { get; set; }
        public string VaultPath { get; set; }
        public string OpenPath { get; set; }
        public bool UsedShadowVault { get; set; }
    }
}
