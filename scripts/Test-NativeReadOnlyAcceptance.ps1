[CmdletBinding()]
param([ValidateSet('x86','x64')][string]$Architecture='x64', [string]$OutputDirectory, [switch]$FullSuite)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory=Join-Path $repo ('artifacts\NativeReadOnly\'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')+'-'+$Architecture) }
$output=[IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a new evidence directory; earlier results are retained.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$oldMode=$env:DOORS_TEST_MODE; $oldCli=$env:DOTNET_CLI_HOME
$build='NOT_RUN'; $testExit=-1; $problem=$null
try {
    $env:DOORS_TEST_MODE='disabled'; $env:DOTNET_CLI_HOME=Join-Path $repo '.cli'
    Push-Location (Join-Path $repo 'artifacts\SmokeBuildHost')
    try {
        & dotnet restore ../../DoorsIntegration.sln "-p:Platform=$Architecture" --packages ../../.packages --configfile ../../NuGet.Config --ignore-failed-sources *> (Join-Path $output 'restore.log')
        if ($LASTEXITCODE -ne 0) { $build='FAIL'; throw 'Restore failed.' }
        & dotnet build ../../DoorsIntegration.sln -c Release "-p:Platform=$Architecture" --no-restore *> (Join-Path $output 'build.log')
        if ($LASTEXITCODE -ne 0) { $build='FAIL'; throw 'Build failed.' }; $build='PASS'
        $filter=if ($FullSuite) { @() } else { @('--filter','TestCategory=NativeReadOnly|TestCategory=NativeReadOnlyReal|FullyQualifiedName~DefaultNativeCompositionUsesReadOnlyObservation') }
        & dotnet test ../../tests/DoorsIntegration.Tests/DoorsIntegration.Tests.csproj -c Release "-p:Platform=$Architecture" --no-build --no-restore @filter --logger 'trx;LogFileName=verification.trx' --results-directory $output -- "RunConfiguration.TargetPlatform=$Architecture" 'RunConfiguration.TestSessionTimeout=600000' *> (Join-Path $output 'tests.log')
        $testExit=$LASTEXITCODE
    } finally { Pop-Location }
} catch { $problem=$_.Exception.Message }
finally { $env:DOORS_TEST_MODE=$oldMode; $env:DOTNET_CLI_HOME=$oldCli }
$cases=@(); $featureCases=@()
if (Test-Path -LiteralPath (Join-Path $output 'verification.trx')) {
    [xml]$trx=Get-Content -LiteralPath (Join-Path $output 'verification.trx') -Raw
    $ns=New-Object System.Xml.XmlNamespaceManager($trx.NameTable); $ns.AddNamespace('t','http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    $classes=@{}; foreach ($definition in $trx.SelectNodes('//t:UnitTest',$ns)) { $classes[$definition.id]=$definition.TestMethod.className }
    $cases=@($trx.SelectNodes('//t:UnitTestResult',$ns))
    $featureCases=@($cases | Where-Object { $classes[$_.testId] -match '\.NativeReadOnly|\.NativeReadDispatch' } | ForEach-Object {
        [pscustomobject]@{ Test=$_.testName; Class=$classes[$_.testId]; Status=$(if ($_.outcome -eq 'Passed') {'PASS'} elseif ($_.outcome -eq 'NotExecuted') {'NOT_RUN'} else {'FAIL'}); Duration=$_.duration }
    })
}
$pass=@($cases | Where-Object outcome -eq 'Passed').Count; $notRun=@($cases | Where-Object outcome -eq 'NotExecuted').Count; $fail=$cases.Count-$pass-$notRun
$featurePass=@($featureCases | Where-Object Status -eq 'PASS').Count; $featureNotRun=@($featureCases | Where-Object Status -eq 'NOT_RUN').Count
$local=if ($build -eq 'PASS' -and $testExit -eq 0 -and $fail -eq 0 -and $featurePass -ge 33 -and $featureNotRun -eq 8 -and !$problem) {'PASS'} else {'FAIL'}
$report=[ordered]@{ Feature='Real mode read-only observation'; RecordedAtUtc=[DateTime]::UtcNow.ToString('O'); Architecture=$Architecture; Framework='net48'; Build=$build; LocalVerification=$local; FullSuite=$FullSuite.IsPresent;
    Suite=[ordered]@{Total=$cases.Count;Pass=$pass;Fail=$fail;NotRun=$notRun}; FeatureCounts=[ordered]@{Pass=$featurePass;NotRun=$featureNotRun}; Real95='NOT_RUN'; Real97='NOT_RUN'; NativeRuntimeValidated=$false; NativeWriteEnabled=$false; DoorsDataModified=$false; InfrastructureError=$problem;
    Disclosure='PASS applies to application, protocol, mocked STA transport and WPF tests. No real DOORS client was used. Runtime DXL/COM acceptance, complete hidden membership, cached definition freshness and trustworthy revision/lock evidence remain unverified. No native write, repair, save, automatic retry or forced COM abort was enabled.'; Cases=$featureCases }
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'acceptance.json') -Encoding UTF8
$featureCases | Export-Csv -LiteralPath (Join-Path $output 'acceptance.csv') -NoTypeInformation -Encoding UTF8
Write-Output "$Architecture Local=$local; build=$build; suite=$pass PASS/$fail FAIL/$notRun NOT_RUN; new=$featurePass PASS/$featureNotRun NOT_RUN; Real95=NOT_RUN; Real97=NOT_RUN; evidence=$output"
if ($local -ne 'PASS') { exit 1 }
