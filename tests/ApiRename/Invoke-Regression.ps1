param([Parameter(Mandatory)][string]$De4dot,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $OutputDirectory){throw 'Choose a new output folder'}
$lib=Join-Path $OutputDirectory 'lib'; $caller=Join-Path $OutputDirectory 'caller'; $reader=Join-Path $OutputDirectory 'reader'
New-Item -ItemType Directory $lib,$caller,$reader | Out-Null
Copy-Item "$PSScriptRoot\Fixture.cs" $lib
Copy-Item "$PSScriptRoot\Caller.cs" $caller
Copy-Item "$PSScriptRoot\ReflectionReader.cs" $reader
'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup></Project>' | Set-Content "$reader\Reader.csproj"
'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net48</TargetFramework><LangVersion>latest</LangVersion></PropertyGroup></Project>' | Set-Content "$lib\Fixture.csproj"
'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net48</TargetFramework><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><ProjectReference Include="..\lib\Fixture.csproj"/><ProjectReference Include="..\reader\Reader.csproj"/></ItemGroup></Project>' | Set-Content "$caller\Caller.csproj"
dotnet build "$caller\Caller.csproj" -c Release -v quiet
if($LASTEXITCODE){throw 'Build failed'}
& "$caller\bin\Release\net48\Caller.exe"
if($LASTEXITCODE){throw 'Baseline failed'}
$output=Join-Path $OutputDirectory 'output'
Copy-Item "$reader\bin\Release\net48\Reader.dll" "$lib\bin\Release\net48"
& $De4dot --batch "$lib\bin\Release\net48" --batch-output $output --df-inline false
if($LASTEXITCODE){throw 'Batch failed'}
Copy-Item "$caller\bin\Release\net48\Caller.exe" $output
& "$output\Caller.exe"
if($LASTEXITCODE){throw 'External caller broke'}
if((Get-FileHash "$output\Reader.dll").Hash -ne (Get-FileHash "$reader\bin\Release\net48\Reader.dll").Hash){throw 'Reflection-only consumer changed'}
[xml]$map=Get-Content "$output\de4dot-rename-map.xml"
$mod=$map.De4dotRenameMap.Module | Where-Object Path -eq 'Fixture.dll'
if(-not ($mod.Method | Where-Object {$_.OldName -eq 'a' -and $_.NewName -like 'smethod_*'})){throw 'Private obfuscation was not renamed and journaled'}
if($mod.Method | Where-Object {$_.OldName -match '^(Open|ProcessIdentity|add|b)$' -and $_.NewName -ne $_.OldName}){throw 'API name changed'}
if($mod.InputSha256 -ne (Get-FileHash "$lib\bin\Release\net48\Fixture.dll").Hash -or $mod.OutputSha256 -ne (Get-FileHash "$output\Fixture.dll").Hash){throw 'Journal hashes mismatch'}
'PASS verified rename journal and input/output hashes'
