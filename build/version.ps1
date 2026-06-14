#Requires -Version 7.0

function Get-WinSnapVersion {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    $propsPath = Join-Path $RepoRoot 'Directory.Build.props'
    if (-not (Test-Path -LiteralPath $propsPath)) {
        throw "找不到版本文件：$propsPath"
    }

    $content = Get-Content -LiteralPath $propsPath -Raw
    $match = [regex]::Match($content, '<Version>(?<Version>\d+\.\d+\.\d+)</Version>')
    if (-not $match.Success) {
        throw "无法从 $propsPath 读取 <Version>x.y.z</Version>。"
    }

    return $match.Groups['Version'].Value
}

function Update-WinSnapPatchVersion {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    $propsPath = Join-Path $RepoRoot 'Directory.Build.props'
    if (-not (Test-Path -LiteralPath $propsPath)) {
        throw "找不到版本文件：$propsPath"
    }

    $content = Get-Content -LiteralPath $propsPath -Raw
    $match = [regex]::Match($content, '<Version>(?<Version>\d+\.\d+\.\d+)</Version>')
    if (-not $match.Success) {
        throw "无法从 $propsPath 读取 <Version>x.y.z</Version>。"
    }

    $parts = $match.Groups['Version'].Value.Split('.')
    $newVersion = '{0}.{1}.{2}' -f $parts[0], $parts[1], ([int]$parts[2] + 1)
    $valueGroup = $match.Groups['Version']
    $updated = $content.Substring(0, $valueGroup.Index) +
        $newVersion +
        $content.Substring($valueGroup.Index + $valueGroup.Length)

    Set-Content -LiteralPath $propsPath -Value $updated -NoNewline -Encoding UTF8
    return $newVersion
}
