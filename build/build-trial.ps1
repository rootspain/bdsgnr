# Builds a trial release, obfuscating the app and calculation assemblies before they are packaged.
# Usage (from the repo root):
#   .\build\build-trial.ps1                             # 30-day trial, one self-contained exe, no dll (out/win-x64-trial-exe)
#   .\build\build-trial.ps1 -Folder                     # folder build with the dlls (out/win-x64-trial)
#   .\build\build-trial.ps1 -Days 14 -ValidDays 45      # 14-day trial, build stops working after 45 days
#   .\build\build-trial.ps1 -Rid linux-x64              # Linux build (from Windows too; the self-test then needs Linux or WSL)
# Obfuscation always runs on the compiled BlastDesigner*.dll first, then publish packages those dlls (single-file too).
# What obfuscation keeps and why is in build/obfuscar.xml.
param(
    [int]$Days = 30,
    [int]$ValidDays = 0,
    [string]$Rid = "win-x64",
    [switch]$Folder,
    [string]$Out = ""
)
$ErrorActionPreference = "Stop"
# Windows PowerShell 5.1 has no $IsWindows; it only runs on Windows
$onWindows = $PSVersionTable.PSEdition -eq "Desktop" -or $IsWindows
$sep = [IO.Path]::DirectorySeparatorChar
$SingleFile = -not $Folder
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
if (-not $Out) { $Out = if ($SingleFile) { "out/$Rid-trial-exe" } else { "out/$Rid-trial" } }
if (-not [IO.Path]::IsPathRooted($Out)) { $Out = Join-Path $root $Out }
$Out = [IO.Path]::GetFullPath($Out)
$trialProps = "-p:Trial=true", "-p:TrialDays=$Days", "-p:TrialValidDays=$ValidDays"

Write-Host "1/4  Сборка ($Days дней, rid=$Rid)..."
dotnet build src/BlastDesigner.App -c Release -r $Rid --self-contained true @trialProps | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed" }
$bin = Join-Path $root "src/BlastDesigner.App/bin/Release/net10.0/$Rid"

Write-Host "2/4  Обфускация приложения и библиотек расчётов (до упаковки)..."
$tool = [IO.Path]::Combine([Environment]::GetFolderPath("UserProfile"), ".dotnet", "tools", $(if ($onWindows) { "obfuscar.console.exe" } else { "obfuscar.console" }))
if (-not (Test-Path $tool)) { dotnet tool install --global Obfuscar.GlobalTool --version 2.2.40 | Out-Null }
$cfg = Join-Path $bin "_obfuscar.xml"
(Get-Content (Join-Path $PSScriptRoot "obfuscar.xml")) `
    -replace '<Var name="InPath" value="[^"]*" />', "<Var name=`"InPath`" value=`"$bin`" />" `
    -replace '<Var name="OutPath" value="[^"]*" />', "<Var name=`"OutPath`" value=`"$(Join-Path $bin '_obf')`" />" `
    -replace '\$\(InPath\)\\', "$bin$sep" |
    Set-Content $cfg -Encoding UTF8
$env:DOTNET_ROLL_FORWARD = "LatestMajor"   # the tool targets .NET 9; roll forward to an installed runtime
& $tool $cfg
if ($LASTEXITCODE -ne 0) { throw "obfuscar failed" }
# publish --no-build does not package the app's bin folder: it takes the app from its obj folder and each library from
# that library's own bin and obj, so the obfuscated copies go there too (and in bin, for runs from the build folder)
$modules = "BlastDesigner", "BlastDesigner.Domain", "BlastDesigner.Engine", "BlastDesigner.IO", "BlastDesigner.Rendering", "BlastDesigner.Recognition"
$replaced = @()
foreach ($m in $modules)
{
    $obf = Join-Path (Join-Path $bin "_obf") "$m.dll"
    if (-not (Test-Path $obf)) { throw "obfuscar did not write $m.dll" }
    $targets = if ($m -eq "BlastDesigner") { @("src/BlastDesigner.App/obj/Release/net10.0/$Rid/$m.dll") }
               else { @("src/$m/bin/Release/net10.0/$m.dll", "src/$m/obj/Release/net10.0/$m.dll") }
    foreach ($t in $targets)
    {
        $t = Join-Path $root $t
        if (-not (Test-Path $t)) { throw "publish source not found: $t" }
        Copy-Item $obf $t -Force
        $replaced += $t
    }
    Copy-Item $obf (Join-Path $bin "$m.dll") -Force
}
Remove-Item (Join-Path $bin "_obf") -Recurse -Force; Remove-Item $cfg -Force

Write-Host "3/4  Упаковка$(if ($SingleFile) { ' в один exe' })..."
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
$pub = "dotnet", "publish", "src/BlastDesigner.App", "-c", "Release", "-r", $Rid, "--self-contained", "true", "--no-build"
$pub += $trialProps
if ($SingleFile) { $pub += "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true" }
$pub += "-o", $Out
try
{
    & $pub[0] $pub[1..($pub.Count - 1)] | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }
}
finally
{
    # the next ordinary build compiles these again instead of taking the obfuscated copies as up to date
    $replaced | ForEach-Object { Remove-Item $_ -Force -ErrorAction SilentlyContinue }
}
if (-not $SingleFile)
{
    foreach ($m in $modules)
    {
        if ((Get-FileHash (Join-Path $Out "$m.dll")).Hash -ne (Get-FileHash (Join-Path $bin "$m.dll")).Hash) { throw "$m.dll was packaged without obfuscation" }
    }
}

Write-Host "4/4  Self-test..."
$report = Join-Path $Out "_selftest.txt"
if (($Rid -like "linux-*") -eq $onWindows)
{
    # a Linux build on Windows: run it in WSL when there is a distribution, otherwise leave the self-test to Linux
    $hasWsl = $onWindows -and (Get-Command wsl.exe -ErrorAction SilentlyContinue) -and (wsl.exe -l -q 2>$null)
    if (-not $hasWsl) { Write-Host "Self-test пропущен: сборка под $Rid здесь не запускается (нужен Linux или WSL)"; Write-Host "Готово: $Out"; return }
    $linuxOut = (wsl.exe wslpath -a ($Out -replace '\\', '/')).Trim()
    wsl.exe chmod +x "$linuxOut/BlastDesigner"
    wsl.exe "$linuxOut/BlastDesigner" --selftest "$linuxOut/_selftest.txt" | Out-Null
}
else
{
    & (Join-Path $Out $(if ($onWindows) { "BlastDesigner.exe" } else { "BlastDesigner" })) --selftest $report | Out-Null
}
$ok = $LASTEXITCODE -eq 0
Get-Content $report | Select-Object -Last 1
Remove-Item $report -Force
if (-not $ok) { throw "selftest failed — обфускация что-то сломала" }
Write-Host "Готово: $Out"
