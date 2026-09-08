param([Parameter(Mandatory)][string]$De4dot,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $OutputDirectory){throw 'Choose a new output directory'}
& (Join-Path $PSScriptRoot 'Invoke-Regression.ps1') -De4dot $De4dot -OutputDirectory (Join-Path $OutputDirectory 'fixture') -Batch
$hostInput=Join-Path $OutputDirectory 'host'
$external=Join-Path $OutputDirectory 'external'
New-Item -ItemType Directory -Path $hostInput,(Join-Path $external 'plugins') | Out-Null
Copy-Item (Join-Path $OutputDirectory 'fixture\input\context-1\Library.dll') $hostInput
foreach($version in 1,2){Copy-Item (Join-Path $OutputDirectory "fixture\input\context-$version\Client.exe") (Join-Path $hostInput "Client$version.exe")}
Copy-Item (Join-Path $OutputDirectory 'fixture\input\context-2\Library.dll') (Join-Path $external 'plugins\Library.dll')
'<configuration><runtime><assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1"><probing privatePath="plugins" /></assemblyBinding></runtime></configuration>' | Set-Content (Join-Path $external 'App.exe.config')
$manifest=Join-Path $OutputDirectory 'contexts.xml'
'<AssemblyContexts><Context Source="host/Client2.exe" Directory="external" Config="external/App.exe.config" /></AssemblyContexts>' | Set-Content $manifest
$hashes=@(Get-ChildItem $hostInput,$external -Recurse -File | Get-FileHash | ForEach-Object Hash)
$output=Join-Path $OutputDirectory 'output'
& $De4dot --batch $hostInput --batch-output $output --assembly-contexts $manifest --default-strtyp none --df-name '^(?!v$)[A-Za-z_][A-Za-z_0-9]*$'
if($LASTEXITCODE -ne 0){throw 'External context batch failed'}
& (Join-Path $output 'Client1.exe')
if($LASTEXITCODE -ne 0){throw 'Host API changed'}
if((Get-FileHash (Join-Path $output 'Client2.exe')).Hash -ne (Get-FileHash (Join-Path $hostInput 'Client2.exe')).Hash){throw 'External consumer was rewritten against host APIs'}
$runtime=Join-Path $OutputDirectory 'runtime'
New-Item -ItemType Directory -Path $runtime | Out-Null
Copy-Item (Join-Path $output 'Client2.exe') $runtime
Copy-Item (Join-Path $external 'plugins\Library.dll') $runtime
& (Join-Path $runtime 'Client2.exe')
if($LASTEXITCODE -ne 0){throw 'External API, reflection or virtual dispatch changed'}
if(Compare-Object $hashes @(Get-ChildItem $hostInput,$external -Recurse -File | Get-FileHash | ForEach-Object Hash)){throw 'Inputs or external dependencies changed'}
$broken=Join-Path $OutputDirectory 'broken'
New-Item -ItemType Directory -Path $broken | Out-Null
Copy-Item (Join-Path $hostInput 'Library.dll') $broken
'<AssemblyContexts><Context Source="host/Client2.exe" Directory="broken" /></AssemblyContexts>' | Set-Content $manifest
$brokenOutput=Join-Path $OutputDirectory 'broken-output'
& $De4dot --batch $hostInput --batch-output $brokenOutput --assembly-contexts $manifest --default-strtyp none
if($LASTEXITCODE -eq 0 -or (Test-Path $brokenOutput)){throw 'External missing-member errors were ignored'}
Write-Output 'PASS: external dependency context, config probing, preserved consumer and missing-member publication gate'
