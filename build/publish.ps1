#Requires -Version 7.0
<#
.SYNOPSIS
    发布 WinSnap 为 self-contained、win-x64、单文件、ReadyToRun 的可执行程序。

.DESCRIPTION
    调用 `dotnet publish` 把主程序项目 src/WinSnap.App/WinSnap.App.csproj 发布到
    build/publish 目录。所有发布相关配置均通过命令行参数传入，不修改任何 .csproj。

    产物特性：
      - self-contained（自带 .NET 运行时，目标机无需安装 .NET）
      - win-x64
      - PublishSingleFile（单文件 WinSnap.exe，原生库自解压）
      - PublishReadyToRun（R2R 预编译，加快启动）
      注意：WPF 不支持 NativeAOT，因此使用 ReadyToRun。

.PARAMETER Configuration
    构建配置，默认 Release。

.PARAMETER Runtime
    运行时标识，默认 win-x64。

.PARAMETER OutputDir
    发布输出目录，默认 <repo>/build/publish。

.PARAMETER SelfContained
    是否 self-contained，默认 $true。

.PARAMETER SingleFile
    是否单文件发布，默认 $true。

.PARAMETER ReadyToRun
    是否启用 R2R 预编译，默认 $true。

.PARAMETER NoClean
    跳过清理旧输出目录。

.PARAMETER UseAltObj
    使用仓库根目录下的 .alt-obj 作为中间输出目录，用于绕过旧 obj 目录被锁定或权限异常的情况。

.EXAMPLE
    pwsh build/publish.ps1

.EXAMPLE
    pwsh build/publish.ps1 -OutputDir D:\out -NoClean
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime       = 'win-x64',
    [string]$OutputDir,
    [bool]$SelfContained   = $true,
    [bool]$SingleFile      = $true,
    [bool]$ReadyToRun      = $true,
    [switch]$NoClean,
    [switch]$UseAltObj
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ---- 路径解析（脚本位于 <repo>/build/，仓库根为其上级）-------------------
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Split-Path -Parent $ScriptDir
$Project   = Join-Path $RepoRoot 'src\WinSnap.App\WinSnap.App.csproj'

if (-not $OutputDir) {
    $OutputDir = Join-Path $ScriptDir 'publish'
}

if (-not (Test-Path -LiteralPath $Project)) {
    Write-Error "找不到主程序项目：$Project"
    exit 1
}

# ---- 校验 dotnet SDK -----------------------------------------------------
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Error '未找到 dotnet CLI。请安装 .NET 10 SDK 后重试：https://dotnet.microsoft.com/download'
    exit 1
}

Write-Host '==========================================' -ForegroundColor Cyan
Write-Host ' WinSnap 发布脚本' -ForegroundColor Cyan
Write-Host '==========================================' -ForegroundColor Cyan
try {
    $sdkVersion = (& dotnet --version) 2>$null
    Write-Host ("  .NET SDK     : {0}" -f $sdkVersion)
} catch { }
Write-Host ("  项目         : {0}" -f $Project)
Write-Host ("  配置         : {0}" -f $Configuration)
Write-Host ("  运行时       : {0}" -f $Runtime)
Write-Host ("  self-contained: {0}" -f $SelfContained)
Write-Host ("  单文件       : {0}" -f $SingleFile)
Write-Host ("  ReadyToRun   : {0}" -f $ReadyToRun)
Write-Host ("  备用 obj     : {0}" -f [bool]$UseAltObj)
Write-Host ("  输出目录     : {0}" -f $OutputDir)
Write-Host ''

# ---- 清理旧输出 ----------------------------------------------------------
if (-not $NoClean) {
    if (Test-Path -LiteralPath $OutputDir) {
        Write-Host "清理旧输出目录…" -ForegroundColor Yellow
        try {
            Remove-Item -LiteralPath $OutputDir -Recurse -Force -ErrorAction Stop
        } catch {
            Write-Error "无法清理输出目录（可能有文件被占用，请关闭正在运行的 WinSnap.exe）：$($_.Exception.Message)"
            exit 1
        }
    }
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

# ---- 组装 dotnet publish 参数 -------------------------------------------
$publishArgs = @(
    'publish'
    $Project
    '-c'; $Configuration
    '-r'; $Runtime
    "--self-contained"; $SelfContained.ToString().ToLowerInvariant()
    "-p:PublishSingleFile=$($SingleFile.ToString().ToLowerInvariant())"
    "-p:IncludeNativeLibrariesForSelfExtract=true"
    "-p:PublishReadyToRun=$($ReadyToRun.ToString().ToLowerInvariant())"
    '-o'; $OutputDir
    '--nologo'
)

if ($UseAltObj) {
    $publishArgs += '-p:WinSnapUseAltObj=true'
}

Write-Host '执行：' -ForegroundColor DarkGray
Write-Host ("  dotnet {0}" -f ($publishArgs -join ' ')) -ForegroundColor DarkGray
Write-Host ''

$sw = [System.Diagnostics.Stopwatch]::StartNew()
& dotnet @publishArgs
$exit = $LASTEXITCODE
$sw.Stop()

if ($exit -ne 0) {
    Write-Host ''
    Write-Error "dotnet publish 失败（退出码 $exit）。请检查上方编译输出。"
    exit $exit
}

# ---- 汇报产物 ------------------------------------------------------------
$exePath = Join-Path $OutputDir 'WinSnap.exe'

Write-Host ''
Write-Host '==========================================' -ForegroundColor Green
Write-Host ' 发布成功' -ForegroundColor Green
Write-Host '==========================================' -ForegroundColor Green
Write-Host ("  用时         : {0:N1} 秒" -f $sw.Elapsed.TotalSeconds)
Write-Host ("  输出目录     : {0}" -f $OutputDir)

if (Test-Path -LiteralPath $exePath) {
    $exeItem = Get-Item -LiteralPath $exePath
    Write-Host ("  主程序       : {0}" -f $exePath)
    Write-Host ("  主程序大小   : {0:N2} MB" -f ($exeItem.Length / 1MB))
} else {
    Write-Warning "未在输出目录找到 WinSnap.exe（请检查 AssemblyName 是否为 WinSnap）。"
}

# 输出目录总大小与文件数（单文件发布通常仍含少量额外文件，如 .pdb / 部分原生库）
$allFiles = Get-ChildItem -LiteralPath $OutputDir -Recurse -File -ErrorAction SilentlyContinue
if ($allFiles) {
    $totalBytes = ($allFiles | Measure-Object -Property Length -Sum).Sum
    Write-Host ("  输出总大小   : {0:N2} MB（共 {1} 个文件）" -f ($totalBytes / 1MB), $allFiles.Count)
    Write-Host ''
    Write-Host '  目录内容：' -ForegroundColor DarkGray
    $allFiles |
        Sort-Object Length -Descending |
        Select-Object -First 15 |
        ForEach-Object {
            Write-Host ("    {0,10:N2} KB  {1}" -f ($_.Length / 1KB), $_.Name) -ForegroundColor DarkGray
        }
}

Write-Host ''
Write-Host '下一步：用 Inno Setup 编译 build/installer/WinSnap.iss 生成安装程序，' -ForegroundColor Cyan
Write-Host '或运行 build/publish-and-pack.ps1 一步完成发布 + 打包。' -ForegroundColor Cyan

exit 0
