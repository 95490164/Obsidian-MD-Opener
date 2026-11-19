using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace ObsidianMdOpener
{
    internal static class SelfTests
    {
        private static int _passed;
        private static int _failed;

        public static int Run()
        {
            Console.OutputEncoding = Encoding.UTF8;
            string testParent = Path.GetTempPath();
            string testRoot = Path.Combine(
                testParent,
                "work-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testRoot);
            AppPaths paths = AppPaths.CreateForTest(Path.Combine(testRoot, "runtime"));
            ActivityLog.Initialize(paths);

            try
            {
                Test("Obsidian URI 支持中文和特殊字符", delegate
                {
                    const string sample = @"C:\测试 目录\文件 #1.md";
                    string uri = Program.BuildObsidianUri(sample);
                    string encoded = uri.Substring(
                        "obsidian://open?path=".Length,
                        uri.Length - "obsidian://open?path=".Length -
                        "&paneType=tab".Length);
                    Assert(Uri.UnescapeDataString(encoded) == sample.Replace('\\', '/'));
                });

                Test("打开确认按目标仓库及文件的准确路径匹配", delegate
                {
                    const string vault = @"C:\测试 仓库";
                    const string path = "下一层/同名 #1.md";
                    Assert(Program.GetVaultRelativePath(vault, vault + @"\下一层\同名 #1.md") == path);
                    Assert(Program.BuildOpenCliArguments("0123456789abcdef", path) ==
                        "vault=0123456789abcdef open \"path=下一层/同名 #1.md\" newtab");
                    Assert(Program.IsOpenConfirmation("Opened: " + path + "\r\n", path));
                    Assert(!Program.IsOpenConfirmation("Opened: 另一层/同名 #1.md", path));
                    Assert(!Program.IsOpenConfirmation("Error: " + path, path));
                    Assert(Program.WasOpenRejectedDuringStartup(
                        "Error: Command \"open\" not found. Did you mean: sync:open?"));
                    Assert(!Program.WasOpenRejectedDuringStartup("Opened: " + path));
                });

                Test("工作区只用实际打开的标签页确认笔记", delegate
                {
                    const string layout =
                        "{\"main\":{\"children\":[{\"type\":\"tabs\",\"children\":[{" +
                        "\"type\":\"leaf\",\"state\":{\"type\":\"markdown\"," +
                        "\"state\":{\"file\":\"打开/笔记.md\"}}}]}]}," +
                        "\"lastOpenFiles\":[\"最近/笔记.md\"]}";
                    Assert(Program.WorkspaceContainsFile(layout, "打开/笔记.md"));
                    Assert(!Program.WorkspaceContainsFile(layout, "最近/笔记.md"));
                    Assert(!Program.WorkspaceContainsFile(layout, "笔记.md"));
                });

                Test("CLI 找不到 Obsidian 时可启动目标仓库", delegate
                {
                    Assert(Program.IsObsidianCliUnreachable(
                        "The CLI is unable to find Obsidian. Please make sure Obsidian is running and try again."));
                    Assert(Program.IsObsidianCliUnreachable("Unable to connect to main process"));
                    Assert(!Program.IsObsidianCliUnreachable("Vault not found."));
                    Assert(!Program.IsObsidianCliUnreachable("Opened: 笔记.md"));
                    Assert(Program.BuildObsidianVaultUri("0123456789abcdef") ==
                        "obsidian://open?vault=0123456789abcdef");
                });

                Test("小型目录使用直接父目录", delegate
                {
                    string folder = Path.Combine(testRoot, "small");
                    Directory.CreateDirectory(folder);
                    string markdown = Path.Combine(folder, "sample.md");
                    File.WriteAllText(markdown, "# sample", Encoding.UTF8);
                    for (int index = 0; index < 20; index++)
                    {
                        File.WriteAllText(
                            Path.Combine(folder, "file-" + index + ".txt"),
                            "x",
                            Encoding.UTF8);
                    }
                    VaultPlan plan = VaultPlanner.Create(markdown, paths);
                    Assert(!plan.UseShadowVault);
                });

                Test("大型小文件目录切换到轻量仓库", delegate
                {
                    string folder = Path.Combine(testRoot, "large");
                    Directory.CreateDirectory(folder);
                    string markdown = Path.Combine(folder, "sample.md");
                    File.WriteAllText(markdown, "# sample", Encoding.UTF8);
                    for (int index = 0; index < VaultPlanner.EntryBudget + 20; index++)
                    {
                        using (File.Create(Path.Combine(folder, "f-" + index + ".tmp")))
                        {
                        }
                    }
                    VaultPlan plan = VaultPlanner.Create(markdown, paths);
                    Assert(plan.UseShadowVault);
                });

                Test("轻量仓库编辑仍写回原文件", delegate
                {
                    string folder = Path.Combine(testRoot, "linked");
                    Directory.CreateDirectory(folder);
                    string image = Path.Combine(folder, "image.png");
                    File.WriteAllBytes(image, new byte[] { 1, 2, 3, 4 });
                    string markdown = Path.Combine(folder, "note.md");
                    File.WriteAllText(markdown, "before\n![](image.png)", Encoding.UTF8);
                    ShadowVaultResult result = ShadowVault.Prepare(markdown, paths);
                    Assert(result.Succeeded);
                    ShadowVaultResult repeated = ShadowVault.Prepare(markdown, paths);
                    Assert(repeated.Succeeded);
                    Assert(repeated.OpenPath == result.OpenPath);
                    File.AppendAllText(result.OpenPath, "\nafter", Encoding.UTF8);
                    Assert(File.ReadAllText(markdown, Encoding.UTF8).Contains("after"));
                    string mirroredImage = ShadowVault.GetMirrorPath(result.VaultPath, image);
                    Assert(File.Exists(mirroredImage));
                    Directory.Delete(result.VaultPath, true);
                });

                Test("按需浏览插件完成安装并启用", delegate
                {
                    string vault = Path.Combine(testRoot, "plugin-vault");
                    Directory.CreateDirectory(vault);
                    LazyBrowserPlugin.Install(vault, testRoot, "C:\\Test\\Opener.exe");
                    string pluginRoot = Path.Combine(
                        vault,
                        ".obsidian",
                        "plugins",
                        "obsidian-md-opener-browser");
                    Assert(File.Exists(Path.Combine(pluginRoot, "manifest.json")));
                    Assert(File.Exists(Path.Combine(pluginRoot, "main.js")));
                    string enabled = File.ReadAllText(
                        Path.Combine(vault, ".obsidian", "community-plugins.json"),
                        Encoding.UTF8);
                    Assert(enabled.Contains("obsidian-md-opener-browser"));
                    string pluginCode = File.ReadAllText(
                        Path.Combine(pluginRoot, "main.js"),
                        Encoding.UTF8);
                    Assert(pluginCode.Contains("--prepare-from-browser"));
                    Assert(pluginCode.Contains("reconcileInternalFile"));
                    Assert(pluginCode.Contains("--sync-config"));
                    Assert(pluginCode.Contains("PROGRAMMATIC_SYNC_MARKER"));
                    Assert(pluginCode.Contains("this.settings.sourceRoot"));
                });

                Test("多选并发打开会串行写入仓库配置", delegate
                {
                    string vault = Path.Combine(testRoot, "multi-open-vault");
                    Directory.CreateDirectory(vault);
                    ManualResetEvent start = new ManualResetEvent(false);
                    List<Thread> workers = new List<Thread>();
                    object resultLock = new object();
                    int active = 0;
                    int maximumActive = 0;
                    Exception workerError = null;

                    for (int index = 0; index < 8; index++)
                    {
                        Thread worker = new Thread(delegate()
                        {
                            start.WaitOne();
                            try
                            {
                                Program.ExecuteSerializedOperation(delegate
                                {
                                    int current = Interlocked.Increment(ref active);
                                    lock (resultLock)
                                    {
                                        maximumActive = Math.Max(
                                            maximumActive,
                                            current);
                                    }
                                    LazyBrowserPlugin.Install(
                                        vault,
                                        testRoot,
                                        "C:\\Test\\Opener.exe");
                                    Thread.Sleep(25);
                                    Interlocked.Decrement(ref active);
                                });
                            }
                            catch (Exception ex)
                            {
                                lock (resultLock)
                                {
                                    workerError = workerError ?? ex;
                                }
                            }
                        });
                        worker.IsBackground = true;
                        workers.Add(worker);
                        worker.Start();
                    }

                    start.Set();
                    foreach (Thread worker in workers)
                    {
                        Assert(worker.Join(10000));
                    }
                    Assert(workerError == null);
                    Assert(maximumActive == 1);
                    string enabled = File.ReadAllText(
                        Path.Combine(vault, ".obsidian", "community-plugins.json"),
                        Encoding.UTF8);
                    Assert(enabled.Contains("obsidian-md-opener-browser"));
                });

                Test("配置筛选不会复制工作区状态", delegate
                {
                    string config = Path.Combine(testRoot, "config-filter", ".obsidian");
                    Directory.CreateDirectory(config);
                    File.WriteAllText(Path.Combine(config, "app.json"), "{}", Encoding.UTF8);
                    File.WriteAllText(Path.Combine(config, "workspace.json"), "{}", Encoding.UTF8);
                    List<string> files = CentralConfigManager
                        .EnumerateSafeFiles(config)
                        .Select(Path.GetFileName)
                        .ToList();
                    Assert(files.Contains("app.json"));
                    Assert(!files.Contains("workspace.json"));
                });

                Test("中央配置双副本可自动恢复", delegate
                {
                    string isolated = Path.Combine(testRoot, "central-config");
                    AppPaths isolatedPaths = AppPaths.CreateForTest(isolated);
                    ActivityLog.Initialize(isolatedPaths);
                    string sourceVault = Path.Combine(isolated, "source-vault");
                    string sourceConfig = Path.Combine(sourceVault, ".obsidian");
                    Directory.CreateDirectory(sourceConfig);
                    File.WriteAllText(
                        Path.Combine(sourceConfig, "appearance.json"),
                        "{\"theme\":\"moonstone\"}",
                        Encoding.UTF8);
                    File.WriteAllText(
                        Path.Combine(sourceConfig, "workspace.json"),
                        "{\"mustNotCopy\":true}",
                        Encoding.UTF8);

                    CentralConfigManager manager = new CentralConfigManager(isolatedPaths);
                    manager.RefreshFromVaultPaths(new[] { sourceVault });
                    Assert(File.Exists(Path.Combine(
                        isolatedPaths.PrimaryCentralConfig,
                        "appearance.json")));
                    Assert(File.Exists(Path.Combine(
                        isolatedPaths.BackupCentralConfig,
                        "appearance.json")));
                    Assert(!File.Exists(Path.Combine(
                        isolatedPaths.PrimaryCentralConfig,
                        "workspace.json")));

                    Directory.Delete(isolatedPaths.PrimaryCentralConfig, true);
                    Directory.CreateDirectory(isolatedPaths.PrimaryCentralConfig);
                    manager.EnsureRedundancy();
                    Assert(File.Exists(Path.Combine(
                        isolatedPaths.PrimaryCentralConfig,
                        "appearance.json")));
                });

                Test("配置修改会立即写入中央双副本", delegate
                {
                    string isolated = Path.Combine(testRoot, "live-sync");
                    AppPaths isolatedPaths = AppPaths.CreateForTest(isolated);
                    ActivityLog.Initialize(isolatedPaths);
                    string vault = Path.Combine(isolated, "vault");
                    string config = Path.Combine(vault, ".obsidian");
                    Directory.CreateDirectory(config);
                    string appearance = Path.Combine(config, "appearance.json");
                    File.WriteAllText(appearance, "{\"theme\":\"base\"}", Encoding.UTF8);

                    CentralConfigManager manager = new CentralConfigManager(
                        isolatedPaths,
                        delegate { return ConfigConflictChoice.UseIncoming; });
                    manager.RefreshFromVaultPaths(new[] { vault });
                    File.WriteAllText(appearance, "{\"theme\":\"changed\"}", Encoding.UTF8);
                    ConfigSyncOutcome outcome = manager.SynchronizeChangedFile(
                        vault,
                        "appearance.json");

                    Assert(outcome == ConfigSyncOutcome.CentralUpdated);
                    Assert(File.ReadAllText(
                        Path.Combine(isolatedPaths.PrimaryCentralConfig, "appearance.json"),
                        Encoding.UTF8).Contains("changed"));
                    Assert(File.ReadAllText(
                        Path.Combine(isolatedPaths.BackupCentralConfig, "appearance.json"),
                        Encoding.UTF8).Contains("changed"));
                    Assert(File.Exists(isolatedPaths.SyncStatePath));
                    Assert(File.Exists(isolatedPaths.BackupSyncStatePath));
                });

                Test("并行修改同一配置会触发选择而不是静默覆盖", delegate
                {
                    string isolated = Path.Combine(testRoot, "conflict-sync");
                    AppPaths isolatedPaths = AppPaths.CreateForTest(isolated);
                    ActivityLog.Initialize(isolatedPaths);
                    string vaultA = Path.Combine(isolated, "vault-a");
                    string vaultB = Path.Combine(isolated, "vault-b");
                    string configA = Path.Combine(vaultA, ".obsidian");
                    string configB = Path.Combine(vaultB, ".obsidian");
                    Directory.CreateDirectory(configA);
                    Directory.CreateDirectory(configB);
                    string fileA = Path.Combine(configA, "appearance.json");
                    string fileB = Path.Combine(configB, "appearance.json");
                    File.WriteAllText(fileA, "{\"theme\":\"base\"}", Encoding.UTF8);
                    File.WriteAllText(fileB, "{\"theme\":\"base\"}", Encoding.UTF8);
                    int conflicts = 0;
                    CentralConfigManager manager = new CentralConfigManager(
                        isolatedPaths,
                        delegate
                        {
                            conflicts++;
                            return ConfigConflictChoice.UseCentral;
                        });
                    manager.RefreshFromVaultPaths(new[] { vaultA, vaultB });

                    File.WriteAllText(fileA, "{\"theme\":\"from-a\"}", Encoding.UTF8);
                    Assert(manager.SynchronizeChangedFile(
                        vaultA,
                        "appearance.json") == ConfigSyncOutcome.CentralUpdated);
                    File.WriteAllText(fileB, "{\"theme\":\"from-b\"}", Encoding.UTF8);
                    Assert(manager.SynchronizeChangedFile(
                        vaultB,
                        "appearance.json") == ConfigSyncOutcome.ConflictCentralChosen);

                    Assert(conflicts == 1);
                    Assert(File.ReadAllText(fileB, Encoding.UTF8).Contains("from-a"));
                    Assert(File.ReadAllText(
                        Path.Combine(isolatedPaths.PrimaryCentralConfig, "appearance.json"),
                        Encoding.UTF8).Contains("from-a"));
                });

                Test("JSON 仅格式不同不会误报配置冲突", delegate
                {
                    string isolated = Path.Combine(testRoot, "format-only-sync");
                    AppPaths isolatedPaths = AppPaths.CreateForTest(isolated);
                    ActivityLog.Initialize(isolatedPaths);
                    string vaultA = Path.Combine(isolated, "vault-a");
                    string vaultB = Path.Combine(isolated, "vault-b");
                    string configA = Path.Combine(vaultA, ".obsidian");
                    string configB = Path.Combine(vaultB, ".obsidian");
                    Directory.CreateDirectory(configA);
                    Directory.CreateDirectory(configB);
                    string fileA = Path.Combine(configA, "community-plugins.json");
                    string fileB = Path.Combine(configB, "community-plugins.json");
                    File.WriteAllText(fileA, "[\"plugin-a\",\"plugin-b\"]", Encoding.UTF8);
                    File.WriteAllText(
                        fileB,
                        "[\n  \"plugin-a\",\n  \"plugin-b\"\n]",
                        Encoding.UTF8);
                    int conflicts = 0;
                    CentralConfigManager manager = new CentralConfigManager(
                        isolatedPaths,
                        delegate
                        {
                            conflicts++;
                            return ConfigConflictChoice.UseCentral;
                        });

                    manager.RefreshFromVaultPaths(new[] { vaultA, vaultB });
                    Assert(manager.SynchronizeChangedFile(
                        vaultB,
                        "community-plugins.json") == ConfigSyncOutcome.NoChange);
                    Assert(conflicts == 0);
                });

                Test("旧仓库缺少自管插件不会误报配置冲突", delegate
                {
                    string isolated = Path.Combine(testRoot, "managed-plugin-sync");
                    AppPaths isolatedPaths = AppPaths.CreateForTest(isolated);
                    ActivityLog.Initialize(isolatedPaths);
                    string vaultA = Path.Combine(isolated, "vault-a");
                    string vaultB = Path.Combine(isolated, "vault-b");
                    string configA = Path.Combine(vaultA, ".obsidian");
                    string configB = Path.Combine(vaultB, ".obsidian");
                    Directory.CreateDirectory(configA);
                    Directory.CreateDirectory(configB);
                    string fileA = Path.Combine(configA, "community-plugins.json");
                    string fileB = Path.Combine(configB, "community-plugins.json");
                    File.WriteAllText(
                        fileA,
                        "[\"plugin-a\",\"obsidian-md-opener-browser\"]",
                        Encoding.UTF8);
                    File.WriteAllText(fileB, "[\"plugin-a\"]", Encoding.UTF8);
                    int conflicts = 0;
                    CentralConfigManager manager = new CentralConfigManager(
                        isolatedPaths,
                        delegate
                        {
                            conflicts++;
                            return ConfigConflictChoice.UseCentral;
                        });

                    manager.RefreshFromVaultPaths(new[] { vaultA, vaultB });
                    Assert(manager.SynchronizeChangedFile(
                        vaultB,
                        "community-plugins.json") == ConfigSyncOutcome.NoChange);
                    Assert(conflicts == 0);
                });

                Test("配置同步拒绝越界路径", delegate
                {
                    Assert(CentralConfigManager.NormalizeSafeRelativePath(
                        "..\\workspace.json") == null);
                    Assert(CentralConfigManager.NormalizeSafeRelativePath(
                        "workspace.json") == null);
                    Assert(CentralConfigManager.NormalizeSafeRelativePath(
                        "plugins\\sample\\data.json") != null);
                });
            }
            catch (Exception ex)
            {
                _failed++;
                Console.Error.WriteLine("未处理的测试错误：" + ex);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(testRoot))
                    {
                        Directory.Delete(testRoot, true);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("测试目录清理失败：" + ex.Message);
                    _failed++;
                }
            }

            Console.WriteLine("通过：" + _passed + "，失败：" + _failed);
            return _failed == 0 ? 0 : 2;
        }

        private static void Test(string name, Action body)
        {
            try
            {
                body();
                _passed++;
                Console.WriteLine("[通过] " + name);
            }
            catch (Exception ex)
            {
                _failed++;
                Console.Error.WriteLine("[失败] " + name + " | " + ex.Message);
            }
        }

        private static void Assert(bool condition)
        {
            if (!condition)
            {
                throw new InvalidOperationException("断言不成立");
            }
        }
    }
}
