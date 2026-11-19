using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace ObsidianMdOpener
{
    internal sealed class ShadowVaultResult
    {
        public bool Succeeded { get; set; }
        public string VaultPath { get; set; }
        public string OpenPath { get; set; }
        public string Error { get; set; }
    }

    internal static class ShadowVault
    {
        private const int SymbolicLinkFlagFile = 0;
        private const int SymbolicLinkFlagAllowUnprivilegedCreate = 2;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(
            string fileName,
            string existingFileName,
            IntPtr securityAttributes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateSymbolicLink(
            string symbolicFileName,
            string targetFileName,
            int flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle fileHandle,
            out ByHandleFileInformation fileInformation);

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        public static ShadowVaultResult Prepare(string sourcePath, AppPaths paths)
        {
            string vaultPath = GetSharedVaultPath(paths);
            try
            {
                Directory.CreateDirectory(vaultPath);
                string openPath = GetMirrorPath(vaultPath, sourcePath);
                EnsureLinkedFile(sourcePath, openPath, vaultPath);
                MirrorReferencedFiles(sourcePath, vaultPath);
                return new ShadowVaultResult
                {
                    Succeeded = true,
                    VaultPath = vaultPath,
                    OpenPath = openPath
                };
            }
            catch (Exception ex)
            {
                return new ShadowVaultResult
                {
                    Succeeded = false,
                    VaultPath = vaultPath,
                    Error = ex.Message
                };
            }
        }

        private static string GetSharedVaultPath(AppPaths paths)
        {
            string pointer = Path.Combine(paths.StateRoot, "shared-vault.txt");
            try
            {
                if (File.Exists(pointer))
                {
                    string saved = File.ReadAllText(pointer, Encoding.UTF8).Trim();
                    if (Directory.Exists(saved) && IsInside(saved, paths.ShadowRoot))
                    {
                        return saved;
                    }
                }

                DirectoryInfo newest = null;
                foreach (string directory in Directory.EnumerateDirectories(paths.ShadowRoot))
                {
                    string plugin = Path.Combine(
                        directory,
                        ".obsidian",
                        "plugins",
                        "obsidian-md-opener-browser",
                        "manifest.json");
                    if (!File.Exists(plugin))
                    {
                        continue;
                    }
                    DirectoryInfo info = new DirectoryInfo(directory);
                    if (newest == null || info.LastWriteTimeUtc > newest.LastWriteTimeUtc)
                    {
                        newest = info;
                    }
                }

                string selected = newest == null
                    ? Path.Combine(paths.ShadowRoot, "shared")
                    : newest.FullName;
                Directory.CreateDirectory(selected);
                File.WriteAllText(pointer, selected, new UTF8Encoding(false));
                return selected;
            }
            catch
            {
                string fallback = Path.Combine(paths.ShadowRoot, "shared");
                Directory.CreateDirectory(fallback);
                return fallback;
            }
        }

        internal static string GetMirrorPath(string vaultPath, string sourcePath)
        {
            string fullPath = Path.GetFullPath(sourcePath);
            string root = Path.GetPathRoot(fullPath);
            string rootKey = SanitizeSegment(root.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(rootKey))
            {
                rootKey = "root";
            }
            string relative = fullPath.Substring(root.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.Combine(vaultPath, "content", rootKey, relative);
        }

        private static void EnsureLinkedFile(
            string sourcePath,
            string destinationPath,
            string vaultPath)
        {
            string destinationDirectory = Path.GetDirectoryName(destinationPath);
            Directory.CreateDirectory(destinationDirectory);

            if (File.Exists(destinationPath))
            {
                if (!IsInside(destinationPath, vaultPath))
                {
                    throw new InvalidOperationException("轻量仓库目标超出安全目录。");
                }
                if (AreSameFile(sourcePath, destinationPath))
                {
                    return;
                }
                File.Delete(destinationPath);
            }

            if (CreateHardLink(destinationPath, sourcePath, IntPtr.Zero))
            {
                return;
            }

            int hardLinkError = Marshal.GetLastWin32Error();
            if (CreateSymbolicLink(
                destinationPath,
                sourcePath,
                SymbolicLinkFlagFile | SymbolicLinkFlagAllowUnprivilegedCreate))
            {
                return;
            }

            int symbolicLinkError = Marshal.GetLastWin32Error();
            throw new IOException(
                "无法为原文件建立轻量映射。硬链接错误 " + hardLinkError +
                "，符号链接错误 " + symbolicLinkError + "。");
        }

        private static bool AreSameFile(string firstPath, string secondPath)
        {
            try
            {
                FileShare share = FileShare.ReadWrite | FileShare.Delete;
                using (FileStream first = new FileStream(
                    firstPath,
                    FileMode.Open,
                    FileAccess.Read,
                    share))
                using (FileStream second = new FileStream(
                    secondPath,
                    FileMode.Open,
                    FileAccess.Read,
                    share))
                {
                    ByHandleFileInformation firstInfo;
                    ByHandleFileInformation secondInfo;
                    return GetFileInformationByHandle(
                            first.SafeFileHandle,
                            out firstInfo) &&
                        GetFileInformationByHandle(
                            second.SafeFileHandle,
                            out secondInfo) &&
                        firstInfo.VolumeSerialNumber ==
                            secondInfo.VolumeSerialNumber &&
                        firstInfo.FileIndexHigh == secondInfo.FileIndexHigh &&
                        firstInfo.FileIndexLow == secondInfo.FileIndexLow;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void MirrorReferencedFiles(string markdownPath, string vaultPath)
        {
            FileInfo markdown = new FileInfo(markdownPath);
            if (markdown.Length > 16 * 1024 * 1024)
            {
                return;
            }

            string text = File.ReadAllText(markdownPath, Encoding.UTF8);
            HashSet<string> candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddMatches(candidates, text, @"!?\[[^\]]*\]\((?<value>[^\)]+)\)");
            AddMatches(candidates, text, @"(?:src|href)\s*=\s*[\""'](?<value>[^\""']+)[\""']");
            AddMatches(candidates, text, @"!?\[\[(?<value>[^\]]+)\]\]");

            string sourceDirectory = Path.GetDirectoryName(markdownPath);
            foreach (string raw in candidates)
            {
                string target = NormalizeLinkTarget(raw);
                if (string.IsNullOrEmpty(target) || IsExternalTarget(target))
                {
                    continue;
                }

                string resolved;
                try
                {
                    resolved = Path.IsPathRooted(target)
                        ? Path.GetFullPath(target)
                        : Path.GetFullPath(Path.Combine(sourceDirectory, target));
                }
                catch
                {
                    continue;
                }

                if (!File.Exists(resolved) && string.IsNullOrEmpty(Path.GetExtension(resolved)))
                {
                    string markdownCandidate = resolved + ".md";
                    if (File.Exists(markdownCandidate))
                    {
                        resolved = markdownCandidate;
                    }
                }

                if (!File.Exists(resolved))
                {
                    continue;
                }

                string destination = GetMirrorPath(vaultPath, resolved);
                EnsureLinkedFile(resolved, destination, vaultPath);
            }
        }

        private static void AddMatches(
            HashSet<string> candidates,
            string text,
            string pattern)
        {
            foreach (Match match in Regex.Matches(
                text,
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                Group value = match.Groups["value"];
                if (value.Success)
                {
                    candidates.Add(value.Value);
                }
            }
        }

        private static string NormalizeLinkTarget(string raw)
        {
            string target = raw.Trim();
            int pipe = target.IndexOf('|');
            if (pipe >= 0)
            {
                target = target.Substring(0, pipe);
            }
            int heading = target.IndexOf('#');
            if (heading >= 0)
            {
                target = target.Substring(0, heading);
            }
            if (target.StartsWith("<", StringComparison.Ordinal) &&
                target.EndsWith(">", StringComparison.Ordinal))
            {
                target = target.Substring(1, target.Length - 2);
            }
            else
            {
                Match titled = Regex.Match(target, @"^(?<path>.+?)\s+[\""'].*[\""']$");
                if (titled.Success)
                {
                    target = titled.Groups["path"].Value;
                }
            }
            try
            {
                target = Uri.UnescapeDataString(target);
            }
            catch
            {
            }
            return target.Trim();
        }

        private static bool IsExternalTarget(string target)
        {
            return target.StartsWith("#", StringComparison.Ordinal) ||
                   target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                   target.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                   target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                   target.StartsWith("obsidian:", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsInside(string path, string root)
        {
            string normalizedPath = Path.GetFullPath(path);
            string normalizedRoot = Program.NormalizeDirectoryPath(root) +
                Path.DirectorySeparatorChar;
            return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static string SanitizeSegment(string value)
        {
            StringBuilder result = new StringBuilder(value.Length);
            foreach (char character in value)
            {
                if (char.IsLetterOrDigit(character) || character == '-' || character == '_')
                {
                    result.Append(character);
                }
            }
            return result.ToString();
        }
    }
}
