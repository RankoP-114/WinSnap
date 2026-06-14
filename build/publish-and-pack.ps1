#Requires -Version 7.0
<#
.SYNOPSIS
    一步完成：发布 WinSnap（self-contained 单文件 R2R）+ 用 Inno Setup 打包成安装程序。

.DESCRIPTION
    1) 调用同目录的 publish.ps1 发布到 build/publish；
    2) 定位 ISCC.exe（Inno Setup 6 命令行编译器），编译 build/installer/WinSnap.iss，
       生成安装程序 build/installer/Output/WinSnap-Setup.exe。

    若找不到 ISCC.exe，给出 Inno Setup 安装提示并退出。

.PARAMETER Configuration
    传给 publish.ps1 的构建配置，默认 Release。

.PARAMETER IsccPath
    手动指定 ISCC.exe 完整路径（找不到自动路径时使用）。

.PARAMETER SkipPublish
    跳过发布步骤，仅打包已有的 build/publish 产物。

.PARAMETER NoVersionBump
    跳过自动递增 Directory.Build.props 中的 patch 版本号。

.EXAMPLE
    pwsh build/publish-and-pack.ps1

.EXAMPLE
    pwsh build/publish-and-pack.ps1 -IsccPath 'C:\Tools\Inno Setup 6\ISCC.exe'
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$IsccPath,
    [switch]$SkipPublish,
    [switch]$NoVersionBump
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptDir
$PublishScript = Join-Path $ScriptDir 'publish.ps1'
$VersionScript = Join-Path $ScriptDir 'version.ps1'
$IssScript     = Join-Path $ScriptDir 'installer\WinSnap.iss'

if (-not (Test-Path -LiteralPath $VersionScript)) {
    Write-Error "找不到版本脚本：$VersionScript"
    exit 1
}

. $VersionScript

if ($SkipPublish -or $NoVersionBump) {
    $AppVersion = Get-WinSnapVersion -RepoRoot $RepoRoot
} else {
    $AppVersion = Update-WinSnapPatchVersion -RepoRoot $RepoRoot
}

Write-Host ("  版本        : {0}" -f $AppVersion)

# ---- 1) 发布 ------------------------------------------------------------
if (-not $SkipPublish) {
    Write-Host '>>> 步骤 1/2：发布 WinSnap' -ForegroundColor Cyan
    if (-not (Test-Path -LiteralPath $PublishScript)) {
        Write-Error "找不到发布脚本：$PublishScript"
        exit 1
    }
    & $PublishScript -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        Write-Error "发布失败（退出码 $LASTEXITCODE），已中止打包。"
        exit $LASTEXITCODE
    }
    Write-Host ''
} else {
    Write-Host '>>> 已跳过发布步骤（-SkipPublish）' -ForegroundColor Yellow
}

# ---- 2) 定位 ISCC.exe ---------------------------------------------------
Write-Host '>>> 步骤 2/2：用 Inno Setup 打包安装程序' -ForegroundColor Cyan

function Find-Iscc {
    param([string]$Explicit)

    # 2a) 显式参数
    if ($Explicit) {
        if (Test-Path -LiteralPath $Explicit) { return (Resolve-Path -LiteralPath $Explicit).Path }
        Write-Warning "指定的 ISCC 路径不存在：$Explicit，继续尝试自动查找…"
    }

    # 2b) PATH 中
    $cmd = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    # 2c) 常见安装路径
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
        (Join-Path $env:ProgramFiles          'Inno Setup 6\ISCC.exe')
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 5\ISCC.exe')
        (Join-Path $env:ProgramFiles          'Inno Setup 5\ISCC.exe')
        (Join-Path $env:LOCALAPPDATA          'Programs\Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ }

    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c) { return (Resolve-Path -LiteralPath $c).Path }
    }

    # 2d) 注册表（Inno Setup 安装时写入的 App Paths / 卸载信息）
    $regPaths = @(
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1'
    )
    foreach ($rp in $regPaths) {
        try {
            $loc = (Get-ItemProperty -Path $rp -ErrorAction Stop).InstallLocation
            if ($loc) {
                $exe = Join-Path $loc 'ISCC.exe'
                if (Test-Path -LiteralPath $exe) { return (Resolve-Path -LiteralPath $exe).Path }
            }
        } catch { }
    }

    return $null
}

$iscc = Find-Iscc -Explicit $IsccPath

if (-not $iscc) {
    Write-Host ''
    Write-Error @'
未找到 Inno Setup 编译器 ISCC.exe。

请先安装 Inno Setup 6（免费）：
  - 官网：https://jrsoftware.org/isdl.php
  - 或用 winget： winget install --id JRSoftware.InnoSetup -e

安装后重新运行本脚本；若装在非默认位置，可显式指定：
  pwsh build/publish-and-pack.ps1 -SkipPublish -IsccPath "<完整路径>\ISCC.exe"
'@
    exit 1
}

Write-Host ("  使用 ISCC   : {0}" -f $iscc)

if (-not (Test-Path -LiteralPath $IssScript)) {
    Write-Error "找不到 Inno Setup 脚本：$IssScript"
    exit 1
}

# 校验发布产物存在
$publishExe = Join-Path $ScriptDir 'publish\WinSnap.exe'
if (-not (Test-Path -LiteralPath $publishExe)) {
    Write-Error "未找到发布产物 $publishExe。请先运行发布（去掉 -SkipPublish），或确认 build/publish 已生成。"
    exit 1
}

Write-Host ("  编译脚本    : {0}" -f $IssScript)
Write-Host ''

& $iscc "/DAppVersion=$AppVersion" $IssScript
$exit = $LASTEXITCODE

if ($exit -ne 0) {
    Write-Error "Inno Setup 编译失败（退出码 $exit）。"
    exit $exit
}

# ---- 汇报安装包 ---------------------------------------------------------
$setupPath = Join-Path $ScriptDir 'installer\Output\WinSnap-Setup.exe'
Write-Host ''
Write-Host '==========================================' -ForegroundColor Green
Write-Host ' 打包成功' -ForegroundColor Green
Write-Host '==========================================' -ForegroundColor Green
if (Test-Path -LiteralPath $setupPath) {
    $item = Get-Item -LiteralPath $setupPath
    Write-Host ("  安装程序    : {0}" -f $setupPath)
    Write-Host ("  大小        : {0:N2} MB" -f ($item.Length / 1MB))
} else {
    Write-Warning "编译返回成功，但未在预期位置找到 WinSnap-Setup.exe，请检查 .iss 的 OutputDir 设置。"
}

exit 0
