param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('x86', 'x64', 'AnyCPU')]
    [string]$Platform = 'x86',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$solutionRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $solutionRoot
try {
    $solutionPlatform = if ($Platform -eq 'AnyCPU') { 'Any CPU' } else { $Platform }
    & dotnet restore DoorsIntegration.sln "-p:Platform=$solutionPlatform" --packages .packages --configfile NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw 'NuGet restore başarısız.' }

    & dotnet build DoorsIntegration.sln --no-restore --configuration $Configuration "-p:Platform=$solutionPlatform"
    if ($LASTEXITCODE -ne 0) { throw 'Build başarısız.' }

    if (-not $SkipTests) {
        $testPlatform = if ($Platform -eq 'AnyCPU') { 'x86' } else { $Platform }
        & dotnet test tests/DoorsIntegration.Tests/DoorsIntegration.Tests.csproj --no-build --no-restore `
            --configuration $Configuration "-p:Platform=$Platform" --settings tests.runsettings `
            --logger 'trx;LogFileName=DoorsIntegration.Tests.trx' --results-directory artifacts/TestResults `
            -- "RunConfiguration.TargetPlatform=$testPlatform"
        if ($LASTEXITCODE -ne 0) { throw 'Testler başarısız.' }
    }
}
finally {
    Pop-Location
}
