using System;
using System.IO;
using System.Text;

namespace ObsidianMdOpener
{
    internal sealed class AppPaths
    {
        public string ProjectRoot { get; private set; }
        public string StateRoot { get; private set; }
        public string ShadowRoot { get; private set; }
        public string PrimaryCentralConfig { get; private set; }
        public string BackupCentralConfig { get; private set; }
        public string SyncStatePath { get; private set; }
        public string BackupSyncStatePath { get; private set; }
        public string LogPath { get; private set; }

        public static AppPaths Create()
        {
            string stateRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ObsidianMdOpener");
            string localRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ObsidianMdOpener");
            string backupRoot = Path.Combine(localRoot, "backup");
            string logRoot = Path.Combine(stateRoot, "logs");

            AppPaths result = new AppPaths
            {
                ProjectRoot = localRoot,
                StateRoot = stateRoot,
                ShadowRoot = Path.Combine(localRoot, "vaults"),
                PrimaryCentralConfig = Path.Combine(stateRoot, "central-config"),
                BackupCentralConfig = Path.Combine(
                    backupRoot,
                    "central-config-backup"),
                SyncStatePath = Path.Combine(stateRoot, "sync-state.json"),
                BackupSyncStatePath = Path.Combine(
                    backupRoot,
                    "sync-state-backup.json"),
                LogPath = Path.Combine(logRoot, "activity.log")
            };

            Directory.CreateDirectory(result.StateRoot);
            Directory.CreateDirectory(result.ShadowRoot);
            Directory.CreateDirectory(result.PrimaryCentralConfig);
            Directory.CreateDirectory(result.BackupCentralConfig);
            Directory.CreateDirectory(logRoot);
            return result;
        }

        internal static AppPaths CreateForTest(string root)
        {
            AppPaths result = new AppPaths
            {
                ProjectRoot = Path.Combine(root, "project"),
                StateRoot = Path.Combine(root, "state"),
                ShadowRoot = Path.Combine(root, "shadows"),
                PrimaryCentralConfig = Path.Combine(root, "state", "central-config"),
                BackupCentralConfig = Path.Combine(root, "project", "data", "central-config-backup"),
                SyncStatePath = Path.Combine(root, "state", "sync-state.json"),
                BackupSyncStatePath = Path.Combine(root, "project", "data", "sync-state-backup.json"),
                LogPath = Path.Combine(root, "project", "logs", "activity.log")
            };
            Directory.CreateDirectory(result.ProjectRoot);
            Directory.CreateDirectory(result.StateRoot);
            Directory.CreateDirectory(result.ShadowRoot);
            Directory.CreateDirectory(result.PrimaryCentralConfig);
            Directory.CreateDirectory(result.BackupCentralConfig);
            Directory.CreateDirectory(Path.GetDirectoryName(result.LogPath));
            return result;
        }
    }

    internal static class ActivityLog
    {
        private static readonly object Sync = new object();
        private static string _path;

        public static void Initialize(AppPaths paths)
        {
            _path = paths.LogPath;
        }

        public static void Write(string message)
        {
            if (string.IsNullOrEmpty(_path))
            {
                return;
            }

            lock (Sync)
            {
                try
                {
                    FileInfo info = new FileInfo(_path);
                    if (info.Exists && info.Length > 2 * 1024 * 1024)
                    {
                        string archive = Path.Combine(
                            info.DirectoryName,
                            "activity.previous.log");
                        if (File.Exists(archive))
                        {
                            File.Delete(archive);
                        }
                        File.Move(_path, archive);
                    }

                    File.AppendAllText(
                        _path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | " +
                        message + Environment.NewLine,
                        new UTF8Encoding(false));
                }
                catch
                {
                }
            }
        }
    }
}
