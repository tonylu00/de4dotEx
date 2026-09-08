param([string]$Path,[int]$Expected)
$ErrorActionPreference='Stop'
$assembly=[Reflection.Assembly]::LoadFrom($Path)
if(@($assembly.GetTypes() | Where-Object { $_.Name -eq '<Api>' -or $_.Name -eq '<Helper>' }).Count){throw 'Fixture types were not renamed.'}
$method=@($assembly.GetTypes() | ForEach-Object { $_.GetMethods() } | Where-Object { $_.Name -eq 'Get' -and $_.IsStatic })
if($method.Count -ne 1 -or $method[0].Invoke($null,@()) -ne $Expected){throw 'Library self-reference changed.'}
Write-Output "PASS: library self-reference $Expected"
