param([Parameter(Mandatory)][string]$De4dot,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(Test-Path $OutputDirectory){throw 'Choose a new output directory'}
$inputDirectory=Join-Path $OutputDirectory 'input'
New-Item -ItemType Directory -Path $inputDirectory | Out-Null
'<Project />' | Set-Content (Join-Path $OutputDirectory 'Directory.Build.props')
Copy-Item (Join-Path $PSScriptRoot 'Fixture.cs') $inputDirectory
'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net48</TargetFramework><OutputType>Exe</OutputType><Optimize>true</Optimize></PropertyGroup></Project>' | Set-Content (Join-Path $inputDirectory 'Fixture.csproj')
dotnet build (Join-Path $inputDirectory 'Fixture.csproj') -c Release --nologo -v quiet
if($LASTEXITCODE -ne 0){throw 'Fixture build failed'}
$original=Join-Path $inputDirectory 'bin\Release\net48\Fixture.exe'
$hash=(Get-FileHash $original).Hash
& $original
if($LASTEXITCODE -ne 0){throw 'Original runtime failed'}
$output=Join-Path $OutputDirectory 'processed.exe'
& $De4dot --default-strtyp none --dont-rename -f $original -o $output
if($LASTEXITCODE -ne 0){throw 'Processing failed'}
& $output
if($LASTEXITCODE -ne 0){throw 'Processed behavior changed'}
if((Get-FileHash $original).Hash -ne $hash){throw 'Input changed'}
