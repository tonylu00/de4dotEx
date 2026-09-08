param([Parameter(Mandatory)][string]$De4dot,[Parameter(Mandatory)][string]$DnSpyConsole,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $OutputDirectory){throw 'Choose a new output directory.'}
foreach($dir in 'library','client','emitter','input'){New-Item -ItemType Directory -Path (Join-Path $OutputDirectory $dir) | Out-Null}
'<Project />' | Set-Content (Join-Path $OutputDirectory 'Directory.Build.props')
foreach($pair in @(@('FieldCollisionLibrary.cs','library'),@('FieldCollisionClient.cs','client'),@('EmitFieldCollision.cs','emitter'))){Copy-Item (Join-Path $PSScriptRoot $pair[0]) (Join-Path $OutputDirectory $pair[1])}
'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net48</TargetFramework><Optimize>true</Optimize></PropertyGroup></Project>' | Set-Content (Join-Path $OutputDirectory 'library\FieldCollisionLibrary.csproj')
'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net48</TargetFramework><OutputType>Exe</OutputType><Optimize>true</Optimize></PropertyGroup><ItemGroup><ProjectReference Include="..\library\FieldCollisionLibrary.csproj" /></ItemGroup></Project>' | Set-Content (Join-Path $OutputDirectory 'client\FieldCollisionClient.csproj')
$dnlib=[Security.SecurityElement]::Escape((Join-Path (Split-Path ([IO.Path]::GetFullPath($DnSpyConsole))) 'dnlib.dll'))
"<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><Reference Include=`"dnlib`"><HintPath>$dnlib</HintPath></Reference></ItemGroup></Project>" | Set-Content (Join-Path $OutputDirectory 'emitter\Emitter.csproj')
dotnet build (Join-Path $OutputDirectory 'client\FieldCollisionClient.csproj') -c Release --nologo -v quiet
if($LASTEXITCODE -ne 0){throw 'Input build failed.'}
$inputDir=Join-Path $OutputDirectory 'input'
dotnet run --project (Join-Path $OutputDirectory 'emitter\Emitter.csproj') -c Release -- (Join-Path $OutputDirectory 'library\bin\Release\net48\FieldCollisionLibrary.dll') (Join-Path $OutputDirectory 'client\bin\Release\net48\FieldCollisionClient.exe') $inputDir
if($LASTEXITCODE -ne 0){throw 'Metadata emission failed.'}
$exe=Join-Path $inputDir 'FieldCollisionClient.exe'; $library=Join-Path $inputDir 'FieldCollisionLibrary.dll'
$hashes=@(Get-FileHash $exe,$library | ForEach-Object Hash)
& $exe
if($LASTEXITCODE -ne 0){throw 'Original field references failed.'}
$processed=Join-Path $OutputDirectory 'processed'
New-Item -ItemType Directory -Path $processed | Out-Null
$processedExe=Join-Path $processed 'FieldCollisionClient.exe'
$processedLibrary=Join-Path $processed 'FieldCollisionLibrary.dll'
& $De4dot --no-cflow-deob --dont-restore-props -f $library -p un --keep-types -o $processedLibrary -f $exe -p un --keep-types -o $processedExe
if($LASTEXITCODE -ne 0){throw 'de4dot rename failed.'}
dotnet run --project (Join-Path $OutputDirectory 'emitter\Emitter.csproj') -c Release --no-build -- --verify $processedLibrary
if($LASTEXITCODE -ne 0){throw 'Processed metadata contains field collisions.'}
& $processedExe
if($LASTEXITCODE -ne 0){throw 'Renamed references changed.'}
foreach($threads in 1,4){
 $export=Join-Path $OutputDirectory "export-$threads"
 & $DnSpyConsole --no-color --sdk-project --threads $threads --asm-path $processed -o $export $processedLibrary $processedExe
 if($LASTEXITCODE -ne 0){throw 'Export failed.'}
 $project=Get-ChildItem $export -Recurse -Filter FieldCollisionClient.csproj | Select-Object -First 1
 $rebuilt=Join-Path $OutputDirectory "rebuilt-$threads"
 dotnet build $project.FullName -c Release -o $rebuilt --nologo -v quiet
 if($LASTEXITCODE -ne 0){throw 'Rebuilt source failed.'}
 & (Join-Path $rebuilt 'FieldCollisionClient.exe')
 if($LASTEXITCODE -ne 0){throw 'Rebuilt references changed.'}
}
if(Compare-Object $hashes @(Get-FileHash $exe,$library | ForEach-Object Hash)){throw 'Inputs changed.'}
foreach($relative in 'FieldCollisionLibrary\Collision.cs','FieldCollisionLibrary\GenericBox.cs','FieldCollisionClient\FieldCollisionClient.cs') {
 $one=Join-Path $OutputDirectory (Join-Path 'export-1' $relative)
 $four=Join-Path $OutputDirectory (Join-Path 'export-4' $relative)
 if((Get-FileHash $one).Hash -ne (Get-FileHash $four).Hash){throw 'Worker count changed generated source.'}
}

