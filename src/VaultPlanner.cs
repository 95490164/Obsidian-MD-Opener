using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ObsidianMdOpener
{
    internal sealed class VaultPlan
    {
        public bool UseShadowVault { get; set; }
        public int EntriesInspected { get; set; }
        public int MarkdownFilesInspected { get; set; }
        public long InspectionMilliseconds { get; set; }
        public string Reason { get; set; }
    }

    internal static class VaultPlanner
    {
        internal const int EntryBudget = 4000;
        internal const int MarkdownBudget = 1200;
        internal const int TimeBudgetMilliseconds = 1200;

        public static VaultPlan Create(string markdownPath, AppPaths paths)
        {
            string parent = Program.NormalizeDirectoryPath(
                Path.GetDirectoryName(markdownPath));
            if (IsInside(parent, paths.ShadowRoot))
            {
                return new VaultPlan
                {
                    UseShadowVault = false,
                    Reason = "文件已经位于轻量仓库中"
                };
            }

            Stopwatch timer = Stopwatch.StartNew();
            int entries = 0;
            int markdown = 0;
            bool inaccessible = false;
            Stack<string> pending = new Stack<string>();
            pending.Push(parent);

            while (pending.Count > 0)
            {
                if (timer.ElapsedMilliseconds >= TimeBudgetMilliseconds)
                {
                    return Build(true, entries, markdown, timer, "目录检查超过时间预算");
                }

                string current = pending.Pop();
                try
                {
                    foreach (string file in Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly))
                    {
                        entries++;
                        if (string.Equals(
                            Path.GetExtension(file),
                            ".md",
                            StringComparison.OrdinalIgnoreCase))
                        {
                            markdown++;
                        }
                        if (entries >= EntryBudget)
                        {
                            return Build(true, entries, markdown, timer, "文件和目录数量达到检查预算");
                        }
                        if (markdown >= MarkdownBudget)
                        {
                            return Build(true, entries, markdown, timer, "Markdown 数量达到索引预算");
                        }
                        if (timer.ElapsedMilliseconds >= TimeBudgetMilliseconds)
                        {
                            return Build(true, entries, markdown, timer, "目录检查超过时间预算");
                        }
                    }

                    foreach (string directory in Directory.EnumerateDirectories(
                        current,
                        "*",
                        SearchOption.TopDirectoryOnly))
                    {
                        entries++;
                        if (entries >= EntryBudget)
                        {
                            return Build(true, entries, markdown, timer, "文件和目录数量达到检查预算");
                        }

                        try
                        {
                            FileAttributes attributes = File.GetAttributes(directory);
                            if ((attributes & FileAttributes.ReparsePoint) == 0)
                            {
                                pending.Push(directory);
                            }
                        }
                        catch
                        {
                            inaccessible = true;
                        }
                    }
                }
                catch
                {
                    inaccessible = true;
                }
            }

            timer.Stop();
            if (inaccessible)
            {
                return Build(true, entries, markdown, timer, "目录存在无法可靠检查的内容");
            }
            return Build(false, entries, markdown, timer, "目录规模在实测预算内");
        }

        private static VaultPlan Build(
            bool useShadow,
            int entries,
            int markdown,
            Stopwatch timer,
            string reason)
        {
            timer.Stop();
            return new VaultPlan
            {
                UseShadowVault = useShadow,
                EntriesInspected = entries,
                MarkdownFilesInspected = markdown,
                InspectionMilliseconds = timer.ElapsedMilliseconds,
                Reason = reason
            };
        }

        private static bool IsInside(string path, string root)
        {
            string normalizedPath = Program.NormalizeDirectoryPath(path) +
                Path.DirectorySeparatorChar;
            string normalizedRoot = Program.NormalizeDirectoryPath(root) +
                Path.DirectorySeparatorChar;
            return normalizedPath.StartsWith(
                normalizedRoot,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class Diagnostics
    {
        public static int PrintPlan(string inputPath)
        {
            try
            {
                string fullPath = Program.ValidateMarkdownPath(inputPath);
                AppPaths paths = AppPaths.Create();
                VaultPlan plan = VaultPlanner.Create(fullPath, paths);
                Console.OutputEncoding = Encoding.UTF8;
                Console.WriteLine("文件：" + fullPath);
                Console.WriteLine("模式：" + (plan.UseShadowVault ? "轻量仓库" : "直接父目录仓库"));
                Console.WriteLine("原因：" + plan.Reason);
                Console.WriteLine("已检查条目：" + plan.EntriesInspected);
                Console.WriteLine("已检查 Markdown：" + plan.MarkdownFilesInspected);
                Console.WriteLine("检查耗时毫秒：" + plan.InspectionMilliseconds);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }
    }
}
