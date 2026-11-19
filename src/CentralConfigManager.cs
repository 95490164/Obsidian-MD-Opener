using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ObsidianMdOpener
{
    internal enum ConfigSyncOutcome
    {
        NoChange,
        CentralUpdated,
        ConflictIncomingChosen,
        ConflictCentralChosen
    }

    internal sealed class CentralConfigManager
    {
        private const string ConfigSyncMutexName =
            @"Local\ObsidianMdOpener.ConfigSync";
        private const string ManagedPluginPrefix =
            @"plugins\obsidian-md-opener-browser\";
        private const string ManagedPluginId =
            "obsidian-md-opener-browser";
        internal const string ProgrammaticSyncMarker =
            ".obsidian-md-opener-sync";

        private static readonly HashSet<string> AllowedRootFiles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "app.json",
                "appearance.json",
                "community-plugins.json",
                "core-plugins.json",
                "hotkeys.json",
                "templates.json",
                "types.json",
                "graph.json"
            };

        private static readonly HashSet<string> AllowedRootDirectories =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "plugins",
                "themes",
                "snippets"
            };

        private readonly AppPaths _paths;
        private readonly Func<ConfigConflict, ConfigConflictChoice> _conflictResolver;

        public CentralConfigManager(AppPaths paths)
            : this(paths, ConfigConflictDialog.Show)
        {
        }

        internal CentralConfigManager(
            AppPaths paths,
            Func<ConfigConflict, ConfigConflictChoice> conflictResolver)
        {
            _paths = paths;
            _conflictResolver = conflictResolver;
        }

        public void RefreshFromRegisteredVaults()
        {
            List<string> registeredVaults = GetRegisteredVaultPaths();
            RefreshFromVaultPaths(registeredVaults);
            ActivityLog.Write(
                "中央配置检查完成 | 候选仓库=" + registeredVaults.Count);
        }

        internal void RefreshFromVaultPaths(IEnumerable<string> vaultPaths)
        {
            List<string> vaultList = NormalizeExistingVaults(vaultPaths);
            ExecuteLocked(delegate
            {
                RestoreRedundantCopyIfNeeded();
                RestoreSyncStateIfNeeded();
                ConfigSyncState state = LoadState();
                if (state.Files.Count == 0)
                {
                    BootstrapCentralConfig(vaultList, state);
                }
                else
                {
                    ReconcileChangedFiles(vaultList, state);
                }
                SaveState(state);
            });
        }

        public ConfigSyncOutcome SynchronizeChangedFile(
            string vaultPath,
            string relativePath)
        {
            string normalizedVault = Program.NormalizeDirectoryPath(vaultPath);
            string normalizedRelative = NormalizeSafeRelativePath(relativePath);
            if (normalizedRelative == null ||
                normalizedRelative.StartsWith(
                    ManagedPluginPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                return ConfigSyncOutcome.NoChange;
            }

            string source = GetConfigFilePath(normalizedVault, normalizedRelative);
            if (!File.Exists(source))
            {
                ActivityLog.Write(
                    "配置实时同步忽略不存在的文件 | " + normalizedRelative);
                return ConfigSyncOutcome.NoChange;
            }

            ConfigSyncOutcome outcome = ConfigSyncOutcome.NoChange;
            ExecuteLocked(delegate
            {
                RestoreRedundantCopyIfNeeded();
                RestoreSyncStateIfNeeded();
                ConfigSyncState state = LoadState();
                if (state.Files.Count == 0)
                {
                    BootstrapCentralConfig(GetRegisteredVaultPaths(), state);
                }
                outcome = ReconcileOneFile(
                    normalizedVault,
                    normalizedRelative,
                    state,
                    true);
                SaveState(state);
            });
            return outcome;
        }

        public void ApplyToVault(string vaultPath)
        {
            string normalizedVault = Program.NormalizeDirectoryPath(vaultPath);
            ExecuteLocked(delegate
            {
                RestoreRedundantCopyIfNeeded();
                RestoreSyncStateIfNeeded();
                string target = Path.Combine(normalizedVault, ".obsidian");
                Directory.CreateDirectory(target);
                TouchProgrammaticSyncMarker(target);
                try
                {
                    MirrorSafeFiles(_paths.PrimaryCentralConfig, target);
                }
                finally
                {
                    TouchProgrammaticSyncMarker(target);
                }

                ConfigSyncState state = LoadState();
                foreach (string centralFile in EnumerateSafeFiles(
                    _paths.PrimaryCentralConfig))
                {
                    string relative = MakeRelativePath(
                        _paths.PrimaryCentralConfig,
                        centralFile);
                    string hash = HashConfigFile(centralFile, relative);
                    SyncFileState fileState = GetOrCreateFileState(
                        state,
                        relative,
                        hash);
                    fileState.CentralHash = hash;
                    fileState.VaultHashes[normalizedVault] = hash;
                }
                SaveState(state);
            });
        }

        internal void EnsureRedundancy()
        {
            ExecuteLocked(delegate
            {
                RestoreRedundantCopyIfNeeded();
                RestoreSyncStateIfNeeded();
            });
        }

        internal static IEnumerable<string> EnumerateSafeFiles(string configRoot)
        {
            foreach (string fileName in AllowedRootFiles)
            {
                string path = Path.Combine(configRoot, fileName);
                if (File.Exists(path))
                {
                    yield return path;
                }
            }

            foreach (string directoryName in AllowedRootDirectories)
            {
                string directory = Path.Combine(configRoot, directoryName);
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                foreach (string file in Directory.EnumerateFiles(
                    directory,
                    "*",
                    SearchOption.AllDirectories))
                {
                    string relative = MakeRelativePath(configRoot, file)
                        .Replace('/', '\\');
                    if (relative.StartsWith(
                        ManagedPluginPrefix,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    yield return file;
                }
            }
        }

        internal static string NormalizeSafeRelativePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return null;
            }
            string normalized = relativePath.Replace('/', '\\').TrimStart('\\');
            if (Path.IsPathRooted(normalized) ||
                normalized.IndexOf(':') >= 0)
            {
                return null;
            }

            string[] segments = normalized.Split(
                new[] { '\\' },
                StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return null;
            }
            foreach (string segment in segments)
            {
                if (segment == "." || segment == "..")
                {
                    return null;
                }
            }

            if (segments.Length == 1)
            {
                return AllowedRootFiles.Contains(segments[0])
                    ? segments[0]
                    : null;
            }
            return AllowedRootDirectories.Contains(segments[0])
                ? string.Join("\\", segments)
                : null;
        }

        private void BootstrapCentralConfig(
            IList<string> vaultPaths,
            ConfigSyncState state)
        {
            bool centralInitiallyEmpty = !HasSafeFiles(_paths.PrimaryCentralConfig);
            Dictionary<string, FileCandidate> newest =
                new Dictionary<string, FileCandidate>(StringComparer.OrdinalIgnoreCase);

            foreach (string vaultPath in vaultPaths)
            {
                string configPath = Path.Combine(vaultPath, ".obsidian");
                if (!Directory.Exists(configPath))
                {
                    continue;
                }
                foreach (string file in EnumerateSafeFiles(configPath))
                {
                    string relative = MakeRelativePath(configPath, file);
                    if (centralInitiallyEmpty &&
                        string.Equals(relative, "app.json", StringComparison.OrdinalIgnoreCase) &&
                        IsEmptyJsonObject(file))
                    {
                        continue;
                    }

                    FileInfo info = new FileInfo(file);
                    FileCandidate existing;
                    if (!newest.TryGetValue(relative, out existing) ||
                        info.LastWriteTimeUtc > existing.LastWriteTimeUtc)
                    {
                        newest[relative] = new FileCandidate
                        {
                            FullPath = file,
                            VaultPath = vaultPath,
                            LastWriteTimeUtc = info.LastWriteTimeUtc
                        };
                    }
                }
            }

            int updates = 0;
            foreach (KeyValuePair<string, FileCandidate> pair in newest)
            {
                string destination = Path.Combine(_paths.PrimaryCentralConfig, pair.Key);
                FileInfo current = new FileInfo(destination);
                if (!current.Exists || pair.Value.LastWriteTimeUtc > current.LastWriteTimeUtc)
                {
                    CopyFilePreservingTime(pair.Value.FullPath, destination);
                    updates++;
                }
            }

            string appConfig = Path.Combine(_paths.PrimaryCentralConfig, "app.json");
            if (File.Exists(appConfig))
            {
                SanitizeAppConfig(appConfig);
            }
            MirrorSafeFiles(
                _paths.PrimaryCentralConfig,
                _paths.BackupCentralConfig);

            foreach (string centralFile in EnumerateSafeFiles(
                _paths.PrimaryCentralConfig))
            {
                string relative = MakeRelativePath(
                    _paths.PrimaryCentralConfig,
                    centralFile);
                string hash = HashConfigFile(centralFile, relative);
                SyncFileState fileState = GetOrCreateFileState(
                    state,
                    relative,
                    hash);
                fileState.CentralHash = hash;
                FileCandidate candidate;
                fileState.CentralSourceVault = newest.TryGetValue(relative, out candidate)
                    ? candidate.VaultPath
                    : "中央配置";
                fileState.CentralUpdatedUtcTicks =
                    File.GetLastWriteTimeUtc(centralFile).Ticks;
            }

            foreach (string vaultPath in vaultPaths)
            {
                string configPath = Path.Combine(vaultPath, ".obsidian");
                if (!Directory.Exists(configPath))
                {
                    continue;
                }
                foreach (string file in EnumerateSafeFiles(configPath))
                {
                    string relative = MakeRelativePath(configPath, file);
                    string centralFile = Path.Combine(
                        _paths.PrimaryCentralConfig,
                        relative);
                    if (!File.Exists(centralFile))
                    {
                        continue;
                    }
                    SyncFileState fileState = GetOrCreateFileState(
                        state,
                        relative,
                        HashConfigFile(centralFile, relative));
                    fileState.VaultHashes[vaultPath] =
                        HashConfigFile(file, relative);
                }
            }
            ActivityLog.Write("中央配置基线已建立 | 更新文件=" + updates);
        }

        private void ReconcileChangedFiles(
            IList<string> vaultPaths,
            ConfigSyncState state)
        {
            foreach (string vaultPath in vaultPaths)
            {
                string configPath = Path.Combine(vaultPath, ".obsidian");
                if (!Directory.Exists(configPath))
                {
                    continue;
                }
                foreach (string file in EnumerateSafeFiles(configPath))
                {
                    string relative = MakeRelativePath(configPath, file);
                    string currentHash = HashConfigFile(file, relative);
                    SyncFileState fileState;
                    string baseline;
                    if (state.Files.TryGetValue(relative, out fileState) &&
                        fileState.VaultHashes.TryGetValue(vaultPath, out baseline) &&
                        string.Equals(
                            baseline,
                            currentHash,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    ReconcileOneFile(vaultPath, relative, state, false);
                }
            }
        }

        private ConfigSyncOutcome ReconcileOneFile(
            string vaultPath,
            string relative,
            ConfigSyncState state,
            bool realTime)
        {
            string source = GetConfigFilePath(vaultPath, relative);
            if (!File.Exists(source))
            {
                return ConfigSyncOutcome.NoChange;
            }

            string incomingHash = HashConfigFile(source, relative);
            string centralFile = Path.Combine(_paths.PrimaryCentralConfig, relative);
            if (!File.Exists(centralFile))
            {
                AcceptIncoming(vaultPath, relative, source, incomingHash, state);
                return ConfigSyncOutcome.CentralUpdated;
            }

            string centralHash = HashConfigFile(centralFile, relative);
            SyncFileState fileState = GetOrCreateFileState(
                state,
                relative,
                centralHash);
            fileState.CentralHash = centralHash;

            string baseline;
            bool hasBaseline = fileState.VaultHashes.TryGetValue(
                vaultPath,
                out baseline);
            if (string.Equals(
                incomingHash,
                centralHash,
                StringComparison.OrdinalIgnoreCase))
            {
                fileState.VaultHashes[vaultPath] = centralHash;
                return ConfigSyncOutcome.NoChange;
            }

            bool conflict = !hasBaseline ||
                !string.Equals(
                    baseline,
                    centralHash,
                    StringComparison.OrdinalIgnoreCase);
            if (!conflict)
            {
                AcceptIncoming(vaultPath, relative, source, incomingHash, state);
                ActivityLog.Write(
                    (realTime ? "配置实时写入中央 | " : "配置补收写入中央 | ") +
                    relative + " | 来源=" + DisplayVault(vaultPath));
                return ConfigSyncOutcome.CentralUpdated;
            }

            ConfigConflict conflictInfo = new ConfigConflict
            {
                RelativePath = relative,
                IncomingVault = DisplayVault(vaultPath),
                CentralVault = DisplayVault(fileState.CentralSourceVault),
                IncomingModifiedLocal = File.GetLastWriteTime(source),
                CentralModifiedLocal = File.GetLastWriteTime(centralFile)
            };
            ConfigConflictChoice choice = _conflictResolver(conflictInfo);
            if (choice == ConfigConflictChoice.UseIncoming)
            {
                AcceptIncoming(vaultPath, relative, source, incomingHash, state);
                ActivityLog.Write(
                    "配置冲突已选择本次修改 | " + relative +
                    " | 来源=" + DisplayVault(vaultPath));
                return ConfigSyncOutcome.ConflictIncomingChosen;
            }

            CopyFilePreservingTime(centralFile, source);
            fileState.VaultHashes[vaultPath] = centralHash;
            ActivityLog.Write(
                "配置冲突已保留中央版本 | " + relative +
                " | 拒绝来源=" + DisplayVault(vaultPath));
            return ConfigSyncOutcome.ConflictCentralChosen;
        }

        private void AcceptIncoming(
            string vaultPath,
            string relative,
            string source,
            string hash,
            ConfigSyncState state)
        {
            string central = Path.Combine(_paths.PrimaryCentralConfig, relative);
            string backup = Path.Combine(_paths.BackupCentralConfig, relative);
            CopyFilePreservingTime(source, central);
            CopyFilePreservingTime(source, backup);
            if (string.Equals(relative, "app.json", StringComparison.OrdinalIgnoreCase))
            {
                SanitizeAppConfig(central);
                SanitizeAppConfig(backup);
                hash = HashConfigFile(central, relative);
            }

            SyncFileState fileState = GetOrCreateFileState(state, relative, hash);
            fileState.CentralHash = hash;
            fileState.CentralSourceVault = vaultPath;
            fileState.CentralUpdatedUtcTicks = DateTime.UtcNow.Ticks;
            fileState.VaultHashes[vaultPath] = hash;
        }

        private SyncFileState GetOrCreateFileState(
            ConfigSyncState state,
            string relative,
            string centralHash)
        {
            SyncFileState fileState;
            if (!state.Files.TryGetValue(relative, out fileState) || fileState == null)
            {
                fileState = new SyncFileState
                {
                    CentralHash = centralHash,
                    CentralSourceVault = "中央配置",
                    CentralUpdatedUtcTicks = DateTime.UtcNow.Ticks,
                    VaultHashes = new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase)
                };
                state.Files[relative] = fileState;
            }
            if (fileState.VaultHashes == null)
            {
                fileState.VaultHashes = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
            }
            return fileState;
        }

        private void RestoreRedundantCopyIfNeeded()
        {
            bool primaryHasFiles = HasSafeFiles(_paths.PrimaryCentralConfig);
            bool backupHasFiles = HasSafeFiles(_paths.BackupCentralConfig);
            if (!primaryHasFiles && backupHasFiles)
            {
                MirrorSafeFiles(
                    _paths.BackupCentralConfig,
                    _paths.PrimaryCentralConfig);
                ActivityLog.Write("中央配置已从桌面项目备份恢复。");
            }
            else if (primaryHasFiles && !backupHasFiles)
            {
                MirrorSafeFiles(
                    _paths.PrimaryCentralConfig,
                    _paths.BackupCentralConfig);
            }
        }

        private void RestoreSyncStateIfNeeded()
        {
            if (!File.Exists(_paths.SyncStatePath) &&
                File.Exists(_paths.BackupSyncStatePath))
            {
                Directory.CreateDirectory(
                    Path.GetDirectoryName(_paths.SyncStatePath));
                File.Copy(
                    _paths.BackupSyncStatePath,
                    _paths.SyncStatePath,
                    true);
                ActivityLog.Write("配置同步状态已从桌面项目备份恢复。");
            }
            else if (File.Exists(_paths.SyncStatePath) &&
                !File.Exists(_paths.BackupSyncStatePath))
            {
                Directory.CreateDirectory(
                    Path.GetDirectoryName(_paths.BackupSyncStatePath));
                File.Copy(
                    _paths.SyncStatePath,
                    _paths.BackupSyncStatePath,
                    true);
            }
        }

        private ConfigSyncState LoadState()
        {
            ConfigSyncState state = TryLoadState(_paths.SyncStatePath);
            if (state == null)
            {
                state = TryLoadState(_paths.BackupSyncStatePath);
            }
            if (state == null)
            {
                state = new ConfigSyncState();
            }
            if (state.Files == null)
            {
                state.Files = new Dictionary<string, SyncFileState>(
                    StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                state.Files = new Dictionary<string, SyncFileState>(
                    state.Files,
                    StringComparer.OrdinalIgnoreCase);
                foreach (SyncFileState fileState in state.Files.Values)
                {
                    if (fileState.VaultHashes == null)
                    {
                        fileState.VaultHashes = new Dictionary<string, string>(
                            StringComparer.OrdinalIgnoreCase);
                    }
                    else
                    {
                        fileState.VaultHashes = new Dictionary<string, string>(
                            fileState.VaultHashes,
                            StringComparer.OrdinalIgnoreCase);
                    }
                }
            }
            return state;
        }

        private static ConfigSyncState TryLoadState(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = int.MaxValue;
                return serializer.Deserialize<ConfigSyncState>(
                    File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                ActivityLog.Write("读取配置同步状态失败：" + ex.Message);
                return null;
            }
        }

        private void SaveState(ConfigSyncState state)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            string json = serializer.Serialize(state);
            WriteTextAtomically(_paths.SyncStatePath, json);
            Directory.CreateDirectory(
                Path.GetDirectoryName(_paths.BackupSyncStatePath));
            File.Copy(
                _paths.SyncStatePath,
                _paths.BackupSyncStatePath,
                true);
        }

        private static void WriteTextAtomically(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Replace(temporary, path, null, true);
            }
            else
            {
                File.Move(temporary, path);
            }
        }

        private List<string> GetRegisteredVaultPaths()
        {
            List<string> result = new List<string>();
            Dictionary<string, object> root = Program.ReadConfig(
                Program.GetObsidianConfigPath());
            Dictionary<string, object> vaults = Program.GetVaults(root, false);
            if (vaults == null)
            {
                return result;
            }

            foreach (object value in vaults.Values)
            {
                Dictionary<string, object> vault =
                    value as Dictionary<string, object>;
                object pathValue;
                if (vault != null &&
                    vault.TryGetValue("path", out pathValue) &&
                    pathValue is string &&
                    Directory.Exists((string)pathValue))
                {
                    result.Add(Program.NormalizeDirectoryPath((string)pathValue));
                }
            }
            return result;
        }

        private static List<string> NormalizeExistingVaults(
            IEnumerable<string> vaultPaths)
        {
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string vaultPath in vaultPaths)
            {
                if (string.IsNullOrWhiteSpace(vaultPath) ||
                    !Directory.Exists(vaultPath))
                {
                    continue;
                }
                string normalized = Program.NormalizeDirectoryPath(vaultPath);
                if (seen.Add(normalized))
                {
                    result.Add(normalized);
                }
            }
            return result;
        }

        private static void MirrorSafeFiles(
            string sourceRoot,
            string destinationRoot)
        {
            if (!Directory.Exists(sourceRoot))
            {
                return;
            }
            Directory.CreateDirectory(destinationRoot);
            foreach (string source in EnumerateSafeFiles(sourceRoot))
            {
                string relative = MakeRelativePath(sourceRoot, source);
                string destination = Path.Combine(destinationRoot, relative);
                CopyFilePreservingTime(source, destination);
            }
        }

        private static void CopyFilePreservingTime(
            string source,
            string destination)
        {
            string directory = Path.GetDirectoryName(destination);
            Directory.CreateDirectory(directory);
            if (File.Exists(destination))
            {
                FileInfo sourceInfo = new FileInfo(source);
                FileInfo destinationInfo = new FileInfo(destination);
                if (sourceInfo.Length == destinationInfo.Length &&
                    sourceInfo.LastWriteTimeUtc == destinationInfo.LastWriteTimeUtc)
                {
                    return;
                }
            }
            Exception lastError = null;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    File.Copy(source, destination, true);
                    File.SetLastWriteTimeUtc(
                        destination,
                        File.GetLastWriteTimeUtc(source));
                    return;
                }
                catch (IOException ex)
                {
                    lastError = ex;
                }
                catch (UnauthorizedAccessException ex)
                {
                    lastError = ex;
                }
                Thread.Sleep(100);
            }
            throw new IOException(
                "同步 Obsidian 配置文件时持续被占用：" + destination,
                lastError);
        }

        private static void TouchProgrammaticSyncMarker(string configRoot)
        {
            string marker = Path.Combine(configRoot, ProgrammaticSyncMarker);
            File.WriteAllText(
                marker,
                DateTime.UtcNow.ToString("O"),
                new UTF8Encoding(false));
        }

        private static bool HasSafeFiles(string path)
        {
            if (!Directory.Exists(path))
            {
                return false;
            }
            using (IEnumerator<string> enumerator =
                EnumerateSafeFiles(path).GetEnumerator())
            {
                return enumerator.MoveNext();
            }
        }

        private static bool IsEmptyJsonObject(string path)
        {
            try
            {
                string value = File.ReadAllText(path).Trim();
                return value == "{}" || value.Length == 0;
            }
            catch
            {
                return false;
            }
        }

        private static void SanitizeAppConfig(string path)
        {
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> root = serializer.DeserializeObject(
                    File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
                if (root == null)
                {
                    return;
                }
                root.Remove("userIgnoreFilters");
                File.WriteAllText(
                    path,
                    serializer.Serialize(root),
                    new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                ActivityLog.Write("清理中央 app.json 失败：" + ex.Message);
            }
        }

        private string DisplayVault(string vaultPath)
        {
            if (string.IsNullOrWhiteSpace(vaultPath) ||
                string.Equals(vaultPath, "中央配置", StringComparison.Ordinal))
            {
                return "中央配置";
            }
            try
            {
                string normalized = Program.NormalizeDirectoryPath(vaultPath);
                string shadow = Program.NormalizeDirectoryPath(_paths.ShadowRoot) +
                    Path.DirectorySeparatorChar;
                if ((normalized + Path.DirectorySeparatorChar).StartsWith(
                    shadow,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return "共享轻量库";
                }
                return new DirectoryInfo(normalized).Name + "（" + normalized + "）";
            }
            catch
            {
                return vaultPath;
            }
        }

        private static string GetConfigFilePath(
            string vaultPath,
            string relative)
        {
            string configRoot = Path.Combine(vaultPath, ".obsidian");
            string fullPath = Path.GetFullPath(Path.Combine(configRoot, relative));
            string expectedPrefix = Program.NormalizeDirectoryPath(configRoot) +
                Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(
                expectedPrefix,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("配置路径超出仓库范围。");
            }
            return fullPath;
        }

        private static string HashConfigFile(string path, string relative)
        {
            if (string.Equals(
                Path.GetExtension(relative),
                ".json",
                StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    JavaScriptSerializer serializer = new JavaScriptSerializer();
                    serializer.MaxJsonLength = int.MaxValue;
                    object root = serializer.DeserializeObject(
                        File.ReadAllText(path, Encoding.UTF8));
                    Dictionary<string, object> dictionary =
                        root as Dictionary<string, object>;
                    if (dictionary != null && string.Equals(
                        relative,
                        "app.json",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        dictionary.Remove("userIgnoreFilters");
                    }
                    object[] array = root as object[];
                    if (array != null && string.Equals(
                        relative,
                        "community-plugins.json",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        List<object> userPlugins = new List<object>();
                        foreach (object item in array)
                        {
                            if (!string.Equals(
                                item as string,
                                ManagedPluginId,
                                StringComparison.OrdinalIgnoreCase))
                            {
                                userPlugins.Add(item);
                            }
                        }
                        root = userPlugins.ToArray();
                    }
                    return HashBytes(Encoding.UTF8.GetBytes(
                        SerializeCanonicalJson(root, serializer)));
                }
                catch
                {
                }
            }
            return HashFile(path);
        }

        private static string SerializeCanonicalJson(
            object value,
            JavaScriptSerializer serializer)
        {
            Dictionary<string, object> dictionary =
                value as Dictionary<string, object>;
            if (dictionary != null)
            {
                List<string> keys = new List<string>(dictionary.Keys);
                keys.Sort(StringComparer.Ordinal);
                StringBuilder result = new StringBuilder();
                result.Append('{');
                for (int index = 0; index < keys.Count; index++)
                {
                    if (index > 0)
                    {
                        result.Append(',');
                    }
                    string key = keys[index];
                    result.Append(serializer.Serialize(key));
                    result.Append(':');
                    result.Append(SerializeCanonicalJson(
                        dictionary[key],
                        serializer));
                }
                result.Append('}');
                return result.ToString();
            }

            object[] array = value as object[];
            if (array != null)
            {
                StringBuilder result = new StringBuilder();
                result.Append('[');
                for (int index = 0; index < array.Length; index++)
                {
                    if (index > 0)
                    {
                        result.Append(',');
                    }
                    result.Append(SerializeCanonicalJson(array[index], serializer));
                }
                result.Append(']');
                return result.ToString();
            }

            return serializer.Serialize(value);
        }

        private static string HashFile(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.Open(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                return FormatHash(sha.ComputeHash(stream));
            }
        }

        private static string HashBytes(byte[] value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return FormatHash(sha.ComputeHash(value));
            }
        }

        private static string FormatHash(byte[] hash)
        {
            StringBuilder result = new StringBuilder(hash.Length * 2);
            foreach (byte value in hash)
            {
                result.Append(value.ToString("x2"));
            }
            return result.ToString();
        }

        private static string MakeRelativePath(string root, string fullPath)
        {
            string normalizedRoot = Program.NormalizeDirectoryPath(root) +
                Path.DirectorySeparatorChar;
            string normalizedFile = Path.GetFullPath(fullPath);
            if (!normalizedFile.StartsWith(
                normalizedRoot,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("配置文件不在预期目录中。");
            }
            return normalizedFile.Substring(normalizedRoot.Length);
        }

        private static void ExecuteLocked(Action body)
        {
            bool lockTaken = false;
            using (Mutex mutex = new Mutex(false, ConfigSyncMutexName))
            {
                try
                {
                    try
                    {
                        lockTaken = mutex.WaitOne(TimeSpan.FromSeconds(30));
                    }
                    catch (AbandonedMutexException)
                    {
                        lockTaken = true;
                    }
                    if (!lockTaken)
                    {
                        throw new TimeoutException("配置同步正忙，请稍后重试。");
                    }
                    body();
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

        private sealed class FileCandidate
        {
            public string FullPath { get; set; }
            public string VaultPath { get; set; }
            public DateTime LastWriteTimeUtc { get; set; }
        }
    }

    internal sealed class ConfigSyncState
    {
        public ConfigSyncState()
        {
            Files = new Dictionary<string, SyncFileState>(
                StringComparer.OrdinalIgnoreCase);
        }

        public Dictionary<string, SyncFileState> Files { get; set; }
    }

    internal sealed class SyncFileState
    {
        public string CentralHash { get; set; }
        public string CentralSourceVault { get; set; }
        public long CentralUpdatedUtcTicks { get; set; }
        public Dictionary<string, string> VaultHashes { get; set; }
    }
}
