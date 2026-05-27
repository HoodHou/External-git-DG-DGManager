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
