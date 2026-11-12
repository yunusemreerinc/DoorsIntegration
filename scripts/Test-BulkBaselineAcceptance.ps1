[CmdletBinding()]
param(
    [ValidateSet('x86','x64')][string]$Architecture = 'x64',
    [string]$OutputDirectory,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot ('artifacts\BulkBaselineAcceptance\' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + $Architecture + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputRoot) { throw 'Choose a new acceptance output directory; existing evidence is never replaced.' }
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$oldMode = $env:DOORS_TEST_MODE; $oldCli = $env:DOTNET_CLI_HOME
$oldArtifacts = $env:DOORS_ACCEPTANCE_ARTIFACT_ROOT
$runError = $null; $testExit = -1; $buildStatus = if ($NoBuild) { 'NOT_RUN' } else { 'PASS' }
$trxPath = Join-Path $outputRoot 'acceptance.trx'
try {
    $env:DOORS_TEST_MODE = 'disabled'
    $env:DOORS_ACCEPTANCE_ARTIFACT_ROOT = Join-Path $outputRoot 'mock-reports'
    $env:DOTNET_CLI_HOME = Join-Path $repoRoot '.cli'
    Push-Location (Join-Path $repoRoot 'artifacts\SmokeBuildHost')
    try {
        if (-not $NoBuild) {
            & dotnet restore ../../DoorsIntegration.sln "-p:Platform=$Architecture" --packages ../../.packages --configfile ../../NuGet.Config --ignore-failed-sources *> (Join-Path $outputRoot 'restore.log')
            if ($LASTEXITCODE -ne 0) { $buildStatus = 'FAIL'; throw 'Restore failed; see restore.log.' }
            & dotnet build ../../DoorsIntegration.sln -c Release "-p:Platform=$Architecture" --no-restore *> (Join-Path $outputRoot 'build.log')
            if ($LASTEXITCODE -ne 0) { $buildStatus = 'FAIL'; throw 'Build failed; see build.log.' }
        }
        & dotnet test ../../tests/DoorsIntegration.Tests/DoorsIntegration.Tests.csproj -c Release "-p:Platform=$Architecture" --no-build --no-restore --filter 'TestCategory=BulkBaselineAcceptance' --settings ../../tests.runsettings --logger 'trx;LogFileName=acceptance.trx' --results-directory $outputRoot -- "RunConfiguration.TargetPlatform=$Architecture" 'RunConfiguration.TestSessionTimeout=600000' *> (Join-Path $outputRoot 'tests.log')
        $testExit = $LASTEXITCODE
    } finally { Pop-Location }
} catch { $runError = $_.Exception.GetType().FullName + ': acceptance infrastructure failed; inspect run logs.' }
finally { $env:DOORS_TEST_MODE = $oldMode; $env:DOTNET_CLI_HOME = $oldCli; $env:DOORS_ACCEPTANCE_ARTIFACT_ROOT = $oldArtifacts }

$units = @(); $metrics = @(); $attachments = @()
if (Test-Path -LiteralPath $trxPath -PathType Leaf) {
    try {
        [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
        $ns = New-Object System.Xml.XmlNamespaceManager($trx.NameTable)
        $ns.AddNamespace('t','http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
        $units = @($trx.SelectNodes('//t:UnitTestResult',$ns))
        $attachments = @($trx.SelectNodes('//t:UnitTestResult/t:ResultFiles/t:ResultFile',$ns) | ForEach-Object { $_.GetAttribute('path') })
        foreach ($node in $trx.SelectNodes('//t:UnitTestResult/t:Output/t:StdOut',$ns)) {
            $metrics += @($node.InnerText -split "`r?`n" | Where-Object { $_ -like 'ACCEPTANCE_METRIC*' })
        }
    } catch { $runError = 'Invalid/missing test result data; no acceptance was assumed.' }
}
$names = @('20 formal modules / multiple folders','Stable identity deduplication','Major 1.4 -> 2.0 / Minor 1.4 -> 1.5','Mixed previous versions','First baseline','Suffix / annotation / overrides','Target collision','Permission denied / locked','Partial failure / execution policies','Cancel remaining / observe active','Simulated COM timeout / read-only reconciliation','Actual MOCK child process crash / new-process journal recovery','Same JobId idempotency','1000-module selection / dry-run performance','JSON / CSV / XLSX report accuracy')
$expected = @(1,1,2,2,2,1,1,1,2,1,1,2,1,3,3)
$scenarios = @()
for ($index=0; $index -lt 15; $index++) {
    $id = 'A{0:D2}' -f ($index+1)
    $cases = @($units | Where-Object { $_.testName -match ('Acceptance_' + $id + '_') })
    $failures = @($cases | Where-Object { $_.outcome -ne 'Passed' -and $_.outcome -ne 'NotExecuted' })
    $notRun = @($cases | Where-Object { $_.outcome -eq 'NotExecuted' })
    $status = if ($failures.Count -gt 0) { 'FAIL' } elseif ($cases.Count -ne $expected[$index] -or $notRun.Count -gt 0) { 'NOT_RUN' } else { 'PASS' }
    $scenarios += [pscustomobject]@{Id=$id;Scenario=$names[$index];Status=$status;Source='Mock';ExpectedCases=$expected[$index];ObservedCases=$cases.Count;Duration=@($cases | Select-Object -ExpandProperty duration);NativeComDxlValidated=$false}
}
$overall = if (@($scenarios | Where-Object { $_.Status -eq 'FAIL' }).Count -gt 0) { 'FAIL' } elseif ($runError -or $testExit -ne 0 -or $units.Count -ne 24 -or @($scenarios | Where-Object { $_.Status -ne 'PASS' }).Count -gt 0) { 'NOT_RUN' } else { 'PASS' }
$native = @()
foreach ($version in @('9.5','9.7')) {
    foreach ($case in @('Metadata read-only','Single Major','Single Minor','Multi-module batch','Unsaved changes / save','Other-session lock','Permission denied','Post-create invalid handle lifecycle','Post-create exact baseline verification','Application crash / journal recovery','DOORS client crash / reconnect / reconciliation')) {
        $native += [pscustomobject]@{Version=$version;Scenario=$case;Status='NOT_RUN';Reason='Real release/build/database not validated; no live write consent supplied. Mock cannot satisfy this case.'}
    }
}
$examples = @(Get-ChildItem -LiteralPath (Join-Path $outputRoot 'mock-reports') -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
$report = [ordered]@{Task=27;RecordedUtc=[DateTime]::UtcNow.ToString('O');Architecture=$Architecture;Framework='net48';BuildStatus=$buildStatus;MockOverall=$overall;ReleaseAcceptance='NOT_RUN';NativeComDxlValidated=$false;LiveDoorsWritesAttempted=$false;DpapiValidated=$false;TestExitCode=$testExit;InfrastructureError=$runError;ExpectedCases=24;ObservedCases=$units.Count;Scenarios=$scenarios;MockPerformanceMetrics=$metrics;ReportAttachments=$attachments;MockReportExampleDirectories=$examples;NativeAcceptance=$native;Trx=$trxPath;Disclosure='PASS applies to Mock application behavior only. Mock subprocess recovery is not a DOORS client crash or DPAPI validation. Native acceptance remains NOT_RUN.'}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot 'acceptance.json') -Encoding UTF8
$scenarios | Select-Object Id,Scenario,Status,Source,ExpectedCases,ObservedCases,NativeComDxlValidated | Export-Csv -LiteralPath (Join-Path $outputRoot 'acceptance.csv') -NoTypeInformation -Encoding UTF8
$lines = @('# Bulk Baseline Acceptance', '', "Architecture: $Architecture | Mock: $overall | Real release acceptance: NOT_RUN", '', '| ID | Scenario | Status | Cases |', '|---|---|---|---|')
$lines += $scenarios | ForEach-Object { '| ' + $_.Id + ' | ' + $_.Scenario + ' | ' + $_.Status + ' | ' + $_.ObservedCases + '/' + $_.ExpectedCases + ' |' }
$lines += @('', 'All 9.5/9.7 native cases: NOT_RUN. No real DOORS writes. Mock PASS does not validate COM/DXL or DPAPI.', '', 'Performance observations (Mock only):') + $metrics
$lines | Set-Content -LiteralPath (Join-Path $outputRoot 'acceptance.md') -Encoding UTF8
Write-Output "Mock=$overall; native=NOT_RUN; report=$outputRoot"
if ($overall -ne 'PASS') { exit 1 }
