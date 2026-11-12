[CmdletBinding()]
param([ValidateSet('x86','x64')][string]$Architecture='x64', [string]$OutputDirectory, [switch]$FullSuite)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
if([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory=Join-Path $repo ('artifacts\ChangeImpact\'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')+'-'+$Architecture) }
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output) { throw 'Choose a new evidence directory; previous results are never replaced.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$oldMode=$env:DOORS_TEST_MODE; $oldCli=$env:DOTNET_CLI_HOME; $oldEvidence=$env:DOORS_CHANGE_IMPACT_ARTIFACT_ROOT
$testExit=-1; $build='NOT_RUN'; $infrastructureError=$null
try {
    $env:DOORS_TEST_MODE='disabled'; $env:DOTNET_CLI_HOME=Join-Path $repo '.cli'; $env:DOORS_CHANGE_IMPACT_ARTIFACT_ROOT=Join-Path $output 'mock-evidence'
    Push-Location (Join-Path $repo 'artifacts\SmokeBuildHost')
    try {
        & dotnet restore ../../DoorsIntegration.sln "-p:Platform=$Architecture" --packages ../../.packages --configfile ../../NuGet.Config --ignore-failed-sources *> (Join-Path $output 'restore.log')
        if($LASTEXITCODE -ne 0) { $build='FAIL'; throw 'Restore failed.' }
        & dotnet build ../../DoorsIntegration.sln -c Release "-p:Platform=$Architecture" --no-restore *> (Join-Path $output 'build.log')
        if($LASTEXITCODE -ne 0) { $build='FAIL'; throw 'Build failed.' }; $build='PASS'
        $filter=if($FullSuite) { @() } else { @('--filter','TestCategory=ChangeImpactMock|TestCategory=ChangeImpactReal') }
        & dotnet test ../../tests/DoorsIntegration.Tests/DoorsIntegration.Tests.csproj -c Release "-p:Platform=$Architecture" --no-build --no-restore @filter --logger 'trx;LogFileName=verification.trx' --results-directory $output -- "RunConfiguration.TargetPlatform=$Architecture" 'RunConfiguration.TestSessionTimeout=600000' *> (Join-Path $output 'tests.log')
        $testExit=$LASTEXITCODE
    } finally { Pop-Location }
} catch { $infrastructureError='Acceptance infrastructure failed; inspect captured logs. No native mutation was enabled.' }
finally { $env:DOORS_TEST_MODE=$oldMode; $env:DOTNET_CLI_HOME=$oldCli; $env:DOORS_CHANGE_IMPACT_ARTIFACT_ROOT=$oldEvidence }
$allCases=@(); $featureCases=@(); $metrics=@()
if(Test-Path -LiteralPath (Join-Path $output 'verification.trx')) {
    [xml]$trx=Get-Content -LiteralPath (Join-Path $output 'verification.trx') -Raw
    $ns=New-Object System.Xml.XmlNamespaceManager($trx.NameTable); $ns.AddNamespace('t','http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    $classes=@{}; foreach($definition in $trx.SelectNodes('//t:UnitTest',$ns)) { $classes[$definition.id]=$definition.TestMethod.className }
    $allCases=@($trx.SelectNodes('//t:UnitTestResult',$ns))
    $featureCases=@($allCases | Where-Object { $classes[$_.testId] -match '\.ChangeImpact' } | ForEach-Object { [pscustomobject]@{ Test=$_.testName; Status=$(if($_.outcome -eq 'Passed') {'PASS'} elseif($_.outcome -eq 'NotExecuted') {'NOT_RUN'} else {'FAIL'}); Duration=$_.duration } })
    foreach($node in $trx.SelectNodes('//t:UnitTestResult/t:Output/t:StdOut',$ns)) { $metrics+=@($node.InnerText -split "`r?`n" | Where-Object { $_ -like 'CHANGE_IMPACT_METRIC*' }) }
}
$pass=@($allCases | Where-Object outcome -eq 'Passed').Count; $notRun=@($allCases | Where-Object outcome -eq 'NotExecuted').Count; $fail=$allCases.Count-$pass-$notRun
$mockPass=@($featureCases | Where-Object Status -eq 'PASS').Count; $mockFail=@($featureCases | Where-Object Status -eq 'FAIL').Count; $nativeNotRun=@($featureCases | Where-Object Status -eq 'NOT_RUN').Count
$overall=if($build -eq 'FAIL' -or $fail -gt 0 -or $testExit -ne 0) {'FAIL'} elseif($infrastructureError -or $mockPass -lt 42 -or $nativeNotRun -ne 16) {'NOT_RUN'} else {'PASS'}
$report=[ordered]@{ Task=30; RecordedAtUtc=[DateTime]::UtcNow.ToString('O'); Architecture=$Architecture; Framework='net48'; Build=$build; Overall=$overall; ReadOnly=$true; FullSuite=$FullSuite.IsPresent; Suite=[ordered]@{Total=$allCases.Count;Pass=$pass;Fail=$fail;NotRun=$notRun}; MockPassCount=$mockPass; FeatureFailCount=$mockFail; NativeNotRunCount=$nativeNotRun; Native95='NOT_RUN'; Native97='NOT_RUN'; NativeComDxlValidated=$false; NativeProductionReady=$false; ValidatedNativeMetadataProviderAvailable=$false; DpapiValidated=$false; LiveDoorsWritesAttempted=$false; InfrastructureError=$infrastructureError; MockPerformanceMetrics=$metrics; Cases=$featureCases; Disclosure='Mock/application and mocked-port behavior only. Native metadata/membership/permissions/configured-shareable provider, historical endpoint versions/echo, release-specific current/deleted/RTF behavior and target-account DPAPI require separate real acceptance. No write, repair, rollback, forced COM abort or automatic retry was enabled.' }
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'acceptance.json') -Encoding UTF8
$featureCases | Export-Csv -LiteralPath (Join-Path $output 'acceptance.csv') -NoTypeInformation -Encoding UTF8
Write-Output "$Architecture Overall=$overall; build=$build; suite=$pass PASS/$fail FAIL/$notRun NOT_RUN; Task30=$mockPass PASS/$nativeNotRun NOT_RUN; readOnly=True; evidence=$output"
if($overall -ne 'PASS') { exit 1 }
