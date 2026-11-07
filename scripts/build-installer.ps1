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
$mcpProjectPath = Join-Path $repoRoot "src\QuerySpace.McpServer\QuerySpace.McpServer.csproj"
$solutionPath = Join-Path $repoRoot "QuerySpace.sln"
$publishDir = Join-Path $repoRoot "publish\QuerySpace"
$mcpPublishDir = Join-Path $publishDir "mcp"
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
    $candidates = New-Object System.Collections.Generic.List[string]

    $fromPath = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($fromPath) {
        $candidates.Add($fromPath.Source)
    }

    $programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    $programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)

    foreach ($root in @($programFilesX86, $programFiles, $localAppData)) {
        if (-not [string]::IsNullOrWhiteSpace($root)) {
            $candidates.Add((Join-Path $root "Inno Setup 6\ISCC.exe"))
            $candidates.Add((Join-Path $root "Programs\Inno Setup 6\ISCC.exe"))
        }
    }

    foreach ($registryPath in @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1",
        "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1"
    )) {
        if (Test-Path -LiteralPath $registryPath) {
            $installLocation = (Get-ItemProperty -LiteralPath $registryPath -ErrorAction SilentlyContinue).InstallLocation
            if (-not [string]::IsNullOrWhiteSpace($installLocation)) {
                $candidates.Add((Join-Path $installLocation "ISCC.exe"))
            }
        }
    }

    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return $candidate
        }
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

if (-not $SkipInstaller) {
    $iscc = Find-InnoCompiler
    if (-not $iscc) {
        throw "Inno Setup 6 compiler was not found. Install Inno Setup 6 from https://jrsoftware.org/isinfo.php, add ISCC.exe to PATH, or rerun with -SkipInstaller to only publish the app folder."
    }
    Write-Host "Inno Setup compiler: $iscc"
}

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

$mcpPublishArgs = @(
    "publish", $mcpProjectPath,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", $selfContained.ToString().ToLowerInvariant(),
    "-p:PublishSingleFile=false",
    "-p:DebugType=embedded",
    "-p:DebugSymbols=false",
    "-o", $mcpPublishDir
)

dotnet @mcpPublishArgs

# Native NuGet packages can include static/import libraries for developers linking
# native code. QuerySpace only loads the corresponding DLLs at runtime, so the
# .lib files are not part of the installed application.
$linkLibraries = Get-ChildItem -LiteralPath $publishDir -Filter "*.lib" -File -Recurse
if ($linkLibraries.Count -gt 0) {
    $linkLibraries | Remove-Item -Force
    Write-Host "Removed $($linkLibraries.Count) unused native link libraries from the publish payload."
}

if ($SkipInstaller) {
    Write-Host "Published application folder:"
    Write-Host $publishDir
    exit 0
}

New-Item -ItemType Directory -Path $installerOutputDir -Force | Out-Null

& $iscc "/DMyAppVersion=$version" $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

Write-Host "Installer output folder:"
Write-Host $installerOutputDir
