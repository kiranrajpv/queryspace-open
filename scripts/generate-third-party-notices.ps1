param(
    [string]$ProjectPath = "$PSScriptRoot\..\src\QuerySpace\QuerySpace.csproj",
    [string]$OutputPath = "$PSScriptRoot\..\THIRD-PARTY-NOTICES.md"
)

$ErrorActionPreference = "Stop"

$ProjectPath = [System.IO.Path]::GetFullPath($ProjectPath)
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$nugetRoot = Join-Path $env:USERPROFILE ".nuget\packages"

dotnet restore $ProjectPath | Out-Null

$lines = dotnet list $ProjectPath package --include-transitive
$packages = New-Object System.Collections.Generic.List[object]
$section = ""
foreach ($line in $lines) {
    if ($line -match "Top-level Package") {
        $section = "Direct"
        continue
    }
    if ($line -match "Transitive Package") {
        $section = "Transitive"
        continue
    }
    if ($line -match "^\s*>\s+(\S+)\s+(?:(\S+)\s+)?([0-9][^\s]*)\s*$") {
        $packages.Add([pscustomobject]@{
            Name = $matches[1]
            Version = $matches[3]
            Relationship = $section
        })
    }
}

function Get-NuspecMetadata {
    param([string]$Name, [string]$Version)

    $packageDir = Join-Path $nugetRoot ($Name.ToLowerInvariant())
    $packageDir = Join-Path $packageDir ($Version.ToLowerInvariant())
    $nuspec = Join-Path $packageDir ($Name.ToLowerInvariant() + ".nuspec")
    if (!(Test-Path $nuspec)) {
        $nuspec = Get-ChildItem -Path $packageDir -Filter "*.nuspec" -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
    }

    $metadata = [ordered]@{
        PackageDir = $packageDir
        License = ""
        LicenseType = ""
        LicenseUrl = ""
        ProjectUrl = ""
        Authors = ""
        Copyright = ""
        PackageLicenseFile = ""
    }

    if ($nuspec -and (Test-Path $nuspec)) {
        [xml]$xml = Get-Content $nuspec
        $licenseNode = $xml.SelectSingleNode("//*[local-name()='license']")
        if ($licenseNode) {
            $metadata.License = (Coalesce-Text $licenseNode.InnerText "").Trim()
            $metadata.LicenseType = (Coalesce-Text $licenseNode.GetAttribute("type") "").Trim()
            if ($metadata.LicenseType -eq "file") {
                $metadata.PackageLicenseFile = $metadata.License
            }
        }
        $licenseUrlNode = $xml.SelectSingleNode("//*[local-name()='licenseUrl']")
        if ($licenseUrlNode) { $metadata.LicenseUrl = (Coalesce-Text $licenseUrlNode.InnerText "").Trim() }
        $projectUrlNode = $xml.SelectSingleNode("//*[local-name()='projectUrl']")
        if ($projectUrlNode) { $metadata.ProjectUrl = (Coalesce-Text $projectUrlNode.InnerText "").Trim() }
        $authorsNode = $xml.SelectSingleNode("//*[local-name()='authors']")
        if ($authorsNode) { $metadata.Authors = (Coalesce-Text $authorsNode.InnerText "").Trim() }
        $copyrightNode = $xml.SelectSingleNode("//*[local-name()='copyright']")
        if ($copyrightNode) { $metadata.Copyright = (Coalesce-Text $copyrightNode.InnerText "").Trim() }
    }

    [pscustomobject]$metadata
}

function Find-NoticeFiles {
    param([object]$Metadata)

    $files = New-Object System.Collections.Generic.List[string]
    if (!(Test-Path $Metadata.PackageDir)) { return $files }

    if ($Metadata.PackageLicenseFile) {
        $declared = Join-Path $Metadata.PackageDir $Metadata.PackageLicenseFile
        if (Test-Path $declared) { $files.Add([System.IO.Path]::GetFullPath($declared)) }
    }

    $patterns = @(
        "LICENSE", "LICENSE.*", "LICENCE", "LICENCE.*",
        "NOTICE", "NOTICE.*",
        "COPYING", "COPYING.*",
        "THIRD-PARTY-NOTICES", "THIRD-PARTY-NOTICES.*",
        "ThirdPartyNotices*", "ThirdPartyNotice*"
    )

    foreach ($pattern in $patterns) {
        Get-ChildItem -Path $Metadata.PackageDir -Filter $pattern -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Length -lt 1048576 -and
                $_.Extension -notin @(".dll", ".exe", ".pdb", ".nupkg", ".snupkg")
            } |
            ForEach-Object { $files.Add($_.FullName) }
    }

    $files | Select-Object -Unique | Sort-Object
}

function Coalesce-Text {
    param([string]$Value, [string]$Fallback = "")
    if ($null -eq $Value -or $Value.Trim() -eq "") { return $Fallback }
    return $Value
}

function Escape-MarkdownCell {
    param([string]$Value)
    $text = Coalesce-Text $Value "Not declared in local package metadata"
    return $text.Replace("|", "\|").Replace([string][char]0x60, "'")
}

$enriched = foreach ($package in $packages) {
    $metadata = Get-NuspecMetadata -Name $package.Name -Version $package.Version
    $noticeFiles = @(Find-NoticeFiles -Metadata $metadata)
    [pscustomobject]@{
        Name = $package.Name
        Version = $package.Version
        Relationship = $package.Relationship
        License = $metadata.License
        LicenseType = $metadata.LicenseType
        LicenseUrl = $metadata.LicenseUrl
        ProjectUrl = $metadata.ProjectUrl
        Authors = $metadata.Authors
        Copyright = $metadata.Copyright
        NoticeFiles = $noticeFiles
    }
}

$sb = [System.Text.StringBuilder]::new()
$null = $sb.AppendLine("# Third-party notices")
$null = $sb.AppendLine()
$null = $sb.AppendLine("Generated from the local NuGet package cache for QuerySpace.csproj.")
$null = $sb.AppendLine()
$null = $sb.AppendLine("This file is an engineering aid for compliance review. It is not legal advice. Regenerate it after changing package versions.")
$null = $sb.AppendLine()
$null = $sb.AppendLine("Generation command:")
$null = $sb.AppendLine()
$null = $sb.AppendLine('```powershell')
$null = $sb.AppendLine(".\scripts\generate-third-party-notices.ps1")
$null = $sb.AppendLine('```')
$null = $sb.AppendLine()
$null = $sb.AppendLine("## Package inventory")
$null = $sb.AppendLine()
$null = $sb.AppendLine("| Relationship | Package | Version | License metadata | License URL | Project URL | Authors |")
$null = $sb.AppendLine("|---|---|---:|---|---|---|---|")
foreach ($item in $enriched | Sort-Object Relationship, Name) {
    $license = if ($item.LicenseType -eq "file") { "file: $($item.License)" } elseif ($item.License) { $item.License } else { "" }
    $null = $sb.AppendLine("| $($item.Relationship) | $($item.Name) | $($item.Version) | $(Escape-MarkdownCell $license) | $(Escape-MarkdownCell $item.LicenseUrl) | $(Escape-MarkdownCell $item.ProjectUrl) | $(Escape-MarkdownCell $item.Authors) |")
}

$null = $sb.AppendLine()
$null = $sb.AppendLine("## Package-provided license and notice files")
$null = $sb.AppendLine()
$null = $sb.AppendLine("The sections below include license/notice file contents found in the restored NuGet package folders. Packages that only declare a license expression may not include a separate license file in the package.")

foreach ($item in $enriched | Sort-Object Name) {
    if ($item.NoticeFiles.Count -eq 0) { continue }
    $null = $sb.AppendLine()
    $null = $sb.AppendLine("### $($item.Name) $($item.Version)")
    $null = $sb.AppendLine()
    $license = if ($item.LicenseType -eq "file") { "file: $($item.License)" } elseif ($item.License) { $item.License } else { "Not declared in local package metadata" }
    $null = $sb.AppendLine("- Relationship: $($item.Relationship)")
    $null = $sb.AppendLine("- License metadata: $license")
    if ($item.LicenseUrl) { $null = $sb.AppendLine("- License URL: $($item.LicenseUrl)") }
    if ($item.ProjectUrl) { $null = $sb.AppendLine("- Project URL: $($item.ProjectUrl)") }
    foreach ($file in $item.NoticeFiles) {
        $relative = Split-Path $file -Leaf
        $content = Get-Content -Raw -LiteralPath $file -ErrorAction SilentlyContinue
        if ($null -eq $content -or $content.Trim() -eq "") { continue }
        $null = $sb.AppendLine()
        $null = $sb.AppendLine("#### $relative")
        $null = $sb.AppendLine()
        $null = $sb.AppendLine('```text')
        $null = $sb.AppendLine($content.TrimEnd())
        $null = $sb.AppendLine('```')
    }
}

$outDir = Split-Path $OutputPath -Parent
if (!(Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
[System.IO.File]::WriteAllText($OutputPath, $sb.ToString(), [System.Text.UTF8Encoding]::new($false))
Write-Host "Wrote $OutputPath"
