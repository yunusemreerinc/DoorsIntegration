param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('x86', 'x64')]
    [string]$Platform = 'x86',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$solutionRoot = Split-Path -Parent $PSScriptRoot
if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'Build.ps1') -Configuration $Configuration -Platform $Platform
}
$appDirectory = Join-Path $solutionRoot "src\DoorsIntegration.Console\bin\$Platform\$Configuration\net48"
foreach ($requiredFile in 'DoorsIntegration.Console.exe', 'doors.cmd', 'DoorsIntegration.Export.dll', 'DoorsIntegration.Processing.dll', 'DocumentFormat.OpenXml.dll', 'DocumentFormat.OpenXml.Framework.dll') {
    if (-not (Test-Path -LiteralPath (Join-Path $appDirectory $requiredFile))) {
        throw 'Build the console application before packaging.'
    }
}
$artifactDirectory = Join-Path $solutionRoot 'artifacts'
New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
$archivePath = Join-Path $artifactDirectory "DoorsIntegration-Console-$Platform.zip"
$packageFiles = @(Get-ChildItem -LiteralPath $appDirectory -File |
    Where-Object { $_.Extension -in '.exe', '.dll', '.config', '.cmd' } | Select-Object -ExpandProperty FullName)
$packageFiles += Join-Path $solutionRoot 'README.md'
$packageFiles += Join-Path $solutionRoot 'docs'
foreach ($scriptName in 'discovery.dxl', 'requirements.dxl', 'links.dxl', 'module-create.dxl', 'requirement-write.dxl', 'link-write.dxl') {
    if (-not (Test-Path -LiteralPath (Join-Path $appDirectory "scripts\dxl\$scriptName"))) {
        throw "Build the console application to copy the external $scriptName script before packaging."
    }
}
$packageFiles += Join-Path $appDirectory 'scripts'
foreach ($profileName in 'requirements-quality.example.xml', 'do178c-review.example.xml', 'do200b-review.example.xml', 'do330-review.example.xml') {
    if (-not (Test-Path -LiteralPath (Join-Path $appDirectory "scripts\quality\$profileName"))) { throw "Build the Processing project to copy $profileName before packaging." }
}
Compress-Archive -LiteralPath $packageFiles -DestinationPath $archivePath -Force
Write-Output $archivePath
