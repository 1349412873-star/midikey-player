#Requires -Version 5.1
<#
.SYNOPSIS
    跑一次 MidiKeyPlayer 的内置自检（MIDIKEY_GAME_SELFTEST），统计 PASS / FAIL。

.DESCRIPTION
    自检是纯逻辑用例（键位方案的内置预设与键名、简谱音高换算）。
    程序走这条路时不建窗口、不注册热键、不碰按键与 MIDI 设备。

    环境变量名仍叫 MIDIKEY_GAME_SELFTEST（历史原因，不改名：改名会破坏既有用法）。

    本脚本做五件事：
      1. 定位 exe（依次找 bin\Release\net8.0\win-x64\、release\win-x64\、bin\Release\net8.0\）。
      2. 设环境变量 MIDIKEY_GAME_SELFTEST=<报告文件>，启动 exe。
      3. 等它退出（超时可配）。
      4. 读回报告文件，统计 PASS / FAIL。
      5. 打印结果，用同样的退出码结束。

    本脚本不写 bin 或 obj，不改仓库文件。报告只写到 %TEMP%。
    程序清单是 requireAdministrator：不在管理员会话里跑会弹 UAC。

.PARAMETER ExePath
    exe 路径。默认 <仓库>\MidiKeyPlayer\bin\Release\net8.0\MidiKeyPlayer.exe。

.PARAMETER ReportPath
    自检报告的输出路径。默认 %TEMP%\midikey-selftest-<时间戳>.txt。
    不接受仓库内的路径（本脚本不修改仓库文件）。

.PARAMETER TimeoutSeconds
    等待自检退出的秒数。默认 120。

.EXAMPLE
    pwsh -File midikey-player\tools\run-selftest.ps1

.NOTES
    退出码：
      0  = 自检全部通过
      1  = 报告里有 FAIL（或自检进程返回 1）
      2  = 找不到 exe
      3  = 启动失败、超时、报告缺失，或报告路径非法
#>
[CmdletBinding()]
param(
    [string]$ExePath = '',
    [string]$ReportPath = '',
    [int]$TimeoutSeconds = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 仓库根 = 本脚本上一级目录（midikey-player\）
$repoRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    # csproj 设了 RuntimeIdentifier=win-x64，Release 输出在 net8.0\win-x64\ 下。
    # 老的非 RID 目录里可能残留旧 exe，所以三个候选按优先级查，避免悄悄跑旧程序。
    $candidates = @(
        (Join-Path $repoRoot 'MidiKeyPlayer\bin\Release\net8.0\win-x64\MidiKeyPlayer.exe'),
        (Join-Path $repoRoot 'MidiKeyPlayer\release\win-x64\MidiKeyPlayer.exe'),
        (Join-Path $repoRoot 'MidiKeyPlayer\bin\Release\net8.0\MidiKeyPlayer.exe')
    )
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c -PathType Leaf) { $ExePath = $c; break }
    }
    if ([string]::IsNullOrWhiteSpace($ExePath)) { $ExePath = $candidates[0] }
}
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $ReportPath = Join-Path $env:TEMP "midikey-selftest-$stamp.txt"
}

$exe = [System.IO.Path]::GetFullPath($ExePath)
$report = [System.IO.Path]::GetFullPath($ReportPath)

# 报告不许落在仓库里：本脚本不改仓库文件。
if ($report.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "!! 报告路径落在仓库里：$report"
    Write-Host "   本脚本不修改仓库文件。请把报告写到 %TEMP% 或其它仓库外目录。"
    exit 3
}

if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    Write-Host "!! 找不到 exe：$exe"
    Write-Host "   先构建：dotnet build midikey-player/MidiKeyPlayer/MidiKeyPlayer.csproj -c Release"
    Write-Host "   或用 -ExePath <路径> 指定别的 exe。"
    exit 2
}

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
$isAdmin = $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)

Write-Host ">> exe    : $exe"
Write-Host ">> 报告   : $report"
if (-not $isAdmin) {
    Write-Host "!! 当前会话不是管理员。程序清单要求管理员权限，启动时会弹 UAC。"
    Write-Host "   如果 UAC 被拒或无法弹窗，请用管理员身份重开 PowerShell，再跑本脚本。"
}

# 只为子进程设置环境变量，跑完恢复原值。
$oldValue = $env:MIDIKEY_GAME_SELFTEST
$env:MIDIKEY_GAME_SELFTEST = $report
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
try {
    $proc = Start-Process -FilePath $exe -PassThru
}
catch {
    Write-Host "!! 启动失败：$($_.Exception.Message)"
    Write-Host "   常见原因：UAC 被拒绝、权限不足、exe 被占用。"
    exit 3
}
finally {
    if ($null -eq $oldValue) {
        Remove-Item Env:\MIDIKEY_GAME_SELFTEST -ErrorAction SilentlyContinue
    }
    else {
        $env:MIDIKEY_GAME_SELFTEST = $oldValue
    }
}

if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
    Write-Host "!! 自检超过 $TimeoutSeconds 秒没有退出，强制结束进程 $($proc.Id)。"
    try { $proc.Kill() } catch { }
    exit 3
}
$stopwatch.Stop()
$code = $proc.ExitCode

if (-not (Test-Path -LiteralPath $report -PathType Leaf)) {
    Write-Host "!! 自检没有写出报告文件。进程退出码：$code"
    if ($code -ne 0) { exit $code }
    exit 3
}

$lines = @(Get-Content -LiteralPath $report -Encoding UTF8)
$passLines = @($lines | Where-Object { $_ -match '^PASS' })
$failLines = @($lines | Where-Object { $_ -match '^FAIL' })
$resultLines = @($lines | Where-Object { $_ -match '^结果：' })

Write-Host ''
Write-Host '---- 自检报告 ----'
foreach ($line in $lines) { Write-Host $line }
Write-Host '------------------'
Write-Host ''
Write-Host "耗时  : $([math]::Round($stopwatch.Elapsed.TotalSeconds, 1)) 秒"
Write-Host "PASS  : $($passLines.Count)"
Write-Host "FAIL  : $($failLines.Count)"
if ($resultLines.Count -gt 0) { Write-Host $resultLines[$resultLines.Count - 1] }

if ($failLines.Count -gt 0) {
    Write-Host ''
    Write-Host '失败明细：'
    foreach ($line in $failLines) { Write-Host "  $line" }
}

if ($code -ne 0) {
    Write-Host ''
    Write-Host "自检失败。退出码 $code。"
    exit $code
}
if ($failLines.Count -gt 0) {
    Write-Host ''
    Write-Host "退出码是 0，但报告里有 $($failLines.Count) 条 FAIL。按失败处理。"
    exit 1
}

Write-Host ''
Write-Host '自检全部通过。退出码 0。'
exit 0
