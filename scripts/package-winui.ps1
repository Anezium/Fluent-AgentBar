$ErrorActionPreference = "Stop"

$projectPath = "winui\FluentAgentBar.csproj"
$framework = "net8.0-windows10.0.19041.0"
$runtime = "win-x64"
$configuration = "Release"
$platform = "x64"

[xml]$project = Get-Content $projectPath
$version = $project.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Could not read Version from $projectPath"
}

$repoRoot = (Resolve-Path ".").Path
$releaseBin = Join-Path $repoRoot "winui\bin\$platform\$configuration\$framework\$runtime"
$artifactRoot = Join-Path $repoRoot "artifacts"
$artifactName = "FluentAgentBar-v$version-win-x64"
$artifactDir = Join-Path $artifactRoot $artifactName
$zipPath = "$artifactDir.zip"

function Assert-UnderRepo([string]$PathToCheck) {
    $parent = Split-Path -Parent $PathToCheck
    $resolvedParent = if (Test-Path $parent) {
        (Resolve-Path $parent).Path
    } else {
        (Resolve-Path (Split-Path -Parent $parent)).Path
    }

    if (!$resolvedParent.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to write outside repo: $PathToCheck"
    }
}

Assert-UnderRepo $releaseBin
Assert-UnderRepo $artifactDir

if (Test-Path $releaseBin) {
    Remove-Item -LiteralPath $releaseBin -Recurse -Force
}

dotnet build $projectPath -c $configuration -p:Platform=$platform

foreach ($requiredFile in @("FluentAgentBar.exe", "FluentAgentBar.dll", "FluentAgentBar.pri", "App.xbf", "TaskbarWidgetWindow.xbf")) {
    $path = Join-Path $releaseBin $requiredFile
    if (!(Test-Path $path)) {
        throw "Release output is missing required WinUI file: $requiredFile"
    }
}

if (Test-Path $artifactDir) {
    Remove-Item -LiteralPath $artifactDir -Recurse -Force
}

if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

New-Item -ItemType Directory -Force $artifactRoot | Out-Null
Copy-Item -Path $releaseBin -Destination $artifactDir -Recurse
Compress-Archive -Path (Join-Path $artifactDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

Get-Item (Join-Path $artifactDir "FluentAgentBar.exe"), $zipPath |
    Select-Object FullName, Length
