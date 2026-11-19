$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceRoot = Join-Path $projectRoot 'src'
$outputRoot = Join-Path $projectRoot 'bin'
$compiler = Join-Path ([Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw '请使用 Windows PowerShell 5.1 运行此脚本，需要系统自带的 .NET Framework C# 编译器。'
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$sources = Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cs' -File |
    Select-Object -ExpandProperty FullName

& $compiler /nologo /target:winexe /optimize+ `
    "/out:$(Join-Path $outputRoot 'ObsidianMdOpener.exe')" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.Core.dll `
    $sources

if ($LASTEXITCODE -ne 0) {
    throw "编译失败，退出码：$LASTEXITCODE"
}

Write-Host "已生成：$(Join-Path $outputRoot 'ObsidianMdOpener.exe')"
