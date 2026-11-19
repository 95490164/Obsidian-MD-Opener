using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ObsidianMdOpener
{
    internal static class LazyBrowserPlugin
    {
        private const string PluginId = "obsidian-md-opener-browser";

        private const string Manifest =
            "{\n" +
            "  \"id\": \"obsidian-md-opener-browser\",\n" +
            "  \"name\": \"按需文件浏览\",\n" +
            "  \"version\": \"1.1.3\",\n" +
            "  \"minAppVersion\": \"1.5.0\",\n" +
            "  \"description\": \"按需读取目录、在共享轻量库中打开 Markdown，并实时记录配置修改。\",\n" +
            "  \"author\": \"Obsidian MD Opener\",\n" +
            "  \"isDesktopOnly\": true\n" +
            "}\n";

        private const string Styles =
            ".omdo-browser-header{padding:8px 10px;border-bottom:1px solid var(--background-modifier-border);}" +
            ".omdo-browser-title{font-weight:600;margin-bottom:4px;}" +
            ".omdo-browser-root{font-size:11px;color:var(--text-muted);word-break:break-all;}" +
            ".omdo-browser-actions{margin-top:8px;}" +
            ".omdo-browser-tree{padding:6px 4px;overflow:auto;}" +
            ".omdo-browser-row{display:flex;align-items:center;gap:5px;min-height:28px;padding:2px 5px;border-radius:5px;cursor:pointer;}" +
            ".omdo-browser-row:hover{background:var(--background-modifier-hover);}" +
            ".omdo-browser-icon{width:16px;height:16px;flex:0 0 16px;}" +
            ".omdo-browser-name{overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}" +
            ".omdo-browser-error{padding:8px;color:var(--text-error);}";

        private const string MainScript = @"const { Plugin, ItemView, Notice, setIcon } = require('obsidian');
const fs = require('fs');
const path = require('path');
const childProcess = require('child_process');

const VIEW_TYPE = 'omdo-lazy-browser';
const CONFIG_ROOT_FILES = new Set([
  'app.json', 'appearance.json', 'community-plugins.json',
  'core-plugins.json', 'hotkeys.json', 'templates.json',
  'types.json', 'graph.json'
]);
const CONFIG_ROOT_DIRS = new Set(['plugins', 'themes', 'snippets']);
const MANAGED_PLUGIN_PREFIX = 'plugins/obsidian-md-opener-browser/';
const PROGRAMMATIC_SYNC_MARKER = '.obsidian-md-opener-sync';

class LazyBrowserView extends ItemView {
  constructor(leaf, plugin) {
    super(leaf);
    this.plugin = plugin;
  }

  getViewType() { return VIEW_TYPE; }
  getDisplayText() { return '按需文件浏览'; }
  getIcon() { return 'folder-tree'; }

  async onOpen() {
    await this.renderBrowser();
  }

  async renderBrowser() {
    const container = this.containerEl.children[1];
    container.empty();
    const header = container.createDiv({ cls: 'omdo-browser-header' });
    header.createDiv({ cls: 'omdo-browser-title', text: '按需文件浏览' });
    header.createDiv({ cls: 'omdo-browser-root', text: this.plugin.settings.sourceRoot || '未设置目录' });
    const actions = header.createDiv({ cls: 'omdo-browser-actions' });
    const refresh = actions.createEl('button', { text: '刷新当前层级' });
    refresh.addEventListener('click', () => this.renderBrowser());
    const tree = container.createDiv({ cls: 'omdo-browser-tree' });
    if (!this.plugin.settings.sourceRoot) {
      tree.createDiv({ cls: 'omdo-browser-error', text: '没有可浏览的原始目录。' });
      return;
    }
    await this.renderDirectory(this.plugin.settings.sourceRoot, tree, 0);
  }

  async renderDirectory(directory, container, depth) {
    let entries;
    try {
      entries = await fs.promises.readdir(directory, { withFileTypes: true });
    } catch (error) {
      container.createDiv({ cls: 'omdo-browser-error', text: '无法读取：' + error.message });
      return;
    }

    entries.sort((a, b) => {
      if (a.isDirectory() !== b.isDirectory()) return a.isDirectory() ? -1 : 1;
      return a.name.localeCompare(b.name, 'zh-CN', { numeric: true });
    });

    for (const entry of entries) {
      if (entry.name === '.obsidian') continue;
      const fullPath = path.join(directory, entry.name);
      const row = container.createDiv({ cls: 'omdo-browser-row' });
      row.style.paddingLeft = (6 + depth * 14) + 'px';
      const icon = row.createSpan({ cls: 'omdo-browser-icon' });
      setIcon(icon, entry.isDirectory() ? 'folder-closed' : (entry.name.toLowerCase().endsWith('.md') ? 'file-text' : 'file'));
      row.createSpan({ cls: 'omdo-browser-name', text: entry.name, attr: { title: fullPath } });

      if (entry.isDirectory()) {
        let child = null;
        row.addEventListener('click', async () => {
          if (child) {
            child.toggleAttribute('hidden');
            setIcon(icon, child.hasAttribute('hidden') ? 'folder-closed' : 'folder-open');
            return;
          }
          child = container.createDiv();
          row.insertAdjacentElement('afterend', child);
          setIcon(icon, 'folder-open');
          await this.renderDirectory(fullPath, child, depth + 1);
        });
      } else if (entry.name.toLowerCase().endsWith('.md')) {
        row.addEventListener('click', () => this.plugin.openMarkdown(fullPath));
      }
    }
  }
}

module.exports = class LazyBrowserPlugin extends Plugin {
  async onload() {
    this.settings = Object.assign({ sourceRoot: '', openerPath: '', vaultPath: '' }, await this.loadData());
    this.configDebounce = new Map();
    this.registerView(VIEW_TYPE, leaf => new LazyBrowserView(leaf, this));
    this.addRibbonIcon('folder-tree', '按需文件浏览', () => this.activateView());
    this.app.workspace.onLayoutReady(() => this.activateView());
    this.startConfigWatcher();
  }

  async activateView() {
    let leaf = this.app.workspace.getLeavesOfType(VIEW_TYPE)[0];
    if (!leaf) {
      leaf = this.app.workspace.getLeftLeaf(false);
      await leaf.setViewState({ type: VIEW_TYPE, active: true });
    }
    this.app.workspace.revealLeaf(leaf);
  }

  async openMarkdown(fullPath) {
    if (!this.settings.openerPath || !fs.existsSync(this.settings.openerPath)) {
      new Notice('找不到 Obsidian 智能打开器。');
      return;
    }
    try {
      const vaultRoot = this.settings.vaultPath || this.app.vault.adapter.basePath;
      const relative = path.relative(vaultRoot, fullPath);
      if (relative && !relative.startsWith('..') && !path.isAbsolute(relative)) {
        const vaultRelative = relative.split(path.sep).join('/');
        const file = this.app.vault.getAbstractFileByPath(vaultRelative);
        if (file && file.extension === 'md') {
          await this.app.workspace.getLeaf('tab').openFile(file);
          return;
        }
      }

      const child = childProcess.spawn(this.settings.openerPath, [
        '--prepare-from-browser', fullPath, vaultRoot,
        this.settings.sourceRoot || path.dirname(fullPath)
      ], {
        detached: false,
        stdio: ['ignore', 'pipe', 'pipe'],
        windowsHide: true
      });
      let output = '';
      let errors = '';
      child.stdout.setEncoding('utf8');
      child.stderr.setEncoding('utf8');
      child.stdout.on('data', value => { output += value; });
      child.stderr.on('data', value => { errors += value; });
      child.on('error', error => {
        new Notice('无法启动 Markdown 打开器：' + error.message);
      });
      child.on('close', async code => {
        try {
          if (code !== 0) {
            throw new Error(errors.trim() || '打开器返回错误代码 ' + code);
          }
          const lines = output.trim().split(/\r?\n/).filter(Boolean);
          const mappedPath = lines.length ? lines[lines.length - 1] : '';
          const relative = path.relative(vaultRoot, mappedPath);
          if (!mappedPath || !relative || relative.startsWith('..') || path.isAbsolute(relative)) {
            throw new Error('打开器没有返回有效的仓库内路径。');
          }
          const vaultRelative = relative.split(path.sep).join('/');
          const adapter = this.app.vault.adapter;
          if (typeof adapter.reconcileInternalFile === 'function') {
            await adapter.reconcileInternalFile(vaultRelative);
          }
          let file = this.app.vault.getAbstractFileByPath(vaultRelative);
          for (let attempt = 0; !file && attempt < 20; attempt++) {
            await new Promise(resolve => setTimeout(resolve, 50));
            file = this.app.vault.getAbstractFileByPath(vaultRelative);
          }
          if (!file || file.extension !== 'md') {
            throw new Error('映射文件已创建，但 Obsidian 尚未识别该文件。');
          }
          await this.app.workspace.getLeaf('tab').openFile(file);
        } catch (error) {
          new Notice('无法打开 Markdown：' + error.message);
        }
      });
    } catch (error) {
      new Notice('无法打开 Markdown：' + error.message);
    }
  }

  startConfigWatcher() {
    const vaultRoot = this.settings.vaultPath || this.app.vault.adapter.basePath;
    const configRoot = path.join(vaultRoot, '.obsidian');
    if (!fs.existsSync(configRoot)) return;
    try {
      const watcher = fs.watch(configRoot, { recursive: true }, (eventType, fileName) => {
        if (!fileName) return;
        const relative = String(fileName).split(path.sep).join('/');
        if (!this.isSyncableConfig(relative)) return;
        const changedPath = path.join(configRoot, relative);
        try {
          if (!fs.statSync(changedPath).isFile()) return;
        } catch (_) {
          return;
        }
        const existing = this.configDebounce.get(relative);
        if (existing) clearTimeout(existing);
        const timer = setTimeout(() => {
          this.configDebounce.delete(relative);
          if (this.wasWrittenByOpener(configRoot, relative)) return;
          this.syncConfig(vaultRoot, relative);
        }, 700);
        this.configDebounce.set(relative, timer);
      });
      this.register(() => watcher.close());
    } catch (error) {
      console.error('Obsidian MD Opener 配置监听失败', error);
    }
  }

  isSyncableConfig(relative) {
    const normalized = relative.replace(/\\/g, '/').replace(/^\/+/, '');
    if (normalized.startsWith(MANAGED_PLUGIN_PREFIX)) return false;
    const parts = normalized.split('/');
    if (parts.length === 1) return CONFIG_ROOT_FILES.has(parts[0]);
    return CONFIG_ROOT_DIRS.has(parts[0]);
  }

  wasWrittenByOpener(configRoot, relative) {
    try {
      const marker = path.join(configRoot, PROGRAMMATIC_SYNC_MARKER);
      const changed = path.join(configRoot, relative);
      if (!fs.existsSync(marker) || !fs.existsSync(changed)) return false;
      return fs.statSync(changed).mtimeMs <= fs.statSync(marker).mtimeMs + 1000;
    } catch (_) {
      return false;
    }
  }

  syncConfig(vaultRoot, relative) {
    if (!this.settings.openerPath || !fs.existsSync(this.settings.openerPath)) return;
    try {
      childProcess.spawn(this.settings.openerPath, [
        '--sync-config', vaultRoot, relative
      ], {
        detached: true,
        stdio: 'ignore',
        windowsHide: false
      }).unref();
    } catch (error) {
      console.error('Obsidian MD Opener 配置同步失败', error);
    }
  }

  onunload() {
    for (const timer of this.configDebounce.values()) clearTimeout(timer);
    this.configDebounce.clear();
    this.app.workspace.detachLeavesOfType(VIEW_TYPE);
  }
};
";

        public static void Install(
            string vaultPath,
            string sourceRoot,
            string openerPath)
        {
            string configRoot = Path.Combine(vaultPath, ".obsidian");
            string pluginRoot = Path.Combine(configRoot, "plugins", PluginId);
            Directory.CreateDirectory(pluginRoot);
            WriteIfDifferent(Path.Combine(pluginRoot, "manifest.json"), Manifest);
            WriteIfDifferent(Path.Combine(pluginRoot, "main.js"), MainScript);
            WriteIfDifferent(Path.Combine(pluginRoot, "styles.css"), Styles);

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            string data = serializer.Serialize(new Dictionary<string, object>
            {
                { "sourceRoot", Program.NormalizeDirectoryPath(sourceRoot) },
                { "openerPath", Path.GetFullPath(openerPath) },
                { "vaultPath", Program.NormalizeDirectoryPath(vaultPath) }
            });
            WriteIfDifferent(Path.Combine(pluginRoot, "data.json"), data);
            EnablePlugin(configRoot);
        }

        private static void EnablePlugin(string configRoot)
        {
            string path = Path.Combine(configRoot, "community-plugins.json");
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            List<object> plugins = new List<object>();
            if (File.Exists(path))
            {
                try
                {
                    object[] values = serializer.DeserializeObject(
                        ReadAllTextWithRetry(path)) as object[];
                    if (values != null)
                    {
                        plugins.AddRange(values);
                    }
                }
                catch
                {
                }
            }

            bool found = false;
            foreach (object plugin in plugins)
            {
                if (string.Equals(
                    plugin as string,
                    PluginId,
                    StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                plugins.Add(PluginId);
                WriteAllTextWithRetry(
                    path,
                    serializer.Serialize(plugins.ToArray()));
            }
        }

        private static void WriteIfDifferent(string path, string content)
        {
            if (File.Exists(path) &&
                string.Equals(
                    ReadAllTextWithRetry(path),
                    content,
                    StringComparison.Ordinal))
            {
                return;
            }
            WriteAllTextWithRetry(path, content);
        }

        private static string ReadAllTextWithRetry(string path)
        {
            Exception lastError = null;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    return File.ReadAllText(path, Encoding.UTF8);
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
                "读取 Obsidian 配置文件时持续被占用：" + path,
                lastError);
        }

        private static void WriteAllTextWithRetry(string path, string content)
        {
            Exception lastError = null;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    File.WriteAllText(
                        path,
                        content,
                        new UTF8Encoding(false));
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
                "写入 Obsidian 配置文件时持续被占用：" + path,
                lastError);
        }
    }
}
