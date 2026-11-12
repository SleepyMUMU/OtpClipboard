param(
    [ValidateSet('x64', 'arm64', 'x86')]
    [string]$Arch = 'x64',
    # 签名证书指纹（CurrentUser\My 中须有私钥）；默认取 packaging\OtpClipboard-Dev.cer 的指纹。
    [string]$CertThumbprint,
    # 跳过构建，直接签名并打包已有的 src\OtpClipboard\bin\installer 里的 MSIX。
    [switch]$NoBuild,
    # 只生成解压后双击安装.cmd的完整离线安装包，不需要 Inno Setup。
    [switch]$ZipOnly
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$appDir = Join-Path $projectRoot 'src\OtpClipboard'
$projectFile = Join-Path $appDir 'OtpClipboard.csproj'
$installerSource = Join-Path $projectRoot 'packaging'
$certFile = Join-Path $installerSource 'OtpClipboard-Dev.cer'
$nugetRoot = Join-Path $env:USERPROFILE '.nuget\packages'

[xml]$manifest = Get-Content -LiteralPath (Join-Path $appDir 'Package.appxmanifest') -Encoding UTF8
$version = $manifest.Package.Identity.Version
$publisher = $manifest.Package.Identity.Publisher

[xml]$project = Get-Content -LiteralPath $projectFile -Encoding UTF8
function Get-PackageVersion([string]$id) {
    $ref = $project.Project.ItemGroup.PackageReference | Where-Object Include -eq $id | Select-Object -First 1
    if (-not $ref) { throw "OtpClipboard.csproj 中没有 $id 的包引用。" }
    $ref.Version
}

$winapp = Join-Path $nugetRoot "microsoft.windows.sdk.buildtools.winapp\$(Get-PackageVersion 'Microsoft.Windows.SDK.BuildTools.WinApp')\tools\win-x64\winapp.exe"
$runtimeMsix = Join-Path $nugetRoot "microsoft.windowsappsdk.runtime\$(Get-PackageVersion 'Microsoft.WindowsAppSDK.Runtime')\tools\MSIX\win10-$Arch\Microsoft.WindowsAppRuntime.2.msix"
foreach ($path in $winapp, $runtimeMsix) {
    if (-not (Test-Path -LiteralPath $path)) { throw "找不到 $path，请先执行 dotnet restore。" }
}

if (-not $CertThumbprint) {
    $CertThumbprint = (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($certFile)).Thumbprint
}
$signingCert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $CertThumbprint -and $_.HasPrivateKey }
if (-not $signingCert) { throw "CurrentUser\My 中没有指纹为 $CertThumbprint 且带私钥的证书。" }
if ($signingCert.Subject -ne $publisher) {
    throw "证书主题 $($signingCert.Subject) 与清单 Publisher $publisher 不一致，Windows 会拒绝安装。"
}

$msixName = "OtpClipboard_${version}_$Arch.msix"
$buildDir = Join-Path $appDir 'bin\installer'
$msixPath = Join-Path $buildDir $msixName
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null

if (-not $NoBuild) {
    Write-Host "构建并打包 $msixName…"
    & $winapp package $projectFile -c Release --arch $Arch --no-sign --output $msixPath
    if ($LASTEXITCODE -ne 0) { throw "winapp package 失败，退出码：$LASTEXITCODE" }
}
if (-not (Test-Path -LiteralPath $msixPath)) { throw "找不到 $msixPath" }

Write-Host '签名…'
& $winapp tool signtool sign /fd SHA256 /sha1 $CertThumbprint $msixPath
if ($LASTEXITCODE -ne 0) { throw "签名失败，退出码：$LASTEXITCODE" }

# 组装安装包目录：既是 exe 的输入，也可单独分发（双击“安装.cmd”安装）。
$distRoot = Join-Path $projectRoot 'dist'
$stageDir = Join-Path $distRoot "OtpClipboard-$version-Installer"
$stageDir = [System.IO.Path]::GetFullPath($stageDir)
$distPrefix = [System.IO.Path]::GetFullPath($distRoot).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
if (-not $stageDir.StartsWith($distPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw '安装包暂存目录超出 dist，拒绝清理。'
}
if (Test-Path -LiteralPath $stageDir) { Remove-Item -LiteralPath $stageDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

Copy-Item -LiteralPath $msixPath, $runtimeMsix, $certFile, (Join-Path $projectRoot 'README.md') -Destination $stageDir
foreach ($name in 'Install.ps1', 'Uninstall.ps1', '安装.cmd', '卸载.cmd', '安装说明.txt') {
    Copy-Item -LiteralPath (Join-Path $installerSource $name) -Destination $stageDir
}

$zipPath = Join-Path $distRoot "OtpClipboard-$version-$Arch-Configured.zip"
Compress-Archive -LiteralPath $stageDir -DestinationPath $zipPath -Force
$checksumPath = Join-Path $distRoot 'SHA256SUMS.txt'
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
[IO.File]::WriteAllText($checksumPath, "$zipHash  $([IO.Path]::GetFileName($zipPath))`n", [Text.UTF8Encoding]::new($false))
if ($ZipOnly) {
    Write-Host "完整离线安装包：$zipPath"
    Write-Host "SHA-256：$checksumPath"
    return
}

# 编译 Inno Setup 安装程序 exe。
$iscc = @(
    (Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue).Source
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $iscc) { throw '找不到 Inno Setup 6 的 ISCC.exe。可用 winget install JRSoftware.InnoSetup 安装。' }

# 包系列名 = 包名 + "_" + 发布者 ID（Publisher 的 UTF-16LE SHA-256 前 8 字节，Base32 编码成 13 位）。
$hash = [Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::Unicode.GetBytes($publisher))
$bits = (($hash[0..7] | ForEach-Object { [Convert]::ToString($_, 2).PadLeft(8, '0') }) -join '') + '0'
$alphabet = '0123456789abcdefghjkmnpqrstvwxyz'
$publisherId = -join (0..12 | ForEach-Object { $alphabet[[Convert]::ToInt32($bits.Substring($_ * 5, 5), 2)] })
$packageFamilyName = "$($manifest.Package.Identity.Name)_$publisherId"

$setupPath = Join-Path $distRoot "OtpClipboard-$version-Setup.exe"
Write-Host '编译安装程序 exe…'
& $iscc /Q "/DAppVersion=$version" "/DAppMsix=$msixName" "/DStageDir=$stageDir" "/DOutputDir=$distRoot" `
    "/DAppDir=$appDir" "/DPackageFamilyName=$packageFamilyName" (Join-Path $installerSource 'OtpClipboard.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败，退出码：$LASTEXITCODE" }

Write-Host "`n安装包已生成：" -ForegroundColor Green
Write-Host "  安装程序：$setupPath（$([math]::Round((Get-Item $setupPath).Length / 1MB, 1)) MB）"
Write-Host "  脚本版安装目录：$stageDir"

$setupHash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash
$checksumPath = Join-Path $distRoot 'SHA256SUMS.txt'
[IO.File]::WriteAllText(
    $checksumPath,
    "$zipHash  $([IO.Path]::GetFileName($zipPath))`n$setupHash  $([IO.Path]::GetFileName($setupPath))`n",
    [Text.UTF8Encoding]::new($false)
)
Write-Host "  SHA-256：$checksumPath"
