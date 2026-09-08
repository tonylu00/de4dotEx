param([Parameter(Mandatory)][string]$De4dot,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $OutputDirectory){throw 'Choose a new output directory'}
$emitter=Join-Path $OutputDirectory 'emitter'
New-Item -ItemType Directory -Path $emitter | Out-Null
'<Project />' | Set-Content (Join-Path $OutputDirectory 'Directory.Build.props')
Copy-Item (Join-Path $PSScriptRoot 'FixtureTools.cs') $emitter
$dnlib=[Security.SecurityElement]::Escape((Join-Path (Split-Path ([IO.Path]::GetFullPath($De4dot))) 'dnlib.dll'))
"<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><Reference Include=`"dnlib`"><HintPath>$dnlib</HintPath></Reference></ItemGroup></Project>" | Set-Content (Join-Path $emitter 'Emitter.csproj')
$project=Join-Path $emitter 'Emitter.csproj'
$inputRoot=Join-Path $OutputDirectory 'input'
dotnet run --project $project -c Release -- emit $inputRoot
if($LASTEXITCODE -ne 0){throw 'Emission failed'}
$hashes=@(Get-ChildItem $inputRoot -File | Get-FileHash | ForEach-Object Hash)
$output=Join-Path $OutputDirectory 'output'
& $De4dot --batch $inputRoot --batch-output $output --default-strtyp none
if($LASTEXITCODE -ne 0){throw 'Attribute repair batch failed'}
dotnet run --project $project -c Release --no-build -- verify $output
if($LASTEXITCODE -ne 0){throw 'Scope verification failed'}
if((Get-FileHash (Join-Path $inputRoot 'Shadow.dll')).Hash -ne (Get-FileHash (Join-Path $output 'Shadow.dll')).Hash){throw 'Valid shadow type assembly was rewritten'}
if(Compare-Object $hashes @(Get-ChildItem $inputRoot -File | Get-FileHash | ForEach-Object Hash)){throw 'Inputs changed'}
$bad=Join-Path $OutputDirectory 'bad'
dotnet run --project $project -c Release --no-build -- bad $bad
if($LASTEXITCODE -ne 0){throw 'Bad fixture emission failed'}
$badOutput=Join-Path $OutputDirectory 'bad-output'
& $De4dot --batch $bad --batch-output $badOutput --default-strtyp none
if($LASTEXITCODE -eq 0 -or (Test-Path $badOutput)){throw 'Unknown System namespace type was guessed or ignored'}
Write-Output 'PASS: missing non-core types still fail publication; original files unchanged'
