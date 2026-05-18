param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    [string]$Runtime = "win-x64",

    [switch]$SelfContained,

    [switch]$SkipRestore,

    [switch]$NoZip
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
}

Write-Host "Publish output: $OutputDirectory"
