param([Parameter(Mandatory)][string]$De4dot,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $OutputDirectory){throw 'Choose a new output directory'}
$inputDir=Join-Path $OutputDirectory 'input'
$verifyDir=Join-Path $OutputDirectory 'verify'
New-Item -ItemType Directory -Path $inputDir,$verifyDir | Out-Null
'<Project />' | Set-Content (Join-Path $OutputDirectory 'Directory.Build.props')
$values=@('{0}.signature','certificate:{0}',([string][char]0+[char]0x4e2d+[char]0xd83d+[char]0xde00+[char]0xffff),'','validation succeeded:{0}')
$keys=@(15,-37,[int]::MaxValue,0,[int]::MinValue)
$calls=@()
for($i=0;$i -lt $values.Count;$i++){
 $key=[long]1234567+51+$keys[$i]; $encoded=''
 foreach($c in $values[$i].ToCharArray()){
  $low=((([int]$c -shr 8) -bxor $key) -band 255); $key++
  $high=((([int]$c -band 255) -bxor $key) -band 255); $key++
  $encoded+='\u{0:x4}' -f (($high -shl 8) -bor $low)
 }
 $calls+='Decode("'+$encoded+'", '+$keys[$i]+')'
}
(Get-Content (Join-Path $PSScriptRoot 'Fixture.cs') -Raw).Replace('/*VALUES*/',('return new string[] { '+($calls -join ',')+' };')) | Set-Content (Join-Path $inputDir 'Fixture.cs')
'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net48</TargetFramework><OutputType>Exe</OutputType><LangVersion>latest</LangVersion><Optimize>true</Optimize></PropertyGroup></Project>' | Set-Content (Join-Path $inputDir 'Fixture.csproj')
dotnet build (Join-Path $inputDir 'Fixture.csproj') -c Release --nologo -v quiet
if($LASTEXITCODE -ne 0){throw 'Fixture build failed'}
$original=Join-Path $inputDir 'bin\Release\net48\Fixture.exe'
$hash=(Get-FileHash $original).Hash
& $original
if($LASTEXITCODE -ne 0){throw 'Original fixture failed'}
$output=Join-Path $OutputDirectory 'processed'
& $De4dot --default-strtyp static --dont-rename --batch (Split-Path $original) --batch-output $output
if($LASTEXITCODE -ne 0){throw 'String batch failed'}
[xml]$journal=Get-Content (Join-Path $output 'de4dot-rename-map.xml') -Raw
if($journal.De4dotRenameMap.RequestedStringDecryption -ne 'Static' -or $journal.De4dotRenameMap.RenameSymbols -ne 'false'){throw 'Static string mode missing from batch journal'}
& (Join-Path $output 'Fixture.exe')
if($LASTEXITCODE -ne 0){throw 'Processed string behavior changed'}
Copy-Item (Join-Path $PSScriptRoot 'Verify.cs') $verifyDir
$dnlib=[Security.SecurityElement]::Escape((Join-Path (Split-Path ([IO.Path]::GetFullPath($De4dot))) 'dnlib.dll'))
"<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><Reference Include=`"dnlib`"><HintPath>$dnlib</HintPath></Reference></ItemGroup></Project>" | Set-Content (Join-Path $verifyDir 'Verify.csproj')
dotnet run --project (Join-Path $verifyDir 'Verify.csproj') -c Release -- (Join-Path $output 'Fixture.exe')
if($LASTEXITCODE -ne 0){throw 'Literal extraction verification failed'}
if((Get-FileHash $original).Hash -ne $hash){throw 'Input changed'}
$controlOutput=Join-Path $OutputDirectory 'control-flow-only'
& $De4dot --only-cflow-deob --batch (Split-Path $original) --batch-output $controlOutput
if($LASTEXITCODE){throw 'Control-flow-only batch failed'}
[xml]$journal=Get-Content (Join-Path $controlOutput 'de4dot-rename-map.xml') -Raw
if($journal.De4dotRenameMap.RequestedStringDecryption -ne 'None' -or $journal.De4dotRenameMap.ControlFlowDeobfuscation -ne 'true'){throw 'Control-flow-only mode missing from batch journal'}
& (Join-Path $controlOutput 'Fixture.exe')
if($LASTEXITCODE){throw 'Control-flow-only runtime changed'}
$overrideOutput=Join-Path $OutputDirectory 'control-flow-with-static-strings'
& $De4dot --only-cflow-deob --default-strtyp static --batch (Split-Path $original) --batch-output $overrideOutput
if($LASTEXITCODE){throw 'Explicit static override batch failed'}
[xml]$journal=Get-Content (Join-Path $overrideOutput 'de4dot-rename-map.xml') -Raw
if($journal.De4dotRenameMap.RequestedStringDecryption -ne 'Static' -or $journal.De4dotRenameMap.RenameSymbols -ne 'false'){throw 'Explicit static override was lost'}
& (Join-Path $overrideOutput 'Fixture.exe')
if($LASTEXITCODE){throw 'Static override runtime changed'}
dotnet run --project (Join-Path $verifyDir 'Verify.csproj') -c Release -- (Join-Path $overrideOutput 'Fixture.exe')
if($LASTEXITCODE){throw 'Static override left encrypted literals'}
'PASS explicit static, control-flow-only and static override behavior and recorded modes'
