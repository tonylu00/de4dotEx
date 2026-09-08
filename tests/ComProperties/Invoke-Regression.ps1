param([Parameter(Mandatory)][string]$De4dot,[Parameter(Mandatory)][string]$DnSpyConsole,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(Test-Path $OutputDirectory){throw 'Choose a new folder.'}
$source=Join-Path $OutputDirectory 'source'; $emitter=Join-Path $OutputDirectory 'emitter'; $contract=Join-Path $OutputDirectory 'contract'
New-Item -ItemType Directory $source,$emitter,$contract | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'Fixture.cs') $source
Copy-Item (Join-Path $PSScriptRoot 'Emit.cs') $emitter
Copy-Item (Join-Path $PSScriptRoot 'Contract.cs') $contract
'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup></Project>' | Set-Content (Join-Path $contract 'Contract.csproj')
'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net48</TargetFramework><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><ProjectReference Include="..\contract\Contract.csproj" /></ItemGroup></Project>' | Set-Content (Join-Path $source 'Fixture.csproj')
$dnlib=[Security.SecurityElement]::Escape((Join-Path (Split-Path $De4dot) 'dnlib.dll'))
"<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><Reference Include=`"dnlib`"><HintPath>$dnlib</HintPath></Reference></ItemGroup></Project>" | Set-Content (Join-Path $emitter 'Emitter.csproj')
dotnet build (Join-Path $source 'Fixture.csproj') -c Release --nologo -v quiet
if($LASTEXITCODE){throw 'Fixture build failed.'}
$original=Join-Path $source 'bin\Release\net48\Fixture.exe'
& $original
if($LASTEXITCODE){throw 'Original behavior failed.'}
$inputFile=Join-Path $OutputDirectory 'Fixture.exe'
dotnet run --project (Join-Path $emitter 'Emitter.csproj') -c Release -- $original $inputFile (Join-Path (Split-Path $original) 'Contract.dll') (Join-Path $OutputDirectory 'Contract.dll')
if($LASTEXITCODE){throw 'Emission failed.'}
& $inputFile emitted
if($LASTEXITCODE){throw 'Emitted behavior failed.'}
$hash=(Get-FileHash $inputFile).Hash
$contractHash=(Get-FileHash (Join-Path $OutputDirectory 'Contract.dll')).Hash
$processed=Join-Path $OutputDirectory 'processed\Fixture.exe'
New-Item -ItemType Directory (Split-Path $processed) | Out-Null
Copy-Item (Join-Path $OutputDirectory 'Contract.dll') (Split-Path $processed)
& $De4dot --no-cflow-deob --default-strtyp none -f $inputFile -p un --keep-types -o $processed
if($LASTEXITCODE){throw 'de4dot failed.'}
& $processed emitted restored
if($LASTEXITCODE){throw 'Processed metadata or behavior failed.'}
foreach($threads in 1,4){
 $export=Join-Path $OutputDirectory "export-$threads"
 & $DnSpyConsole --no-color --sdk-project --asm-path (Split-Path $processed) --threads $threads -o $export $processed
 if($LASTEXITCODE){throw 'Export failed.'}
 $project=Get-ChildItem $export -Recurse -Filter '*.csproj' | Select-Object -First 1
 $rebuilt=Join-Path $OutputDirectory "rebuilt-$threads"
 dotnet build $project.FullName -c Release -o $rebuilt --nologo -v quiet
 if($LASTEXITCODE){throw 'Rebuild failed.'}
 & (Join-Path $rebuilt 'Fixture.exe') emitted restored
 if($LASTEXITCODE){throw 'Rebuilt metadata or behavior failed.'}
}
if((Get-FileHash $inputFile).Hash -ne $hash){throw 'Input changed.'}
if((Get-FileHash (Join-Path $OutputDirectory 'Contract.dll')).Hash -ne $contractHash){throw 'Contract input changed.'}
$one=@(Get-ChildItem (Join-Path $OutputDirectory 'export-1') -Recurse -Filter '*.cs' | Sort-Object FullName | Get-FileHash | ForEach-Object Hash)
$four=@(Get-ChildItem (Join-Path $OutputDirectory 'export-4') -Recurse -Filter '*.cs' | Sort-Object FullName | Get-FileHash | ForEach-Object Hash)
if(Compare-Object $one $four -SyncWindow 0){throw 'Worker count changed source.'}
Write-Output 'PASS: imported properties stay original while managed properties restore and recompile.'
