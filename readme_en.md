# Obsidian MD Opener

[简体中文](readme_ch.md)

**Double-click a Markdown file in Windows File Explorer and Obsidian opens that very note. You’ve found it once. That should be enough.**

A whole project can take ten minutes to load when all you want is one note. If there are too many files, or the folder check alone takes 1.2 seconds, the opener tries a lightweight vault first. Switch to another vault on the same PC using the opener and your hotkeys, appearance, and common plugin settings come with you. I like setting up my shortcuts exactly once.

## Why I built this

I’m a product manager with Markdown files all over my desktop and project folders. I’d find a note in File Explorer, add its folder as an Obsidian vault, then go hunting for the note I’d just been looking at.

My first bright idea was to have the opener make the whole folder a vault for me. Then I tried it on a project with thousands of build files. I wanted to read one note. Twenty minutes later, I’d read exactly zero notes.

That’s how the lightweight vault came about. For a project like that, the opener brings in the note and any attachments it can find. **When you hit Save, your edits go straight into the original file.**

## An example

```text
project/
├── README.md
├── deployment-notes.md
├── output/
│   └── test-plan.md
└── build/               (lots of files)
```

I double-click `README.md` in File Explorer. Those thousands of files in `build/` can wait. If the folder is small to begin with, the opener just uses it as the vault.

Halfway through the README, I want to check the test plan. In Obsidian’s file browser sidebar (labeled `按需文件浏览`), I expand `output/` and click `test-plan.md`. If I already know I want both notes, I can select them together in File Explorer and choose **Open**.

## Installation

1. Install Obsidian on Windows and launch it once.
2. [Download the Windows opener](https://github.com/95490164/Obsidian-MD-Opener/releases/latest/download/ObsidianMdOpener.exe) and keep `ObsidianMdOpener.exe` in a folder where it can stay.
3. In File Explorer, right-click any `.md` file. Choose **Open with → Choose another app → Choose an app on your PC**, select the EXE, and make it the default app for `.md` files. From then on, just double-click a note.

Prefer to build it yourself? Follow the [Windows PowerShell build and test steps (in Chinese)](CONTRIBUTING.md).

**Can’t find the sidebar?** The opener installs its file browser plugin automatically. Click the folder-tree icon on Obsidian’s left ribbon. If the icon is missing, open **Settings → Community plugins → Installed plugins** in the current vault and check that `按需文件浏览` is enabled. That’s the name you’ll see in the plugin list.

**Vault registration failed on the first try?** Quit Obsidian from its icon in the Windows system tray, then double-click the original note again.

See the [usage guide (in Chinese)](CONTRIBUTING.md) for attachments, opening files across drives, and settings sync. This independent project uses your local Obsidian installation and is released under the [MIT License](LICENSE).
