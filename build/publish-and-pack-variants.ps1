#Requires -Version 7.0
<#
.SYNOPSIS
    一次生成两种 WinSnap 安装程序：带 .NET 10 和不带 .NET 10。

.DESCRIPTION
    输出：
      - build/installer/Output/WinSnap-Setup-with-dotnet10.exe
        self-contained，安装包内包含 .NET 10 Windows Desktop Runtime。

      - build/installer/Output/WinSnap-Setup-no-dotnet10.exe
        framework-dependent，不包含 .NET 10；目标机器需已有 .NET 10 Desktop Runtime x64。
        该版本不启用单文件发布，避免把 framework-dependent apphost 做成过大的单文件。

.PARAMETER Configuration
    构建配置，默认 Release。

.PARAMETER Runtime
    Runtime Identifier，默认 win-x64。

.PARAMETER IsccPath
    手动指定 Inno Setup ISCC.exe 路径。

.PARAMETER SkipPublish
    跳过 dotnet publish，只用已有 build/publish/with-dotnet10 和 build/publish/no-dotnet10 打包。

.PARAMETER NoVersionBump
    跳过自动递增 Directory.Build.props 中的 patch 版本号。
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
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
$IssScript = Join-Path $ScriptDir 'installer\WinSnap.iss'
$PublishRoot = Join-Path $ScriptDir 'publish'
$WithRuntimeDir = Join-Path $PublishRoot 'with-dotnet10'
$NoRuntimeDir = Join-Path $PublishRoot 'no-dotnet10'
$InstallerOutputDir = Join-Path $ScriptDir 'installer\Output'

function Find-Iscc {
    param([string]$Explicit)

    if ($Explicit) {
        if (Test-Path -LiteralPath $Explicit) { return (Resolve-Path -LiteralPath $Explicit).Path }
        Write-Warning "指定的 ISCC 路径不存在：$Explicit，继续尝试自动查找。"
    }

    $cmd = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 5\ISCC.exe')
        (Join-Path $env:ProgramFiles 'Inno Setup 5\ISCC.exe')
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return (Resolve-Path -LiteralPath $candidate).Path }
    }

    $regPaths = @(
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1'
    )
    foreach ($regPath in $regPaths) {
        try {
            $loc = (Get-ItemProperty -Path $regPath -ErrorAction Stop).InstallLocation
            if ($loc) {
                $exe = Join-Path $loc 'ISCC.exe'
                if (Test-Path -LiteralPath $exe) { return (Resolve-Path -LiteralPath $exe).Path }
            }
        } catch { }
    }

    return $null
}

function Invoke-Checked {
    param(
        [string]$FilePath,
        [string[]]$Arguments
    )

    Write-Host ("执行：{0} {1}" -f $FilePath, ($Arguments -join ' ')) -ForegroundColor DarkGray
    & $FilePath @Arguments
    $exit = $LASTEXITCODE
    if ($exit -ne 0) {
        throw "命令失败（退出码 $exit）：$FilePath"
    }
}

function Format-FileSize {
    param([long]$Bytes)
    if ($Bytes -ge 1GB) { return ('{0:N2} GB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:N2} MB' -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ('{0:N2} KB' -f ($Bytes / 1KB)) }
    return "$Bytes B"
}

if (-not (Test-Path -LiteralPath $PublishScript)) {
    throw "找不到发布脚本：$PublishScript"
}
if (-not (Test-Path -LiteralPath $VersionScript)) {
    throw "找不到版本脚本：$VersionScript"
}
if (-not (Test-Path -LiteralPath $IssScript)) {
    throw "找不到 Inno Setup 脚本：$IssScript"
}

. $VersionScript

if ($SkipPublish -or $NoVersionBump) {
    $AppVersion = Get-WinSnapVersion -RepoRoot $RepoRoot
} else {
    $AppVersion = Update-WinSnapPatchVersion -RepoRoot $RepoRoot
}

Write-Host '==========================================' -ForegroundColor Cyan
Write-Host ' WinSnap 双安装包发布' -ForegroundColor Cyan
Write-Host '==========================================' -ForegroundColor Cyan
Write-Host ("  配置       : {0}" -f $Configuration)
Write-Host ("  Runtime    : {0}" -f $Runtime)
Write-Host ("  版本       : {0}" -f $AppVersion)
Write-Host ("  仓库       : {0}" -f $RepoRoot)
Write-Host ''

if (-not $SkipPublish) {
    Write-Host '>>> 步骤 1/3：发布带 .NET 10 的 self-contained 版本' -ForegroundColor Cyan
    & $PublishScript `
        -Configuration $Configuration `
        -Runtime $Runtime `
        -OutputDir $WithRuntimeDir `
        -SelfContained:$true `
        -SingleFile:$true `
        -ReadyToRun:$true `
        -UseAltObj
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host ''

    Write-Host '>>> 步骤 2/3：发布不带 .NET 10 的 framework-dependent 版本' -ForegroundColor Cyan
    & $PublishScript `
        -Configuration $Configuration `
        -Runtime $Runtime `
        -OutputDir $NoRuntimeDir `
        -SelfContained:$false `
        -SingleFile:$false `
        -ReadyToRun:$false `
        -UseAltObj
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host ''
} else {
    Write-Host '>>> 已跳过发布步骤（-SkipPublish）' -ForegroundColor Yellow
}

foreach ($dir in @($WithRuntimeDir, $NoRuntimeDir)) {
    $exe = Join-Path $dir 'WinSnap.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "缺少发布产物：$exe"
    }
}

Write-Host '>>> 步骤 3/3：用 Inno Setup 生成安装程序' -ForegroundColor Cyan
$iscc = Find-Iscc -Explicit $IsccPath
if (-not $iscc) {
    throw @'
未找到 Inno Setup 编译器 ISCC.exe。

请先安装 Inno Setup 6：
  winget install --id JRSoftware.InnoSetup -e

安装后重新运行：
  pwsh build/publish-and-pack-variants.ps1 -SkipPublish
'@
}
Write-Host ("  使用 ISCC : {0}" -f $iscc)

New-Item -ItemType Directory -Path $InstallerOutputDir -Force | Out-Null

Invoke-Checked $iscc @(
    "/DPublishDir=$WithRuntimeDir",
    "/DOutputBaseFilename=WinSnap-Setup-with-dotnet10",
    "/DAppVersion=$AppVersion",
    $IssScript
)

Invoke-Checked $iscc @(
    "/DPublishDir=$NoRuntimeDir",
    "/DOutputBaseFilename=WinSnap-Setup-no-dotnet10",
    "/DAppVersion=$AppVersion",
    '/DRequiresDotNet10',
    $IssScript
)

$installers = @(
    (Join-Path $InstallerOutputDir 'WinSnap-Setup-with-dotnet10.exe')
    (Join-Path $InstallerOutputDir 'WinSnap-Setup-no-dotnet10.exe')
)

Write-Host ''
Write-Host '==========================================' -ForegroundColor Green
Write-Host ' 打包完成' -ForegroundColor Green
Write-Host '==========================================' -ForegroundColor Green
foreach ($installer in $installers) {
    if (Test-Path -LiteralPath $installer) {
        $item = Get-Item -LiteralPath $installer
        Write-Host ("  {0}  {1}" -f (Format-FileSize $item.Length), $item.FullName)
    } else {
        Write-Warning "未找到预期安装包：$installer"
    }
}

exit 0
