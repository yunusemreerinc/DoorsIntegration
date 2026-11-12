param(
    [Parameter(Mandatory=$true)][Guid]$RequestId,
    [Parameter(Mandatory=$true)][string]$OutputFile,
    [string]$AssemblyDirectory = (Split-Path -Parent $PSScriptRoot),
    [string]$CaptureDirectory = (Join-Path $env:LOCALAPPDATA 'DoorsIntegration\discovery-debug')
)
$ErrorActionPreference = 'Stop'
# Explicit local export under the same Windows account that captured the response.
# Never prints raw response, database paths, or exception messages to the console.
Add-Type -Path (Join-Path $AssemblyDirectory 'DoorsIntegration.Core.dll')
Add-Type -Path (Join-Path $AssemblyDirectory 'DoorsIntegration.Dxl.dll')
$store = New-Object DoorsIntegration.Dxl.ProtectedDiscoveryDebugStore($CaptureDirectory)
$record = $store.Read($RequestId)
$json = [ordered]@{ Metadata=$record; RawComResponse=$record.ReadComResponse(); RawDiscoveryResponse=$record.ReadDiscoveryResponse() } | ConvertTo-Json -Depth 8
$destination = [IO.Path]::GetFullPath($OutputFile)
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
$acl = New-Object Security.AccessControl.FileSecurity
$acl.SetOwner($sid)
$acl.SetAccessRuleProtection($true, $false)
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', 'Allow')))
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier('S-1-5-18')), 'FullControl', 'Allow')))
$stream = New-Object IO.FileStream($destination, [IO.FileMode]::CreateNew, [Security.AccessControl.FileSystemRights]::Write, [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough, $acl)
try { $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($json); $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
finally { $stream.Dispose() }
Write-Output ('Protected local plaintext export created. RequestId=' + $RequestId.ToString('N'))
