$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceRoot = Join-Path $projectRoot 'src'
$outputRoot = Join-Path $projectRoot 'test-output'
$compiler = Join-Path ([Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw '请使用 Windows PowerShell 5.1 运行此脚本，需要系统自带的 .NET Framework C# 编译器。'
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$sources = Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cs' -File |
    Select-Object -ExpandProperty FullName
$testExecutable = Join-Path $outputRoot 'ObsidianMdOpener.Console.exe'

& $compiler /nologo /target:exe /optimize+ `
    "/out:$testExecutable" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.Core.dll `
    $sources

if ($LASTEXITCODE -ne 0) {
    throw "测试版本编译失败，退出码：$LASTEXITCODE"
}

& $testExecutable --self-test
if ($LASTEXITCODE -ne 0) {
    throw "自动测试失败，退出码：$LASTEXITCODE"
}

if ($args.Count -gt 0) {
    & $testExecutable --diagnose $args[0]
    if ($LASTEXITCODE -ne 0) {
        throw "真实目录诊断失败，退出码：$LASTEXITCODE"
    }
}
