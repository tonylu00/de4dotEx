param([Parameter(Mandatory)][string[]]$Tools,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $OutputDirectory){throw 'Choose a new directory.'}
$root=Join-Path $OutputDirectory 'input'
New-Item -ItemType Directory -Path $root | Out-Null
'<Project />' | Set-Content (Join-Path $OutputDirectory 'Directory.Build.props')
foreach($variant in @('Old','New')) {
 $source=Join-Path $OutputDirectory $variant
 $lib=Join-Path $source 'lib'
 $app=Join-Path $source 'app'
 New-Item -ItemType Directory -Path $lib,$app | Out-Null
 $offset=if($variant -eq 'Old'){10}else{30}
 "public interface I<T> { T a(T q); } public class Z {} public class C : I<int> { public enum E { X=3 } public virtual int a(int q) { return q+$offset; } public static T b<T>(T q) { return q; } public static string n() { return `"n`"; } public int d(int q) { return q; } public override string ToString() { return `"fixture`"; } }" | Set-Content (Join-Path $lib 'Library.cs')
 '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup></Project>' | Set-Content (Join-Path $lib 'Library.csproj')
 "using System; class D:C { public override int a(int q) { return base.a(q)+1; } } class App { static int Main() { I<int> v=new D(); if(v.a(2)!=$($offset+3) || C.b(7)!=7 || (int)C.E.X!=3 || !typeof(C.E).IsEnum) return 1; Console.WriteLine(`"PASS $variant dispatch/generic/enum`"); return 0; } }" | Set-Content (Join-Path $app 'App.cs')
 '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net48</TargetFramework><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><ProjectReference Include="..\lib\Library.csproj" /></ItemGroup></Project>' | Set-Content (Join-Path $app 'App.csproj')
 dotnet build (Join-Path $app 'App.csproj') -c Release --nologo -v quiet
 if($LASTEXITCODE -ne 0){throw 'Fixture build failed.'}
 Copy-Item -LiteralPath (Join-Path $app 'bin\Release\net48') -Destination (Join-Path $root $variant) -Recurse
 New-Item -ItemType Directory -Path (Join-Path $root "$variant\empty") | Out-Null
 'data' | Set-Content (Join-Path $root "$variant\file.txt")
}
$originalHashes=@(Get-ChildItem $root -Recurse -File | Get-FileHash | ForEach-Object Hash)
$toolIndex=0
foreach($tool in $Tools) {
 $toolIndex++
 $inventory=Join-Path $OutputDirectory "inventory-$toolIndex.xml"
 & $tool --name-map-input $root --name-map-export $inventory
 if($LASTEXITCODE -ne 0){throw 'Inventory failed.'}
 [xml]$map=Get-Content $inventory
 $module=@($map.NameMap.Module | Where-Object Path -eq 'Old\Library.dll')[0]
 ($module.Type | Where-Object ExpectedName -eq C).SetAttribute('NewName','Calculator')
 ($module.Type | Where-Object ExpectedName -eq E).SetAttribute('NewName','ResultKind')
 ($module.Method | Where-Object { $_.ExpectedName -eq 'a' -and $_.Signature.Contains('I`1') }).SetAttribute('NewName','Calculate')
 $identity=$module.Method | Where-Object ExpectedName -eq b
 $identity.SetAttribute('NewName','Identity')
 $identity.Parameter.SetAttribute('NewName','value')
 $mapPath=Join-Path $OutputDirectory "map-$toolIndex.xml"
 $map.Save($mapPath)
 $report=Join-Path $OutputDirectory "report-$toolIndex.xml"
 & $tool --name-map-input $root --name-map $mapPath --name-map-preview --name-map-report $report
 if($LASTEXITCODE -ne 0){throw 'Preview failed.'}
 $out=Join-Path $OutputDirectory "result-$toolIndex"
 & $tool --name-map-input $root --name-map $mapPath --name-map-output $out
 if($LASTEXITCODE -ne 0){throw 'Apply failed.'}
 foreach($variant in @('Old','New')) {
  & (Join-Path $out "$variant\App.exe")
  if($LASTEXITCODE -ne 0){throw 'Runtime behavior changed.'}
 }
 if((Get-FileHash (Join-Path $out 'New\Library.dll')).Hash -ne (Get-FileHash (Join-Path $root 'New\Library.dll')).Hash){throw 'Unmapped compatibility context changed.'}
 if(@(Get-ChildItem $root -Recurse -Force).Count -ne @(Get-ChildItem $out -Recurse -Force).Count){throw 'Tree shape changed.'}
 $after=Join-Path $OutputDirectory "after-$toolIndex.xml"
 & $tool --name-map-input $out --name-map-export $after
 if($LASTEXITCODE -ne 0){throw 'Post-write inventory failed.'}
 [xml]$saved=Get-Content $after
 $savedLibrary=$saved.NameMap.Module | Where-Object Path -eq 'Old\Library.dll'
 if(!($savedLibrary.Type | Where-Object ExpectedName -eq Calculator) -or !($savedLibrary.Type | Where-Object ExpectedName -eq ResultKind) -or ($savedLibrary.Method | Where-Object ExpectedName -eq Identity).Parameter.ExpectedName -ne 'value'){throw 'Saved names incorrect.'}
 if(@($saved.NameMap.Module.Method | Where-Object ExpectedName -eq Calculate).Count -ne 3){throw 'Virtual family did not follow mapping.'}
 $ambiguousRoot=Join-Path $OutputDirectory "ambiguous-input-$toolIndex"
 Copy-Item -LiteralPath $root -Destination $ambiguousRoot -Recurse
 Copy-Item -LiteralPath (Join-Path $ambiguousRoot 'Old\Library.dll') -Destination (Join-Path $ambiguousRoot 'Old\Alternate.dll')
 & $tool --name-map-input $ambiguousRoot --name-map $mapPath --name-map-preview
 if($LASTEXITCODE -eq 0){throw 'Ambiguous binding was guessed.'}
 [xml]$explicitMap=Get-Content $mapPath
 $binding=$explicitMap.CreateElement('Binding')
 $binding.SetAttribute('Source','Old\App.exe')
 $binding.SetAttribute('Dependency','Old\Library.dll')
 $null=$explicitMap.NameMap.AppendChild($binding)
 $explicitPath=Join-Path $OutputDirectory "explicit-$toolIndex.xml"
 $explicitMap.Save($explicitPath)
 & $tool --name-map-input $ambiguousRoot --name-map $explicitPath --name-map-preview
 if($LASTEXITCODE -ne 0){throw 'Explicit duplicate binding failed.'}
 foreach($negative in @('stale','collision','external','parameter','reflection','methodcollision','genericparameter')) {
  [xml]$bad=Get-Content $inventory
  $badModule=$bad.NameMap.Module | Where-Object Path -eq 'Old\Library.dll'
  switch($negative) {
   stale { $badModule.SetAttribute('Mvid',[Guid]::NewGuid().ToString()) }
   collision { ($badModule.Type | Where-Object ExpectedName -eq C).SetAttribute('NewName','Z') }
   genericparameter { ($badModule.Method | Where-Object ExpectedName -eq b).Parameter.SetAttribute('NewName','T') }
   reflection { ($badModule.Method | Where-Object ExpectedName -eq n).SetAttribute('NewName','ReadMarker') }
   methodcollision { ($badModule.Method | Where-Object ExpectedName -eq d).SetAttribute('NewName','a') }
   external { ($badModule.Method | Where-Object ExpectedName -eq ToString).SetAttribute('NewName','Display') }
   parameter { $badParam=($badModule.Method | Where-Object ExpectedName -eq b).Parameter; $badParam.SetAttribute('Sequence','0'); $badParam.SetAttribute('NewName','value') }
  }
  $badPath=Join-Path $OutputDirectory "bad-$toolIndex-$negative.xml"
  $bad.Save($badPath)
  $badOut=Join-Path $OutputDirectory "bad-$toolIndex-$negative"
  & $tool --name-map-input $root --name-map $badPath --name-map-output $badOut
  if($LASTEXITCODE -eq 0 -or (Test-Path $badOut)){throw "Invalid $negative map published."}
 }
}
if(Compare-Object $originalHashes @(Get-ChildItem $root -Recurse -File | Get-FileHash | ForEach-Object Hash)){throw 'Inputs changed.'}
Write-Output 'PASS: all mapping frontends, duplicate contexts, runtime behavior, names, virtual families, preview and rejection cases.'
exit 0
