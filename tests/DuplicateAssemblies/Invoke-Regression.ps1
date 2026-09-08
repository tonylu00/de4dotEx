param([Parameter(Mandatory)][string]$De4dot,[Parameter(Mandatory)][string]$OutputDirectory)
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
 "public class Api { public static int Get() { return Helper.Value(); } } public class Helper { public static int Value() { return $version; } }" | Set-Content (Join-Path $build 'library\Library.cs')
 "using System; class Program { static int Main() { if(Api.Get() != $version) throw new Exception(`"Wrong compatibility assembly`" ); Console.WriteLine(`"PASS: binding context $version`" ); return 0; } }" | Set-Content (Join-Path $build 'client\Client.cs')
 '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net48</TargetFramework><AssemblyVersion>1.0.0.0</AssemblyVersion></PropertyGroup></Project>' | Set-Content (Join-Path $build 'library\Library.csproj')
 '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net48</TargetFramework><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><ProjectReference Include="..\library\Library.csproj" /></ItemGroup></Project>' | Set-Content (Join-Path $build 'client\Client.csproj')
 dotnet build (Join-Path $build 'client\Client.csproj') -c Release --nologo -v quiet
 if($LASTEXITCODE -ne 0){throw 'Fixture build failed.'}
 $inputDir=Join-Path $OutputDirectory "input\context-$version"
 dotnet run --project (Join-Path $emitter 'Emitter.csproj') -c Release -- (Join-Path $build 'library\bin\Release\net48\Library.dll') (Join-Path $build 'client\bin\Release\net48\Client.exe') $inputDir
 if($LASTEXITCODE -ne 0){throw 'Emission failed.'}
 foreach($name in 'Client.exe','AlternateClient.exe') { & (Join-Path $inputDir $name); if($LASTEXITCODE -ne 0){throw 'Original runtime failed.'} }
 $inputs+=Get-ChildItem $inputDir -File
}
$hashes=@($inputs | Get-FileHash | ForEach-Object Hash)
foreach($order in 'forward','reverse'){
 $ordered=@($inputs)
 if($order -eq 'reverse'){[Array]::Reverse($ordered)}
 $arguments=@('--no-cflow-deob','--dont-restore-props','--un-name','^[A-Za-z_][A-Za-z_0-9]*$')
 foreach($file in $ordered){
  $destination=Join-Path $OutputDirectory (Join-Path $order (Join-Path $file.Directory.Name $file.Name))
  New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
  $arguments+=@('-f',$file.FullName,'-p','un','--keep-types','-o',$destination)
 }
 & $De4dot @arguments
 if($LASTEXITCODE -ne 0){throw 'Duplicate batch failed.'}
 foreach($version in 1,2){foreach($name in 'Client.exe','AlternateClient.exe'){
  & (Join-Path $OutputDirectory "$order\context-$version\$name")
  if($LASTEXITCODE -ne 0){throw 'Processed runtime bound the wrong duplicate.'}
 }}
 foreach($version in 1,2){foreach($name in 'Library.dll','AlternateLibrary.dll'){
  powershell.exe -NoProfile -File (Join-Path $PSScriptRoot 'Verify-Library.ps1') -Path (Join-Path $OutputDirectory "$order\context-$version\$name") -Expected $version
  if($LASTEXITCODE -ne 0){throw 'Self-reference bound the wrong duplicate.'}
 }}
}
if(Compare-Object $hashes @($inputs | Get-FileHash | ForEach-Object Hash)){throw 'Input assemblies changed.'}
