#Requires -Version 5.1
<#
.SYNOPSIS
    卸载「7-Zip 右键菜单助手」的 Windows 11 新版右键菜单扩展。
#>
[CmdletBinding()]
param(
    [string]$LogPath
)

$ErrorActionPreference = 'Stop'
$PackageName = '7zAIWin11RCM.ShellExt'

function Write-Log {
    param([string]$Message)
    $line = '[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $Message
    Write-Host $line
    if ($LogPath) {
        try { Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8 } catch { }
    }
}

try {
    Write-Log '=== 卸载 Windows 11 新版右键菜单扩展 ==='

    $package = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
    if (-not $package) {
        Write-Log '未检测到已安装的扩展包，无需卸载。'
        exit 0
    }

    Write-Log "正在移除 $($package.PackageFullName)…"
    $package | Remove-AppxPackage -ErrorAction Stop

    if (Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue) {
        Write-Log '错误：卸载后仍能查询到扩展包。'
        exit 6
    }

    Write-Log '卸载完成，重启资源管理器后生效。'
    exit 0
}
catch {
    Write-Log ("错误：" + $_.Exception.Message)
    exit 6
}
