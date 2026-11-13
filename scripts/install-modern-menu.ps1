#Requires -Version 5.1
<#
.SYNOPSIS
    安装「7-Zip 右键菜单助手」的 Windows 11 新版右键菜单扩展。

.DESCRIPTION
    Windows 11 的紧凑右键菜单只接受带包标识的 IExplorerCommand 扩展，
    因此这里把随附的 COM 宿主 DLL 打包成稀疏 MSIX（sparse package），
    用自签名证书签名后以 ExternalLocation 方式注册。

    需要管理员权限：写入 LocalMachine 证书存储 + Add-AppxPackage。

.PARAMETER ExternalPath
    应用安装目录（包含 SevenZipAIWin11RCM.exe、shell\ 与 packaging\）。

.PARAMETER ShellExtensionPath
    COM 宿主 DLL 所在目录（通常为 <ExternalPath>\shell）。

.PARAMETER ManifestPath
    AppxManifest.xml 路径，默认 <ExternalPath>\packaging\AppxManifest.xml。

.PARAMETER LogPath
    日志文件路径，由主程序传入。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExternalPath,
    [Parameter(Mandatory = $true)][string]$ShellExtensionPath,
    [string]$ManifestPath,
    [string]$LogPath
)

$ErrorActionPreference = 'Stop'
$PackageName = '7zAIWin11RCM.ShellExt'
$CertificateSubject = 'CN=7zAIWin11RCM'
$WorkRoot = Join-Path $env:TEMP '7zAIWin11RCM'

function Write-Log {
    param([string]$Message)
    $line = '[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $Message
    Write-Host $line
    if ($LogPath) {
        try { Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8 } catch { }
    }
}

function Find-SdkTool {
    param([string]$Name)

    $roots = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'),
        (Join-Path $env:ProgramFiles 'Windows Kits\10\bin')
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($root in $roots) {
        $tool = Get-ChildItem -Path $root -Filter $Name -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($tool) { return $tool.FullName }
    }

    return $null
}

try {
    Write-Log '=== 安装 Windows 11 新版右键菜单扩展 ==='

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Log '错误：需要管理员权限。'
        exit 5
    }

    if (-not $ManifestPath) {
        $ManifestPath = Join-Path $ExternalPath 'packaging\AppxManifest.xml'
    }

    $comHost = Join-Path $ShellExtensionPath 'SevenZipAIWin11RCM.ShellExt.comhost.dll'
    foreach ($required in @($ManifestPath, $comHost)) {
        if (-not (Test-Path -LiteralPath $required)) {
            Write-Log "错误：缺少文件 $required"
            exit 4
        }
    }

    $makeAppx = Find-SdkTool -Name 'MakeAppx.exe'
    $signtool = Find-SdkTool -Name 'signtool.exe'
    if (-not $makeAppx -or -not $signtool) {
        Write-Log '错误：未找到 MakeAppx.exe / signtool.exe，请安装 Windows SDK（10.0.22621 或更高）。'
        exit 2
    }
    Write-Log "使用 MakeAppx：$makeAppx"
    Write-Log "使用 SignTool：$signtool"

    $existing = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Log '检测到旧版本，先卸载…'
        $existing | Remove-AppxPackage -ErrorAction Stop
    }

    # 1) 准备 MakeAppx 的目录结构
    $stage = Join-Path $WorkRoot 'stage'
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    New-Item -ItemType Directory -Path $stage -Force | Out-Null

    Copy-Item -LiteralPath $ManifestPath -Destination (Join-Path $stage 'AppxManifest.xml') -Force

    $assetSource = Join-Path $ExternalPath 'packaging\Assets'
    if (Test-Path -LiteralPath $assetSource) {
        $assetTarget = Join-Path $stage 'packaging\Assets'
        New-Item -ItemType Directory -Path $assetTarget -Force | Out-Null
        Copy-Item -Path (Join-Path $assetSource '*.png') -Destination $assetTarget -Force
    }

    $msix = Join-Path $WorkRoot 'SevenZipAIWin11RCM.ShellExt.msix'
    if (Test-Path -LiteralPath $msix) { Remove-Item -LiteralPath $msix -Force }

    Write-Log '正在打包稀疏 MSIX…'
    & $makeAppx pack /o /d $stage /p $msix | ForEach-Object { Write-Log $_ }
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $msix)) {
        Write-Log "错误：MakeAppx 打包失败（退出码 $LASTEXITCODE）。"
        exit 3
    }

    # 2) 准备代码签名证书
    $certificate = Get-ChildItem Cert:\LocalMachine\My |
        Where-Object { $_.Subject -eq $CertificateSubject -and $_.HasPrivateKey } |
        Select-Object -First 1

    if (-not $certificate) {
        Write-Log '创建自签名代码签名证书…'
        $certificate = New-SelfSignedCertificate `
            -Type Custom `
            -Subject $CertificateSubject `
            -KeyUsage DigitalSignature `
            -FriendlyName '7zAIWin11RCM 右键菜单扩展' `
            -CertStoreLocation 'Cert:\LocalMachine\My' `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') `
            -NotAfter (Get-Date).AddYears(10)
    }

    $trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople |
        Where-Object { $_.Thumbprint -eq $certificate.Thumbprint } |
        Select-Object -First 1

    if (-not $trusted) {
        Write-Log '把证书加入「受信任人」存储…'
        $cerPath = Join-Path $WorkRoot '7zAIWin11RCM.cer'
        Export-Certificate -Cert $certificate -FilePath $cerPath -Force | Out-Null
        Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
    }

    Write-Log '正在签名…'
    & $signtool sign /fd SHA256 /td SHA256 /sha1 $certificate.Thumbprint $msix |
        ForEach-Object { Write-Log $_ }
    if ($LASTEXITCODE -ne 0) {
        Write-Log "错误：签名失败（退出码 $LASTEXITCODE）。"
        exit 3
    }

    # 3) 注册稀疏包，外部内容指向应用安装目录
    Write-Log "注册扩展包（ExternalLocation=$ExternalPath）…"
    Add-AppxPackage -Path $msix -ExternalLocation $ExternalPath -ErrorAction Stop

    $installed = Get-AppxPackage -Name $PackageName -ErrorAction Stop
    if (-not $installed) {
        Write-Log '错误：注册完成但未查询到扩展包。'
        exit 6
    }

    Write-Log "安装成功：$($installed.PackageFullName)"
    Write-Log '提示：资源管理器可能需要重启（任务管理器 → 重启「Windows 资源管理器」）才能看到新菜单。'
    exit 0
}
catch {
    Write-Log ("错误：" + $_.Exception.Message)
    if ($_.ScriptStackTrace) { Write-Log $_.ScriptStackTrace }
    exit 6
}
