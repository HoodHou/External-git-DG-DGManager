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

    [switch]$CreateDesktopRuntimePackage,

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
$RuntimePackagePath = $null
$RuntimePackageName = ""
$RuntimePackageSha256 = ""

function Resolve-DesktopRuntimeInstaller {
    param(
        [string]$RuntimeVersion,
        [string]$InstallerUrlOverride
    )

    if ([string]::IsNullOrWhiteSpace($RuntimeVersion)) {
        throw "DesktopRuntimeVersion is required when packaging the desktop runtime installer."
    }

    $installerName = "windowsdesktop-runtime-$RuntimeVersion-win-x64.exe"
    $installerUrl = if ([string]::IsNullOrWhiteSpace($InstallerUrlOverride)) {
        "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/$RuntimeVersion/$installerName"
    } else {
        $InstallerUrlOverride
    }

    $redistCache = Join-Path $Artifacts "redist"
    $cachedInstaller = Join-Path $redistCache $installerName
    New-Item -ItemType Directory -Force -Path $redistCache | Out-Null

    if (-not (Test-Path -LiteralPath $cachedInstaller) -or (Get-Item -LiteralPath $cachedInstaller).Length -eq 0) {
        Write-Host "Downloading .NET Desktop Runtime $RuntimeVersion installer..."
        Invoke-WebRequest -Uri $installerUrl -OutFile $cachedInstaller
    } else {
        Write-Host "Using cached .NET Desktop Runtime installer: $cachedInstaller"
    }

    [pscustomobject]@{
        Name = $installerName
        Url = $installerUrl
        Path = $cachedInstaller
        Sha256 = (Get-FileHash -LiteralPath $cachedInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Write-DesktopRuntimeInstallFiles {
    param(
        [string]$DestinationDirectory,
        [object]$Installer,
        [string]$AppVersion
    )

    $packageRedist = Join-Path $DestinationDirectory "redist"
    New-Item -ItemType Directory -Force -Path $packageRedist | Out-Null
    $packageInstaller = Join-Path $packageRedist $Installer.Name
    Copy-Item -LiteralPath $Installer.Path -Destination $packageInstaller -Force

    Set-Content -LiteralPath (Join-Path $packageRedist "$($Installer.Name).sha256.txt") -Encoding UTF8 -Value @(
        "$($Installer.Sha256)  $($Installer.Name)",
        "Source: $($Installer.Url)"
    )

    Set-Content -LiteralPath (Join-Path $DestinationDirectory "Install-DotNet-DesktopRuntime.cmd") -Encoding ASCII -Value @(
        "@echo off",
        "setlocal",
        "set ""DIR=%~dp0""",
        "set ""INSTALLER=%DIR%redist\$($Installer.Name)""",
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

    Set-Content -LiteralPath (Join-Path $DestinationDirectory "运行环境说明.txt") -Encoding UTF8 -Value @(
        "Dream SVNManager v$AppVersion 运行环境说明",
        "",
        "本包包含 Microsoft .NET 8 Desktop Runtime x64 安装包：",
        "redist\$($Installer.Name)",
        "",
        "首次使用或系统提示缺少 .NET 时，请先运行：",
        "Install-DotNet-DesktopRuntime.cmd",
        "",
        "安装完成后运行 SVNManager.exe。",
        "",
        "说明：.NET Desktop Runtime 已包含普通 .NET Runtime；WPF/WinForms 桌面程序需要 Desktop Runtime。",
        "SVN 操作仍需要本机已安装 TortoiseSVN command line tools 或 Apache Subversion，并且 svn.exe 在 PATH 中。"
    )
}

function Write-AppRuntimeNotice {
    param(
        [string]$DestinationDirectory,
        [string]$AppVersion,
        [string]$RuntimePackageFileName
    )

    $runtimePackageText = if ([string]::IsNullOrWhiteSpace($RuntimePackageFileName)) {
        "同一 GitHub Release 页面中的 .NET Desktop Runtime 运行环境包"
    } else {
        $RuntimePackageFileName
    }

    Set-Content -LiteralPath (Join-Path $DestinationDirectory "运行环境说明.txt") -Encoding UTF8 -Value @(
        "Dream SVNManager v$AppVersion 运行环境说明",
        "",
        "主程序 zip 不内置 .NET 安装包，文件会更小。",
        "",
        "如果系统提示缺少 .NET，请下载并解压：",
        $runtimePackageText,
        "",
        "然后运行其中的 Install-DotNet-DesktopRuntime.cmd。",
        "",
        "已安装 .NET 8 Desktop Runtime 的机器可以直接运行 SVNManager.exe。",
        "SVN 操作仍需要本机已安装 TortoiseSVN command line tools 或 Apache Subversion，并且 svn.exe 在 PATH 中。"
    )
}

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

if ($IncludeDesktopRuntimeInstaller -or $CreateDesktopRuntimePackage) {
    $DesktopRuntimeInstaller = Resolve-DesktopRuntimeInstaller $DesktopRuntimeVersion $DesktopRuntimeInstallerUrl

    if ($IncludeDesktopRuntimeInstaller) {
        Write-DesktopRuntimeInstallFiles $OutputDirectory $DesktopRuntimeInstaller $Version
        Write-Host ".NET Desktop Runtime installer included in app package."
        Write-Host ".NET Desktop Runtime installer SHA256: $($DesktopRuntimeInstaller.Sha256)"
    }

    if ($CreateDesktopRuntimePackage) {
        $RuntimePackageName = "DreamSVNManager-dotnet-runtime-$Runtime-v$Version.zip"
        $runtimePackageDirectory = Join-Path $PublishRoot "DotNetDesktopRuntime-$Runtime-v$Version"
        if (Test-Path -LiteralPath $runtimePackageDirectory) {
            Remove-Item -LiteralPath $runtimePackageDirectory -Recurse -Force
        }

        New-Item -ItemType Directory -Force -Path $runtimePackageDirectory | Out-Null
        Write-DesktopRuntimeInstallFiles $runtimePackageDirectory $DesktopRuntimeInstaller $Version

        $RuntimePackagePath = Join-Path $Artifacts $RuntimePackageName
        if (Test-Path -LiteralPath $RuntimePackagePath) {
            Remove-Item -LiteralPath $RuntimePackagePath -Force
        }

        Compress-Archive -Path (Join-Path $runtimePackageDirectory "*") -DestinationPath $RuntimePackagePath -Force
        $RuntimePackageSha256 = (Get-FileHash -LiteralPath $RuntimePackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-Host "Runtime package zip: $RuntimePackagePath"
        Write-Host "Runtime package SHA256: $RuntimePackageSha256"
    }
}

if (-not $SelfContained -and -not $IncludeDesktopRuntimeInstaller) {
    Write-AppRuntimeNotice $OutputDirectory $Version $RuntimePackageName
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

        $ChannelManifest = [ordered]@{
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

        if (-not [string]::IsNullOrWhiteSpace($RuntimePackageName)) {
            $ChannelManifest.runtimeAssetName = $RuntimePackageName
            $ChannelManifest.runtimeUrl = "$ReleaseBaseUrl/v$Version/$RuntimePackageName"
            $ChannelManifest.runtimeSha256 = $RuntimePackageSha256
        }

        $Manifest[$Channel] = $ChannelManifest
        $Manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ManifestPath -Encoding UTF8
        Write-Host "SHA256: $Sha256"
        Write-Host "Update manifest: $ManifestPath ($Channel)"
    }
}

Write-Host "Publish output: $OutputDirectory"
