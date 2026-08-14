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
$src2 = Join-Path $root 'src\AddDirectForm.cs'
$src3 = Join-Path $root 'src\GuardianMainForm.cs'
$src4 = Join-Path $root 'src\DirectListForm.cs'
$src5 = Join-Path $root 'src\NodesForm.cs'
$src6 = Join-Path $root 'src\LogViewForm.cs'
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
    $src2Time = (Get-Item -LiteralPath $src2).LastWriteTime
    $src3Time = (Get-Item -LiteralPath $src3).LastWriteTime
    $src4Time = (Get-Item -LiteralPath $src4).LastWriteTime
    $src5Time = (Get-Item -LiteralPath $src5).LastWriteTime
    $src6Time = (Get-Item -LiteralPath $src6).LastWriteTime
    $outTime = (Get-Item -LiteralPath $out).LastWriteTime
    $allSrc = @($srcTime,$src2Time,$src3Time,$src4Time,$src5Time,$src6Time)
    if ($outTime -ge ($allSrc | Measure-Object -Maximum).Maximum) {
        "已是最新: $out"
        exit 0
    }
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null

& $csc /nologo /target:winexe /utf8output /win32icon:"$(Join-Path $dist 'guardian.ico')" /out:"$out" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll "$src" "$src2" "$src3" "$src4" "$src5" "$src6"
if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }

"编译完成: $out"
Get-Item -LiteralPath $out | Select-Object FullName, Length, LastWriteTime