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

# 运行外部工具并合并 stdout / stderr。
# 注意：Windows PowerShell 5.1 里原生命令的错误走 stderr，直接管到 ForEach-Object 会丢掉；
# 而 $ErrorActionPreference='Stop' 时 2>&1 产生 ErrorRecord 又会抛异常，所以这里临时放宽。
function Invoke-Tool {
    param(
        [Parameter(Mandatory = $true)][string]$Exe,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $raw = & $Exe @Arguments 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }

    $text = (@($raw) | ForEach-Object { ([string]$_).TrimEnd() }) -join "`r`n"
    return [pscustomobject]@{ ExitCode = $code; Text = $text }
}

# 新建代码签名证书。
# 两点必须满足，否则签名一定失败：
#   1. 主题必须与 AppxManifest.xml 的 Publisher 完全一致（这里都是 CN=7zAIWin11RCM），
#      不一致时 signtool 只报 "SignerSign() failed"（0x8007000B），很难看懂；
#   2. 必须带代码签名 EKU（1.3.6.1.5.5.7.3.3）。
function New-SigningCertificate {
    return New-SelfSignedCertificate `
        -Type Custom `
        -Subject $CertificateSubject `
        -KeyUsage DigitalSignature `
        -KeyExportPolicy Exportable `
        -FriendlyName '7zAIWin11RCM 右键菜单扩展' `
        -CertStoreLocation 'Cert:\LocalMachine\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3') `
        -NotAfter (Get-Date).AddYears(10)
}

# 自签名证书必须被本机信任，否则 Add-AppxPackage 报 0x800B0109。
# 「受信任人」对侧载 MSIX 有效，「受信任的根证书颁发机构」是微软文档里的做法，两个都确保一遍。
function Register-CertificateTrust {
    param([Parameter(Mandatory = $true)]$Certificate)

    $cer = Join-Path $WorkRoot '7zAIWin11RCM.cer'
    Export-Certificate -Cert $Certificate -FilePath $cer -Force | Out-Null

    foreach ($store in @('TrustedPeople', 'Root')) {
        $exists = Get-ChildItem "Cert:\LocalMachine\$store" |
            Where-Object { $_.Thumbprint -eq $Certificate.Thumbprint } |
            Select-Object -First 1

        if (-not $exists) {
            Write-Log "把证书加入「$store」存储…"
            Import-Certificate -FilePath $cer -CertStoreLocation "Cert:\LocalMachine\$store" | Out-Null
        }
    }
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
    # /nv：稀疏包引用的 exe / DLL 都在外部安装目录里，不在包内，
    #     必须跳过"清单里引用的文件是否存在于包内"的校验。
    $packResult = Invoke-Tool -Exe $makeAppx -Arguments @('pack', '/o', '/nv', '/d', $stage, '/p', $msix)
    if ($packResult.Text) { Write-Log $packResult.Text }
    if ($packResult.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $msix)) {
        Write-Log "错误：MakeAppx 打包失败（退出码 $($packResult.ExitCode)）。"
        exit 3
    }

    # 2) 准备代码签名证书
    $certificate = Get-ChildItem Cert:\LocalMachine\My |
        Where-Object { $_.Subject -eq $CertificateSubject -and $_.HasPrivateKey } |
        Select-Object -First 1

    if (-not $certificate) {
        Write-Log '创建自签名代码签名证书…'
        $certificate = New-SigningCertificate
    }

    Write-Log "签名证书指纹：$($certificate.Thumbprint)"
    Register-CertificateTrust -Certificate $certificate

    Write-Log '正在签名…'
    # 不做时间戳：/td 必须与 /tr 成对出现，而时间戳服务器在离线环境下可能连不上。
    # 本地自签名 + 已加入受信任存储的包不需要时间戳。
    #
    # 必须带 /sm：证书建在 LocalMachine\My，而 signtool 默认只查 CurrentUser\My，
    # 不加 /sm 会直接报 "No certificates were found that met all the given criteria"。
    $signResult = Invoke-Tool -Exe $signtool -Arguments @(
        'sign', '/fd', 'SHA256', '/sm', '/sha1', $certificate.Thumbprint, $msix)
    if ($signResult.Text) { Write-Log $signResult.Text }

    # 兜底 1：某些环境里 signtool 无法直接使用证书存储中的私钥，改用 PFX 文件签名
    if ($signResult.ExitCode -ne 0) {
        Write-Log "证书存储签名失败（退出码 $($signResult.ExitCode)），改用 PFX 文件重试…"
        $pfxPath = Join-Path $WorkRoot 'sign.pfx'
        $pfxPassword = [guid]::NewGuid().ToString('N')
        try {
            $secure = ConvertTo-SecureString -String $pfxPassword -Force -AsPlainText
            Export-PfxCertificate -Cert $certificate -FilePath $pfxPath `
                -Password $secure -Force | Out-Null

            $signResult = Invoke-Tool -Exe $signtool -Arguments @(
                'sign', '/fd', 'SHA256', '/f', $pfxPath, '/p', $pfxPassword, $msix)
            if ($signResult.Text) { Write-Log $signResult.Text }
        }
        catch {
            Write-Log "导出 PFX 失败：$($_.Exception.Message)"
        }
        finally {
            if (Test-Path -LiteralPath $pfxPath) { Remove-Item -LiteralPath $pfxPath -Force -ErrorAction SilentlyContinue }
        }
    }

    # 兜底 2：证书本身有问题（早期版本生成的证书私钥不可导出），删掉重建再签一次
    if ($signResult.ExitCode -ne 0) {
        Write-Log '现有证书无法用于签名，删除后重建…'
        try {
            Get-ChildItem Cert:\LocalMachine\My |
                Where-Object { $_.Subject -eq $CertificateSubject } |
                ForEach-Object { Remove-Item -Path $_.PSPath -Force }

            $certificate = New-SigningCertificate
            Write-Log "新证书指纹：$($certificate.Thumbprint)"
            Register-CertificateTrust -Certificate $certificate

            $signResult = Invoke-Tool -Exe $signtool -Arguments @(
                'sign', '/fd', 'SHA256', '/sm', '/sha1', $certificate.Thumbprint, $msix)
            if ($signResult.Text) { Write-Log $signResult.Text }
        }
        catch {
            Write-Log "重建证书失败：$($_.Exception.Message)"
        }
    }

    if ($signResult.ExitCode -ne 0) {
        Write-Log "错误：签名失败（退出码 $($signResult.ExitCode)）。"
        Write-Log '提示：管理员 PowerShell 执行 certutil -store My 7zAIWin11RCM 可查看证书状态。'
        exit 3
    }

    # 签名结果自检（失败只告警，不阻断：自签名证书的信任链判定较严格）
    $verifyResult = Invoke-Tool -Exe $signtool -Arguments @('verify', '/pa', $msix)
    if ($verifyResult.ExitCode -eq 0) {
        Write-Log '签名校验：通过'
    }
    else {
        Write-Log "签名校验未通过（退出码 $($verifyResult.ExitCode)），继续尝试注册："
        if ($verifyResult.Text) { Write-Log $verifyResult.Text }
    }

    # 3) 注册稀疏包，外部内容指向应用安装目录
    Write-Log "注册扩展包（ExternalLocation=$ExternalPath）…"
    try {
        Add-AppxPackage -Path $msix -ExternalLocation $ExternalPath -ErrorAction Stop
    }
    catch {
        $message = $_.Exception.Message
        Write-Log "注册失败：$message"

        # 0x800B0109 = CERT_E_UNTRUSTEDROOT：自签名证书的根不在受信任根存储里。
        # 追加到「受信任的根证书颁发机构」后重试一次（仅本机，随时可删）。
        if ($message -match '800B0109' -or $message -match '0x800B0109') {
            Write-Log '证书根不受信任，把自签名证书加入「受信任的根证书颁发机构」后重试…'
            $cerPath = Join-Path $WorkRoot '7zAIWin11RCM.cer'
            Export-Certificate -Cert $certificate -FilePath $cerPath -Force | Out-Null
            Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
            Add-AppxPackage -Path $msix -ExternalLocation $ExternalPath -ErrorAction Stop
        }
        else {
            throw
        }
    }

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
