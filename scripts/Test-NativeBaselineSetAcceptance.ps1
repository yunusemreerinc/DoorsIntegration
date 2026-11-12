[CmdletBinding()]
param([ValidateSet('x86','x64')][string]$Architecture = 'x64', [string]$OutputDirectory, [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $repo ('artifacts\NativeBaselineSet\' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + $Architecture) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a new evidence directory; previous evidence is never replaced.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$oldMode = $env:DOORS_TEST_MODE; $oldCli = $env:DOTNET_CLI_HOME; $oldEvidence = $env:DOORS_NATIVE_SET_ARTIFACT_ROOT
$testExit = -1; $build = 'NOT_RUN'; $errorMessage = $null
try {
    # This runner NEVER enables native writes, even if the calling shell has live test variables.
    $env:DOORS_TEST_MODE = 'disabled'; $env:DOTNET_CLI_HOME = Join-Path $repo '.cli'; $env:DOORS_NATIVE_SET_ARTIFACT_ROOT = Join-Path $output 'mock-evidence'
    Push-Location (Join-Path $repo 'artifacts\SmokeBuildHost')
    try {
        if (-not $NoBuild) {
            & dotnet restore ../../DoorsIntegration.sln "-p:Platform=$Architecture" --packages ../../.packages --configfile ../../NuGet.Config --ignore-failed-sources *> (Join-Path $output 'restore.log')
            if ($LASTEXITCODE -ne 0) { $build = 'FAIL'; throw 'Restore failed.' }
            & dotnet build ../../DoorsIntegration.sln -c Release "-p:Platform=$Architecture" --no-restore *> (Join-Path $output 'build.log')
            if ($LASTEXITCODE -ne 0) { $build = 'FAIL'; throw 'Build failed.' }; $build = 'PASS'
        }
        & dotnet test ../../tests/DoorsIntegration.Tests/DoorsIntegration.Tests.csproj -c Release "-p:Platform=$Architecture" --no-build --no-restore --filter 'TestCategory=NativeBaselineSetMock|TestCategory=NativeBaselineSetReal' --logger 'trx;LogFileName=native-set.trx' --results-directory $output -- "RunConfiguration.TargetPlatform=$Architecture" 'RunConfiguration.TestSessionTimeout=600000' *> (Join-Path $output 'tests.log')
        $testExit = $LASTEXITCODE
    } finally { Pop-Location }
} catch { $errorMessage = 'Acceptance infrastructure failed; inspect logs. No native execution was enabled.' }
finally { $env:DOORS_TEST_MODE = $oldMode; $env:DOTNET_CLI_HOME = $oldCli; $env:DOORS_NATIVE_SET_ARTIFACT_ROOT = $oldEvidence }
$cases = @()
if (Test-Path -LiteralPath (Join-Path $output 'native-set.trx')) {
    [xml]$trx = Get-Content -LiteralPath (Join-Path $output 'native-set.trx') -Raw
    $ns = New-Object System.Xml.XmlNamespaceManager($trx.NameTable); $ns.AddNamespace('t','http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    $cases = @($trx.SelectNodes('//t:UnitTestResult',$ns) | ForEach-Object { [pscustomobject]@{Test=$_.testName;Status=$(if ($_.outcome -eq 'Passed') {'PASS'} elseif ($_.outcome -eq 'NotExecuted') {'NOT_RUN'} else {'FAIL'});Duration=$_.duration} })
}
$pass = @($cases | Where-Object Status -eq 'PASS').Count; $fail = @($cases | Where-Object Status -eq 'FAIL').Count; $notRun = @($cases | Where-Object Status -eq 'NOT_RUN').Count
$overall = if ($fail -gt 0 -or $testExit -ne 0) {'FAIL'} elseif ($errorMessage -or $pass -lt 28 -or $notRun -ne 4) {'NOT_RUN'} else {'PASS'}
$report = [ordered]@{Task=28;RecordedAtUtc=[DateTime]::UtcNow.ToString('O');Architecture=$Architecture;Build=$build;MockOverall=$overall;PassCount=$pass;FailCount=$fail;NotRunCount=$notRun;Native95='NOT_RUN';Native97='NOT_RUN';NativeComDxlValidated=$false;LiveDoorsWritesAttempted=$false;DpapiValidated=$false;InfrastructureError=$errorMessage;Cases=$cases;Disclosure='PASS verifies Mock/application behavior only; native scripts, configured-shareable/link behavior and DPAPI require separate controlled validation.'}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'acceptance.json') -Encoding UTF8
$cases | Export-Csv -LiteralPath (Join-Path $output 'acceptance.csv') -NoTypeInformation -Encoding UTF8
Write-Output "Mock=$overall; PASS=$pass; FAIL=$fail; NOT_RUN=$notRun; native9.5/9.7=NOT_RUN; evidence=$output"
if ($overall -ne 'PASS') { exit 1 }
