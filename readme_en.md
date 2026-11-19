# Obsidian MD Opener

[简体中文](readme_ch.md)

**Double-click a Markdown file in Windows File Explorer to open the original file in Obsidian. You can skip creating a vault by hand and hunting down the same note a second time.**
If a project has too many files, or checking its folders takes more than 1.2 seconds, the opener tries a lightweight vault first. That can spare you a long wait for the whole project to load (think ten minutes =-=).
When you use the opener to switch between vaults on the same PC, it automatically syncs your hotkeys, appearance, and common plugin settings. You only have to set them up once.

## Why I built this

I’m a product manager (kind of a lousy one), and I use Obsidian to read Markdown files scattered across my desktop and project folders. Sometimes I’d already found the note I wanted in File Explorer, but its folder wasn’t an Obsidian vault yet. I’d have to open the folder as a vault and then find the exact same note all over again.

The first version opened the note’s folder as a vault for me. That worked until I opened a project with thousands of build files. I just wanted to read one document, and 20 minutes later the damn thing still hadn’t opened. So I added a lightweight vault. Obsidian can skip files that have nothing to do with the note, and when I hit Save, my edits still go into the original file.

## An example

Say you want to read the `README.md` in this project.

```text
project/
├── README.md
├── deployment-notes.md
├── output/
│   └── test-plan.md
└── build/               (lots of files)
```

Double-click `README.md` in File Explorer. With all those build files, the opener links the note and any attachments it can find into a lightweight vault. Obsidian can open the note without scanning the entire project. For a note in a small folder, the opener uses that folder as the vault.

Want to read the test plan next? In Obsidian’s on-demand file browser (`按需文件浏览`), expand `output/` and click `test-plan.md`.

To open several notes at once, select them in File Explorer and choose **Open**.

## Installation

For Windows. Install Obsidian and launch it at least once before you start.

1. Open the project folder in **Windows PowerShell** and run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`. The script creates `bin\ObsidianMdOpener.exe`. You can move it to a permanent location before the next step.
2. In File Explorer, right-click any `.md` file and choose **Open with → Choose another app → Choose an app on your PC**. Select the EXE and set it as the default app for `.md` files.
3. Double-click a `.md` file in File Explorer. Obsidian opens that note.

**Want to browse nearby files in the sidebar?** The opener installs its file browser plugin (`按需文件浏览`) automatically. Click the folder-tree icon on Obsidian’s left ribbon. If the icon is missing, open **Settings → Community plugins → Installed plugins** in the current vault and check that `按需文件浏览` is enabled.

**Vault registration failed on the first try?** In the Windows system tray, right-click the Obsidian icon and quit the app. Then double-click the original `.md` file again.

For details on attachments, settings sync, and working across drives, see the [build, test, and usage guide (in Chinese)](CONTRIBUTING.md).

This is an independent project that uses your local installation of Obsidian. [MIT License](LICENSE).
