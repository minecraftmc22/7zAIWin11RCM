#Requires -Version 5.1
<#
.SYNOPSIS
    一键构建「7-Zip 右键菜单助手」。

.DESCRIPTION
    先构建 COM 宿主扩展（shell\SevenZipAIWin11RCM.ShellExt.comhost.dll），
    再构建 WinUI 3 主程序；主程序的构建后步骤会把扩展产物复制到 shell\ 子目录，
    因此产物目录可以直接运行，也可以直接用于注册新版右键菜单。

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Configuration Debug -Run
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$Run
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Push-Location $root
try {
    Write-Host "== 构建 COM 宿主扩展 ($Configuration/$Runtime) ==" -ForegroundColor Cyan
    dotnet build 'src\SevenZipAIWin11RCM.ShellExt\SevenZipAIWin11RCM.ShellExt.csproj' `
        -c $Configuration -r $Runtime --nologo
    if ($LASTEXITCODE -ne 0) { throw 'COM 宿主扩展构建失败。' }

    Write-Host "== 构建 WinUI 3 主程序 ($Configuration/$Runtime) ==" -ForegroundColor Cyan
    dotnet build 'src\7zAIWin11RCM\7zAIWin11RCM.csproj' `
        -c $Configuration -r $Runtime --nologo
    if ($LASTEXITCODE -ne 0) { throw '主程序构建失败。' }

    $output = Join-Path $root "src\7zAIWin11RCM\bin\$Configuration\net8.0-windows10.0.19041.0\$Runtime"
    $exe = Join-Path $output 'SevenZipAIWin11RCM.exe'

    if (-not (Test-Path -LiteralPath $exe)) { throw "未找到输出文件：$exe" }

    Write-Host ''
    Write-Host '构建完成。' -ForegroundColor Green
    Write-Host "输出目录：$output"
    Write-Host '直接运行上面的 SevenZipAIWin11RCM.exe 即可（无需安装 Windows App SDK 运行时）。'

    if ($Run) {
        Start-Process -FilePath $exe
    }
}
finally {
    Pop-Location
}
