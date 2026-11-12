param([ValidateSet('Debug','Release')][string]$Configuration='Release', [ValidateSet('x86','x64')][string]$Platform='x86', [switch]$SkipBuild, [string]$OutputDirectory)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$project=Join-Path $repo 'archive\WinForms\src\DoorsIntegration.Desktop\DoorsIntegration.Desktop.csproj'
if (!$SkipBuild) {
    Push-Location (Join-Path $repo 'artifacts\SmokeBuildHost')
    try {
        & dotnet restore $project "-p:Platform=$Platform" --packages (Join-Path $repo '.packages') --configfile (Join-Path $repo 'NuGet.Config') --ignore-failed-sources
        if ($LASTEXITCODE -ne 0) { throw 'Legacy restore failed.' }
        & dotnet build $project -c $Configuration "-p:Platform=$Platform" --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Legacy build failed.' }
    } finally { Pop-Location }
}
$app=Join-Path (Split-Path -Parent $project) "bin\$Platform\$Configuration\net48"
if (!(Test-Path -LiteralPath (Join-Path $app 'DoorsIntegration.Desktop.exe'))) { throw 'Build the archived WinForms project first.' }
$output=if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $repo 'archive\releases\manual-WinForms' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$files=@(Get-ChildItem -LiteralPath $app -File | Where-Object Extension -in '.exe','.dll','.config' | Select-Object -ExpandProperty FullName)
$files+=Join-Path $app 'scripts'
$files+=Join-Path $repo 'archive\WinForms\README.md'
$zip=Join-Path $output "DoorsIntegration-WinForms-$Platform.zip"
Compress-Archive -LiteralPath $files -DestinationPath $zip -Force
Write-Output $zip
