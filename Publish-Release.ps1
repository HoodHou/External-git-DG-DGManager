param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    [string]$Runtime = "win-x64",

    [switch]$SelfContained,

    [switch]$SkipRestore,

    [switch]$NoZip,

    [ValidateSet("stable", "beta")]
    [string]$Channel = "stable",

    [string]$ReleaseNotes = "",

    [string]$ReleaseBaseUrl = "https://github.com/HoodHou/External-git-DG-DGManager/releases/download",

    [switch]$IncludeDesktopRuntimeInstaller,

    [string]$DesktopRuntimeVersion = "8.0.26",

    [string]$DesktopRuntimeInstallerUrl = "",

    [switch]$NoManifest
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Project = Join-Path $Root "SVNManager\SVNManager.csproj"
$Artifacts = Join-Path $Root "artifacts"
$PublishRoot = Join-Path $Artifacts "publish"

if (-not (Test-Path -LiteralPath $Project)) {
    throw "Project file not found: $Project"
}

[xml]$ProjectXml = Get-Content -LiteralPath $Project
$VersionNode = $ProjectXml.Project.PropertyGroup | Select-Object -First 1
$Version = $VersionNode.Version
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = "0.0.0"
}

$OutputDirectory = Join-Path $PublishRoot "SVNManager-$Runtime-v$Version"
if (Test-Path -LiteralPath $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $Artifacts | Out-Null
New-Item -ItemType Directory -Force -Path $PublishRoot | Out-Null

$SelfContainedText = if ($SelfContained) { "true" } else { "false" }

if (-not $SkipRestore) {
    Write-Host "Restoring SVNManager for $Runtime..."
    & dotnet restore $Project -r $Runtime --ignore-failed-sources /p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed with exit code $LASTEXITCODE"
    }
}

$PublishArgs = @(
    "publish",
    $Project,
    "-c",
    $Configuration,
    "-r",
    $Runtime,
    "--self-contained",
    $SelfContainedText,
    "--no-restore",
    "-o",
    $OutputDirectory,
    "/p:NuGetAudit=false",
    "/p:PublishSingleFile=false"
)

Write-Host "Publishing SVNManager $Version ($Configuration, $Runtime, self-contained=$SelfContainedText)..."
& dotnet @PublishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory "SVNManager.exe"))) {
    throw "Publish output is missing SVNManager.exe: $OutputDirectory"
}

if ($IncludeDesktopRuntimeInstaller) {
    if ([string]::IsNullOrWhiteSpace($DesktopRuntimeVersion)) {
        throw "DesktopRuntimeVersion is required when IncludeDesktopRuntimeInstaller is set."
    }

    $InstallerName = "windowsdesktop-runtime-$DesktopRuntimeVersion-win-x64.exe"
    $InstallerUrl = if ([string]::IsNullOrWhiteSpace($DesktopRuntimeInstallerUrl)) {
        "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/$DesktopRuntimeVersion/$InstallerName"
    } else {
        $DesktopRuntimeInstallerUrl
    }

    $RedistCache = Join-Path $Artifacts "redist"
    $CachedInstaller = Join-Path $RedistCache $InstallerName
    New-Item -ItemType Directory -Force -Path $RedistCache | Out-Null

    if (-not (Test-Path -LiteralPath $CachedInstaller) -or (Get-Item -LiteralPath $CachedInstaller).Length -eq 0) {
        Write-Host "Downloading .NET Desktop Runtime $DesktopRuntimeVersion installer..."
        Invoke-WebRequest -Uri $InstallerUrl -OutFile $CachedInstaller
    } else {
        Write-Host "Using cached .NET Desktop Runtime installer: $CachedInstaller"
    }

    $PackageRedist = Join-Path $OutputDirectory "redist"
    New-Item -ItemType Directory -Force -Path $PackageRedist | Out-Null
    $PackageInstaller = Join-Path $PackageRedist $InstallerName
    Copy-Item -LiteralPath $CachedInstaller -Destination $PackageInstaller -Force

    $InstallerSha256 = (Get-FileHash -LiteralPath $PackageInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath (Join-Path $PackageRedist "$InstallerName.sha256.txt") -Encoding UTF8 -Value @(
        "$InstallerSha256  $InstallerName",
        "Source: $InstallerUrl"
    )

    Set-Content -LiteralPath (Join-Path $OutputDirectory "Install-DotNet-DesktopRuntime.cmd") -Encoding ASCII -Value @(
        "@echo off",
        "setlocal",
        "set ""DIR=%~dp0""",
        "set ""INSTALLER=%DIR%redist\$InstallerName""",
        "if not exist ""%INSTALLER%"" (",
        "  echo Missing installer: %INSTALLER%",
        "  pause",
        "  exit /b 1",
        ")",
        "echo Installing Microsoft .NET Desktop Runtime $DesktopRuntimeVersion x64...",
        "start /wait """" ""%INSTALLER%"" /install /passive /norestart",
        "set ""EXITCODE=%ERRORLEVEL%""",
        "if not ""%EXITCODE%""==""0"" (",
        "  echo Install failed. Exit code: %EXITCODE%",
        "  pause",
        "  exit /b %EXITCODE%",
        ")",
        "echo Done. You can now run SVNManager.exe.",
        "pause"
    )

    Set-Content -LiteralPath (Join-Path $OutputDirectory "运行环境说明.txt") -Encoding UTF8 -Value @(
        "Dream SVNManager v$Version 运行环境说明",
        "",
        "本包已内置 Microsoft .NET 8 Desktop Runtime x64 安装包：",
        "redist\$InstallerName",
        "",
        "首次使用或系统提示缺少 .NET 时，请先运行：",
        "Install-DotNet-DesktopRuntime.cmd",
        "",
        "安装完成后运行 SVNManager.exe。",
        "",
        "说明：.NET Desktop Runtime 已包含普通 .NET Runtime；WPF/WinForms 桌面程序需要 Desktop Runtime。",
        "SVN 操作仍需要本机已安装 TortoiseSVN command line tools 或 Apache Subversion，并且 svn.exe 在 PATH 中。"
    )

    Write-Host ".NET Desktop Runtime installer included: $PackageInstaller"
    Write-Host ".NET Desktop Runtime installer SHA256: $InstallerSha256"
}

if (-not $NoZip) {
    $ZipPath = Join-Path $Artifacts "DreamSVNManager-$Runtime-v$Version.zip"
    if (Test-Path -LiteralPath $ZipPath) {
        Remove-Item -LiteralPath $ZipPath -Force
    }

    Compress-Archive -Path (Join-Path $OutputDirectory "*") -DestinationPath $ZipPath -Force
    Write-Host "Release zip: $ZipPath"

    if (-not $NoManifest) {
        $ZipName = Split-Path -Leaf $ZipPath
        $Sha256 = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $DownloadUrl = "$ReleaseBaseUrl/v$Version/$ZipName"
        $ReleasePageBaseUrl = $ReleaseBaseUrl -replace "/releases/download$", "/releases/tag"
        $ReleaseUrl = "$ReleasePageBaseUrl/v$Version"
        $ManifestPath = Join-Path $Root "update.json"
        $Manifest = [ordered]@{}
        if (Test-Path -LiteralPath $ManifestPath) {
            $Existing = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
            foreach ($Property in $Existing.PSObject.Properties) {
                $Manifest[$Property.Name] = $Property.Value
            }
        }

        $Manifest[$Channel] = [ordered]@{
            version = $Version
            tag = "v$Version"
            assetName = $ZipName
            url = $DownloadUrl
            sha256 = $Sha256
            required = $false
            notes = $ReleaseNotes
            releaseUrl = $ReleaseUrl
            publishedAt = (Get-Date).ToString("yyyy-MM-dd")
        }
        $Manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ManifestPath -Encoding UTF8
        Write-Host "SHA256: $Sha256"
        Write-Host "Update manifest: $ManifestPath ($Channel)"
    }
}

Write-Host "Publish output: $OutputDirectory"
