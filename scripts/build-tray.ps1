<#
.SYNOPSIS
    编译 Codex 代理守护托盘控制台（GuardianTray.exe）
.DESCRIPTION
    使用系统自带 .NET Framework 4.8 csc.exe 编译，无需安装 SDK。
    输出到项目根目录 dist\GuardianTray.exe。
    用法:
        .\build-tray.ps1            # 增量编译（存在 exe 时跳过）
        .\build-tray.ps1 -Force     # 强制重新编译
#>
[CmdletBinding()]
param([switch]$Force)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptDir
$src = Join-Path $root 'src\GuardianTray.cs'
$dist = Join-Path $root 'dist'
$out = Join-Path $dist 'GuardianTray.exe'

if (-not (Test-Path -LiteralPath $src)) { throw "未找到 $src" }

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $csc)) { throw '未找到 csc.exe（需要 .NET Framework 4.x）' }

if ((Test-Path -LiteralPath $out) -and -not $Force) {
    $srcTime = (Get-Item -LiteralPath $src).LastWriteTime
    $outTime = (Get-Item -LiteralPath $out).LastWriteTime
    if ($outTime -ge $srcTime) {
        "已是最新: $out"
        exit 0
    }
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null

& $csc /nologo /target:winexe /utf8output /out:"$out" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll "$src"
if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }

"编译完成: $out"
Get-Item -LiteralPath $out | Select-Object FullName, Length, LastWriteTime