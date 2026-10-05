<#
.SYNOPSIS
  Builds PlumeFX.dll with the .NET Framework C# compiler, optionally rebuilds the shader bundle with Unity, deploys
  to KSP and packages a release zip.

.PARAMETER KspPath
  Path of the KSP installation (folder that contains KSP_x64_Data). Defaults to the environment variable KSP_PATH.

.PARAMETER Shaders
  Rebuild GameData/PlumeFX/Shaders/plumefx.shaders from Unity/Assets/Shaders with Unity 2019.4.18f1
  (-UnityExe or the environment variable UNITY_EXE). Without it the committed bundle is used.

.PARAMETER Deploy
  Copy GameData/PlumeFX into <KspPath>/GameData (KSP must be closed: it locks the DLL while running).

.PARAMETER Package
  Create PlumeFX-<version>.zip (GameData/PlumeFX plus README and LICENSE) in the repository root.

.EXAMPLE
  ./build.ps1 -KspPath "C:\Games\Kerbal Space Program" -Deploy
#>
param(
    [string]$KspPath = $env:KSP_PATH,
    [string]$UnityExe = $env:UNITY_EXE,
    [switch]$Shaders,
    [switch]$Deploy,
    [switch]$Package
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $KspPath) { throw 'Set -KspPath or the environment variable KSP_PATH to your KSP installation.' }
$managed = Join-Path $KspPath 'KSP_x64_Data\Managed'
if (-not (Test-Path $managed)) { throw "Not a KSP installation (missing $managed)." }

if ($Shaders) {
    if (-not $UnityExe) { $UnityExe = 'C:\Program Files\Unity\Hub\Editor\2019.4.18f1\Editor\Unity.exe' }
    $project = Join-Path $root 'Unity'
    $log = Join-Path $project 'build.log'
    & $UnityExe -batchmode -quit -projectPath $project -executeMethod BuildBundles.Build -logFile $log | Out-Null
    $bundle = Join-Path $project 'Build\plumefx.shaders'
    if ($LASTEXITCODE -ne 0 -or -not (Select-String -Path $log -Pattern 'PLUMEFX_BUILD_DONE' -Quiet)) { throw "Shader build failed, see $log" }
    Copy-Item -Force $bundle (Join-Path $root 'GameData\PlumeFX\Shaders\plumefx.shaders')
    Write-Host "Built shader bundle"
}

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$refs = 'mscorlib', 'System', 'System.Core', 'UnityEngine', 'UnityEngine.CoreModule', 'UnityEngine.PhysicsModule',
        'UnityEngine.AssetBundleModule', 'UnityEngine.UI', 'UnityEngine.ParticleSystemModule', 'Assembly-CSharp' |
        ForEach-Object { '-r:' + (Join-Path $managed "$_.dll") }
$out = Join-Path $root 'GameData\PlumeFX\Plugins\PlumeFX.dll'
$sources = (Join-Path $root 'Source\PlumeFX.cs'), (Join-Path $root 'Unity\Assets\PlumeFX\Core\PlumeCore.cs')
& $csc -nologo -target:library -nostdlib -noconfig -codepage:65001 -optimize "-out:$out" $refs $sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Host "Built $out"

if ($Deploy) {
    $dest = Join-Path $KspPath 'GameData\PlumeFX'
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item -Recurse -Force (Join-Path $root 'GameData\PlumeFX\*') $dest
    Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $dest 'Plugins\.gitkeep')
    Write-Host "Deployed to $dest"
}

if ($Package) {
    $ver = (Get-Content (Join-Path $root 'GameData\PlumeFX\PlumeFX.version') -Raw | ConvertFrom-Json).VERSION
    $name = "PlumeFX-$($ver.MAJOR).$($ver.MINOR).$($ver.PATCH).zip"
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('PlumeFX_pkg_' + [Guid]::NewGuid())
    New-Item -ItemType Directory -Path (Join-Path $tmp 'GameData') | Out-Null
    Copy-Item -Recurse (Join-Path $root 'GameData\PlumeFX') (Join-Path $tmp 'GameData\PlumeFX')
    Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $tmp 'GameData\PlumeFX\Plugins\.gitkeep')
    Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE') $tmp
    $zip = Join-Path $root $name
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path (Join-Path $tmp '*') -DestinationPath $zip
    Remove-Item -Recurse -Force $tmp
    Write-Host "Packaged $zip"
}
