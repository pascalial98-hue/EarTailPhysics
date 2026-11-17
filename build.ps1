param([string]$DalamudLibPath = "$env:APPDATA\XIVLauncherCN\addon\Hooks\15.0.4.0")
$ErrorActionPreference = 'Stop'
$sdk = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetExe = if (Test-Path 'D:\modding\tools\dotnet-sdk-10.0.100\dotnet.exe') { 'D:\modding\tools\dotnet-sdk-10.0.100\dotnet.exe' } elseif ($sdk) { $sdk.Source } else { throw '.NET 10 SDK required' }
& $dotnetExe build "$PSScriptRoot\src\EarTailPhysics.csproj" -c Release "-p:DalamudLibPath=$DalamudLibPath"
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$output = Join-Path $PSScriptRoot 'artifacts'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$dll = Join-Path $PSScriptRoot 'src\bin\Release\net10.0-windows7.0\EarTailPhysics.dll'
$manifest = Join-Path $PSScriptRoot 'src\EarTailPhysics.json'
Compress-Archive -LiteralPath $dll,$manifest -DestinationPath (Join-Path $output 'EarTailPhysics.zip') -Force
Get-FileHash (Join-Path $output 'EarTailPhysics.zip') -Algorithm SHA256
