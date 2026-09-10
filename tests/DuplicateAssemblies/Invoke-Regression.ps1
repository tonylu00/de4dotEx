param([Parameter(Mandatory)][string]$De4dot,[Parameter(Mandatory)][string]$OutputDirectory,[switch]$Batch)
$ErrorActionPreference='Stop'
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $OutputDirectory){throw 'Choose a new output directory.'}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
'<Project />' | Set-Content (Join-Path $OutputDirectory 'Directory.Build.props')
$emitter=Join-Path $OutputDirectory 'emitter'
New-Item -ItemType Directory -Path $emitter | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'EmitFixture.cs') $emitter
$dnlib=[Security.SecurityElement]::Escape((Join-Path (Split-Path ([IO.Path]::GetFullPath($De4dot))) 'dnlib.dll'))
"<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><Reference Include=`"dnlib`"><HintPath>$dnlib</HintPath></Reference></ItemGroup></Project>" | Set-Content (Join-Path $emitter 'Emitter.csproj')
$inputs=@()
foreach($version in 1,2){
 $build=Join-Path $OutputDirectory "build-$version"
 foreach($part in 'library','client'){New-Item -ItemType Directory -Path (Join-Path $build $part) | Out-Null}
 "public class Api { public static int Get() { return Helper.Value(); } public virtual int v() { return 0; } } public class Helper { public static int Value() { return $version; } }" | Set-Content (Join-Path $build 'library\Library.cs')
 if($Batch){Add-Content (Join-Path $build 'library\Library.cs') "public class VersionApi { public static int Only$version() { return $version; } }"; Add-Content (Join-Path $build 'library\Library.cs') 'public class ReflectionProbe { public static int Read() { return (int)typeof(Helper).Assembly.GetType("Api").GetMethod("Get").Invoke(null, null); } }'}
 "using System; class Derived : Api { public override int v() { return 7; } } class Program { static int Main() { Api instance=new Derived(); if(Api.Get() != $version || instance.v() != 7) throw new Exception(`"Wrong compatibility assembly or virtual binding`" ); Console.WriteLine(`"PASS: binding context $version`" ); return 0; } }" | Set-Content (Join-Path $build 'client\Client.cs')
 if($Batch){$clientFile=Join-Path $build 'client\Client.cs'; (Get-Content $clientFile -Raw).Replace('return 0;', 'if (ReflectionProbe.Read() != Api.Get() || (int)typeof(Api).Assembly.GetType("Api").GetMethod("Get").Invoke(null, null) != Api.Get()) throw new Exception("Reflection binding changed"); return 0;') | Set-Content $clientFile}
 if($Batch){(Get-Content $clientFile -Raw).Replace('return 0;', "if (VersionApi.Only$version() != $version) throw new Exception(); return 0;") | Set-Content $clientFile}
 '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net48</TargetFramework><AssemblyVersion>1.0.0.0</AssemblyVersion></PropertyGroup></Project>' | Set-Content (Join-Path $build 'library\Library.csproj')
 '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net48</TargetFramework><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><ProjectReference Include="..\library\Library.csproj" /></ItemGroup></Project>' | Set-Content (Join-Path $build 'client\Client.csproj')
 dotnet build (Join-Path $build 'client\Client.csproj') -c Release --nologo -v quiet
 if($LASTEXITCODE -ne 0){throw 'Fixture build failed.'}
 $inputDir=Join-Path $OutputDirectory "input\context-$version"
 $mode=if($Batch){'batch'}else{'normal'}
 dotnet run --project (Join-Path $emitter 'Emitter.csproj') -c Release -- (Join-Path $build 'library\bin\Release\net48\Library.dll') (Join-Path $build 'client\bin\Release\net48\Client.exe') $inputDir $mode
 if($LASTEXITCODE -ne 0){throw 'Emission failed.'}
 foreach($name in 'Client.exe','AlternateClient.exe') { & (Join-Path $inputDir $name); if($LASTEXITCODE -ne 0){throw 'Original runtime failed.'} }
 $inputs+=Get-ChildItem $inputDir -File
}
if($Batch){
 $root=Join-Path $OutputDirectory 'input'
 New-Item -ItemType Directory -Path (Join-Path $root 'empty') | Out-Null
 'settings preserved' | Set-Content (Join-Path $root 'settings.txt')
 Copy-Item (Join-Path $OutputDirectory 'build-1\library\bin\Release\net48\Library.dll') (Join-Path $root 'Unprotected.dll')
 [IO.File]::WriteAllBytes((Join-Path $root 'native.dll'),[byte[]](0,1,2,3))
 $inputs=@(Get-ChildItem $root -File -Recurse)
}
$hashes=@($inputs | Get-FileHash | ForEach-Object Hash)
foreach($order in 'forward','reverse'){
 $ordered=@($inputs)
 if($order -eq 'reverse'){[Array]::Reverse($ordered)}
 $arguments=@('--rename-public-api','--no-cflow-deob','--dont-restore-props','--un-name','^[A-Za-z_][A-Za-z_0-9]*$')
 foreach($file in $ordered){
  $destination=Join-Path $OutputDirectory (Join-Path $order (Join-Path $file.Directory.Name $file.Name))
  if(!$Batch){New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null}
  $arguments+=@('-f',$file.FullName,'-p','un','--keep-types','-o',$destination)
 }
 if($Batch){
  & $De4dot --rename-public-api --no-cflow-deob --default-strtyp none --df-name '^(?!v$)[A-Za-z_][A-Za-z_0-9]*$' --batch (Join-Path $OutputDirectory 'input') --batch-output (Join-Path $OutputDirectory $order)
 }else{ & $De4dot @arguments }
 if($LASTEXITCODE -ne 0){throw 'Duplicate batch failed.'}
 foreach($version in 1,2){foreach($name in 'Client.exe','AlternateClient.exe'){
  & (Join-Path $OutputDirectory "$order\context-$version\$name")
  if($LASTEXITCODE -ne 0){throw 'Processed runtime bound the wrong duplicate.'}
 }}
 foreach($version in 1,2){foreach($name in 'Library.dll','AlternateLibrary.dll'){
  powershell.exe -NoProfile -File (Join-Path $PSScriptRoot 'Verify-Library.ps1') -Path (Join-Path $OutputDirectory "$order\context-$version\$name") -Expected $version
  if($LASTEXITCODE -ne 0){throw 'Self-reference bound the wrong duplicate.'}
 }}
 if($Batch){
  foreach($name in 'settings.txt','native.dll','Unprotected.dll'){
   if((Get-FileHash (Join-Path $OutputDirectory "input\$name")).Hash -ne (Get-FileHash (Join-Path $OutputDirectory "$order\$name")).Hash){throw 'Unchanged file was rewritten.'}
  }
  if(!(Test-Path (Join-Path $OutputDirectory "$order\empty") -PathType Container)){throw 'Empty directory missing.'}
  if(@(Get-ChildItem (Join-Path $OutputDirectory $order) -Recurse -File).Count -ne ($inputs.Count + 1)){throw 'File structure changed.'}
 }
}
if(Compare-Object $hashes @($inputs | Get-FileHash | ForEach-Object Hash)){throw 'Input assemblies changed.'}
if($Batch){
 $flat=Join-Path $OutputDirectory 'flat-input'
 New-Item -ItemType Directory -Path $flat | Out-Null
 foreach($version in 1,2){
  Copy-Item (Join-Path $root "context-$version\Client.exe") (Join-Path $flat "Client$version.exe")
  $name=if($version -eq 1){'Library.dll'}else{'LegacyLibrary.dll'}
  Copy-Item (Join-Path $root "context-$version\Library.dll") (Join-Path $flat $name)
 }
 $flatHashes=@(Get-ChildItem $flat -File | Get-FileHash | ForEach-Object Hash)
 $without=Join-Path $OutputDirectory 'flat-without-bindings'
 & $De4dot --rename-public-api --no-cflow-deob --default-strtyp none --batch $flat --batch-output $without
 if($LASTEXITCODE -eq 0 -or (Test-Path $without)){throw 'Mixed APIs without the required binding were silently accepted.'}
 foreach($order in 'forward','reverse'){
  $bindingArgs=@('--batch-binding','Client1.exe=Library.dll','--batch-binding','Client2.exe=LegacyLibrary.dll')
  if($order -eq 'reverse'){$bindingArgs=@('--batch-binding','Client2.exe=LegacyLibrary.dll','--batch-binding','Client1.exe=Library.dll')}
  $flatOutput=Join-Path $OutputDirectory "flat-$order"
  & $De4dot --rename-public-api --no-cflow-deob --default-strtyp none --df-name '^(?!v$)[A-Za-z_][A-Za-z_0-9]*$' --batch $flat --batch-output $flatOutput @bindingArgs
  if($LASTEXITCODE -ne 0){throw 'Explicit same-directory binding failed.'}
  foreach($version in 1,2){
   # Reproduce each custom loader's dependency choice in an isolated process.
   $runtime=Join-Path $OutputDirectory "flat-runtime-$order-$version"
   New-Item -ItemType Directory -Path $runtime | Out-Null
   Copy-Item (Join-Path $flatOutput "Client$version.exe") (Join-Path $runtime 'Client.exe')
   $name=if($version -eq 1){'Library.dll'}else{'LegacyLibrary.dll'}
   Copy-Item (Join-Path $flatOutput $name) (Join-Path $runtime 'Library.dll')
   & (Join-Path $runtime 'Client.exe')
   if($LASTEXITCODE -ne 0){throw 'Explicit binding changed API, reflection or virtual dispatch.'}
  }
 }
 if(Compare-Object $flatHashes @(Get-ChildItem $flat -File | Get-FileHash | ForEach-Object Hash)){throw 'Explicit binding changed inputs.'}
 $guardIndex=0
 foreach($bad in @(@('Client2.exe=missing.dll'),@('../input/context-2/Client.exe=Library.dll'),@('Client2.exe=LegacyLibrary.dll','Client2.exe=Library.dll'),@('Client1.exe=Client2.exe'),@('malformed'))){
  $guardOutput=Join-Path $OutputDirectory "binding-invalid-$guardIndex"
  $guardArgs=@(); foreach($value in $bad){$guardArgs+=@('--batch-binding',$value)}
  & $De4dot --rename-public-api --batch $flat --batch-output $guardOutput @guardArgs
  if($LASTEXITCODE -eq 0 -or (Test-Path $guardOutput)){throw 'Invalid binding was accepted.'}
  $guardIndex++
 }
 Write-Output 'PASS: explicit same-directory dependency bindings and validation guards'
 & $De4dot --rename-public-api --batch $root --batch-output (Join-Path $root 'nested-output')
 if($LASTEXITCODE -eq 0 -or (Test-Path (Join-Path $root 'nested-output'))){throw 'Overlap guard failed.'}
 & $De4dot --rename-public-api --batch $root --batch-output (Join-Path $OutputDirectory 'forward')
 if($LASTEXITCODE -eq 0){throw 'Existing output was accepted.'}
 $broken=Join-Path $OutputDirectory 'invalid-input'
 Copy-Item $root $broken -Recurse
 $library=Join-Path $broken 'context-1\Library.dll'
 $temporary=Join-Path $OutputDirectory 'broken-library.dll'
 dotnet run --project (Join-Path $emitter 'Emitter.csproj') -c Release --no-build -- --remove-get $library $temporary
 if($LASTEXITCODE -ne 0){throw 'Broken input emission failed.'}
 Copy-Item $temporary $library -Force
 $unpublished=Join-Path $OutputDirectory 'invalid-output'
 & $De4dot --rename-public-api --no-cflow-deob --default-strtyp none --batch $broken --batch-output $unpublished
 if($LASTEXITCODE -eq 0 -or (Test-Path $unpublished)){throw 'Invalid graph was published.'}
 Write-Output 'PASS: output guards and unresolved-reference publication gate'
}
