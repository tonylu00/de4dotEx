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
$originalOutput = & $original
if($LASTEXITCODE -ne 0){throw 'Original runtime failed'}
if($originalOutput -notcontains 'guard:is tampered'){throw 'Original guard output missing'}
if($originalOutput -notcontains 'debug:Debugger Detected'){throw 'Original debug output missing'}
$output=Join-Path $OutputDirectory 'processed.exe'
# The CLI pauses for a key when it cannot tell it runs from a terminal; suppress
# that so automation never blocks, and relax EAP while capturing stderr lines.
$hadShell = Test-Path env:SHELL
$oldShell = $env:SHELL
$env:SHELL = 'invoke-regression'
$arguments = @()
if($De4dot -like '*.dll'){ $arguments += 'dotnet'; $arguments += $De4dot } else { $arguments += $De4dot }
$arguments += @('-f',$original,'-p','dr4','--dont-rename','-o',$output)
$previousErrorAction = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$de4dotOutput = & $arguments[0] $arguments[1..($arguments.Count - 1)] 2>&1
$de4dotExit = $LASTEXITCODE
$ErrorActionPreference = $previousErrorAction
if($hadShell){$env:SHELL=$oldShell}else{Remove-Item env:SHELL -ErrorAction SilentlyContinue}
if($de4dotExit -ne 0){$de4dotOutput | Write-Host; throw 'Processing failed'}
$processedOutput = & $output
if($LASTEXITCODE -ne 0){throw 'Processed runtime failed'}
foreach($line in @('guard:is tampered','debug:Debugger Detected')){
	if($processedOutput -contains $line){throw "Guard call survived processing: $line"}
}
if($processedOutput -notcontains 'PASS'){throw 'Processed output missing PASS'}
if((Get-FileHash $original).Hash -ne $hash){throw 'Input changed'}
Write-Host 'PASS: tamper/debug guards neutralized, calls removed, behavior preserved'
