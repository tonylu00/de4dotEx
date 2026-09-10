param([Parameter(Mandatory)][string]$De4dot,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $OutputDirectory){throw 'Choose a new test folder'}
New-Item -ItemType Directory $OutputDirectory | Out-Null
Copy-Item "$PSScriptRoot\ContractPolicy.cs" $OutputDirectory
$runtime=Split-Path ([IO.Path]::GetFullPath($De4dot))
$refs=foreach($name in 'de4dot.code','de4dot.blocks','dnlib'){
 $path=[Security.SecurityElement]::Escape((Join-Path $runtime ($name+'.dll')))
 "<Reference Include=`"$name`"><HintPath>$path</HintPath></Reference>"
}
"<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>net48</TargetFramework><OutputType>Exe</OutputType></PropertyGroup><ItemGroup>$($refs -join '')</ItemGroup></Project>" | Set-Content "$OutputDirectory\Policy.csproj"
dotnet build "$OutputDirectory\Policy.csproj" -c Release --nologo -v quiet
if($LASTEXITCODE){throw 'Policy regression build failed'}
& "$OutputDirectory\bin\Release\net48\Policy.exe"
if($LASTEXITCODE){throw 'Policy regression failed'}
