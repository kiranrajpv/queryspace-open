param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Runtime = "win-x64",

    [switch]$FrameworkDependent,

    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$projectPath = Join-Path $repoRoot "src\QuerySpace\QuerySpace.csproj"
$solutionPath = Join-Path $repoRoot "QuerySpace.sln"
$publishDir = Join-Path $repoRoot "publish\QuerySpace"
$installerScript = Join-Path $repoRoot "installer\QuerySpace.iss"
$installerOutputDir = Join-Path $repoRoot "installer-output"

function Read-ProjectVersion {
    param([string]$Path)

    [xml]$project = Get-Content -LiteralPath $Path
    $versionNode = $project.Project.PropertyGroup.Version | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($versionNode)) {
        return "0.1.0"
    }

    return $versionNode.Trim()
}

function Find-InnoCompiler {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return $candidate
        }
    }

    $fromPath = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($fromPath) {
        return $fromPath.Source
    }

    return $null
}

$version = Read-ProjectVersion -Path $projectPath
$selfContained = -not $FrameworkDependent.IsPresent

Write-Host "Building QuerySpace installer project"
Write-Host "Repository: $repoRoot"
Write-Host "Configuration: $Configuration"
Write-Host "Runtime: $Runtime"
Write-Host "Version: $version"
Write-Host "Self-contained: $selfContained"

dotnet restore $solutionPath

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

$publishArgs = @(
    "publish", $projectPath,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", $selfContained.ToString().ToLowerInvariant(),
    "-p:PublishSingleFile=false",
    "-p:DebugType=embedded",
    "-p:DebugSymbols=false",
    "-o", $publishDir
)

dotnet @publishArgs

if ($SkipInstaller) {
    Write-Host "Published application folder:"
    Write-Host $publishDir
    exit 0
}

$iscc = Find-InnoCompiler
if (-not $iscc) {
    throw "Inno Setup 6 compiler was not found. Install Inno Setup 6, or rerun with -SkipInstaller to only publish the app folder."
}

New-Item -ItemType Directory -Path $installerOutputDir -Force | Out-Null

& $iscc "/DMyAppVersion=$version" $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

Write-Host "Installer output folder:"
Write-Host $installerOutputDir
