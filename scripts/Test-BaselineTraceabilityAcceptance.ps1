[CmdletBinding()]
param([ValidateSet('x86','x64')][string]$Architecture = 'x64', [string]$OutputDirectory, [switch]$NoBuild, [switch]$FullSuite)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $repo ('artifacts\BaselineVerification\' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + $Architecture) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a new evidence directory; existing evidence is never replaced.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$oldMode = $env:DOORS_TEST_MODE; $oldCli = $env:DOTNET_CLI_HOME; $oldEvidence = $env:DOORS_BASELINE_VERIFICATION_ARTIFACT_ROOT
$testExit = -1; $build = 'NOT_RUN'; $errorMessage = $null
try {
    $env:DOORS_TEST_MODE = 'disabled'; $env:DOTNET_CLI_HOME = Join-Path $repo '.cli'; $env:DOORS_BASELINE_VERIFICATION_ARTIFACT_ROOT = Join-Path $output 'mock-evidence'
    Push-Location (Join-Path $repo 'artifacts\SmokeBuildHost')
    try {
        if (-not $NoBuild) {
            & dotnet restore ../../DoorsIntegration.sln "-p:Platform=$Architecture" --packages ../../.packages --configfile ../../NuGet.Config --ignore-failed-sources *> (Join-Path $output 'restore.log')
            if ($LASTEXITCODE -ne 0) { $build = 'FAIL'; throw 'Restore failed.' }
            & dotnet build ../../DoorsIntegration.sln -c Release "-p:Platform=$Architecture" --no-restore *> (Join-Path $output 'build.log')
            if ($LASTEXITCODE -ne 0) { $build = 'FAIL'; throw 'Build failed.' }; $build = 'PASS'
        }
        $filter = if ($FullSuite) { @() } else { @('--filter', 'TestCategory=BaselineVerificationMock|TestCategory=BaselineVerificationReal') }
        & dotnet test ../../tests/DoorsIntegration.Tests/DoorsIntegration.Tests.csproj -c Release "-p:Platform=$Architecture" --no-build --no-restore @filter --logger 'trx;LogFileName=verification.trx' --results-directory $output -- "RunConfiguration.TargetPlatform=$Architecture" 'RunConfiguration.TestSessionTimeout=600000' *> (Join-Path $output 'tests.log')
        $testExit = $LASTEXITCODE
    } finally { Pop-Location }
} catch { $errorMessage = 'Acceptance infrastructure failed; inspect logs. No native write was enabled.' }
finally { $env:DOORS_TEST_MODE = $oldMode; $env:DOTNET_CLI_HOME = $oldCli; $env:DOORS_BASELINE_VERIFICATION_ARTIFACT_ROOT = $oldEvidence }
$cases = @(); $allCases = @(); $metrics = @()
if (Test-Path -LiteralPath (Join-Path $output 'verification.trx')) {
    [xml]$trx = Get-Content -LiteralPath (Join-Path $output 'verification.trx') -Raw
    $ns = New-Object System.Xml.XmlNamespaceManager($trx.NameTable); $ns.AddNamespace('t','http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    $classes = @{}; foreach ($definition in $trx.SelectNodes('//t:UnitTest',$ns)) { $classes[$definition.id] = $definition.TestMethod.className }
    $allCases = @($trx.SelectNodes('//t:UnitTestResult',$ns))
    $cases = @($allCases | Where-Object { $classes[$_.testId] -match '\.BaselineVerification' } | ForEach-Object { [pscustomobject]@{Test=$_.testName;Status=$(if ($_.outcome -eq 'Passed') {'PASS'} elseif ($_.outcome -eq 'NotExecuted') {'NOT_RUN'} else {'FAIL'});Duration=$_.duration} })
    foreach ($node in $trx.SelectNodes('//t:UnitTestResult/t:Output/t:StdOut',$ns)) { $metrics += @($node.InnerText -split "`r?`n" | Where-Object { $_ -like 'BASELINE_VERIFICATION_METRIC*' }) }
}
$pass = @($cases | Where-Object Status -eq 'PASS').Count; $fail = @($cases | Where-Object Status -eq 'FAIL').Count; $notRun = @($cases | Where-Object Status -eq 'NOT_RUN').Count
$overall = if ($fail -gt 0 -or $testExit -ne 0) {'FAIL'} elseif ($errorMessage -or $pass -lt 36 -or $notRun -ne 2) {'NOT_RUN'} else {'PASS'}
$report = [ordered]@{Task=29;RecordedAtUtc=[DateTime]::UtcNow.ToString('O');Architecture=$Architecture;Build=$build;ReadOnly=$true;MockOverall=$overall;MockPassCount=$pass;FailCount=$fail;NativeNotRunCount=$notRun;FullSuite=$FullSuite.IsPresent;ObservedSuiteCases=$allCases.Count;Native95='NOT_RUN';Native97='NOT_RUN';NativeComDxlValidated=$false;LiveDoorsWritesAttempted=$false;DpapiValidated=$false;InfrastructureError=$errorMessage;MockPerformanceMetrics=$metrics;Cases=$cases;Disclosure='PASS verifies Mock/application behavior only; actual native membership/permissions/shareable configuration/sourceVersion/targetVersion/echo require separate release/build validation. No repair or native mutation is implemented.'}
$report | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $output 'acceptance.json') -Encoding UTF8
$cases | Export-Csv -LiteralPath (Join-Path $output 'acceptance.csv') -NoTypeInformation -Encoding UTF8
Write-Output "Mock=$overall; PASS=$pass; FAIL=$fail; Native NOT_RUN=$notRun; readOnly=True; evidence=$output"
if ($overall -ne 'PASS') { exit 1 }
