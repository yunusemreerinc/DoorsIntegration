param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('x86', 'x64')]
    [string]$Platform = 'x86',
    [switch]$SkipBuild,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$solutionRoot = Split-Path -Parent $PSScriptRoot
if (-not $SkipBuild) {
    # Packaging builds only; native integration tests must remain a separate explicitly configured step.
    & (Join-Path $PSScriptRoot 'Build.ps1') -Configuration $Configuration -Platform $Platform -SkipTests
}
$appDirectory = Join-Path $solutionRoot "src\DoorsIntegration.Wpf\bin\$Platform\$Configuration\net48"
foreach ($required in 'DoorsIntegration.Wpf.exe', 'DoorsIntegration.cmd', 'DoorsIntegration.Core.dll', 'DoorsIntegration.Application.dll', 'DoorsIntegration.Com.dll', 'DoorsIntegration.Dxl.dll', 'DoorsIntegration.Export.dll', 'DoorsIntegration.Processing.dll', 'DocumentFormat.OpenXml.dll', 'DocumentFormat.OpenXml.Framework.dll') {
    if (-not (Test-Path -LiteralPath (Join-Path $appDirectory $required))) { throw "Build the WPF application before packaging: missing $required" }
}
foreach ($script in 'discovery.dxl', 'discovery-ping.dxl', 'requirements.dxl', 'links.dxl', 'module-create.dxl', 'requirement-write.dxl', 'link-write.dxl') {
    if (-not (Test-Path -LiteralPath (Join-Path $appDirectory "scripts\dxl\$script"))) { throw "Missing external DXL script: $script" }
}
if (-not (Test-Path -LiteralPath (Join-Path $appDirectory 'scripts\Read-DiscoveryDebug.ps1'))) { throw 'Missing discovery debug reader script' }
$artifacts = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $solutionRoot 'artifacts\releases\current' }
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$archive = Join-Path $artifacts "DoorsIntegration-Wpf-$Platform.zip"
$files = @(Get-ChildItem -LiteralPath $appDirectory -File | Where-Object { $_.Extension -in '.exe', '.dll', '.config', '.cmd' } | Select-Object -ExpandProperty FullName)
$files += Join-Path $appDirectory 'scripts'
$files += Join-Path $solutionRoot 'README.md'
$files += Join-Path $solutionRoot 'docs'
Compress-Archive -LiteralPath $files -DestinationPath $archive -Force
Write-Output $archive
