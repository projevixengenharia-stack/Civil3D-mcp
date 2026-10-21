<#
.SYNOPSIS
  Installs the plugin as an Autodesk ApplicationPlugins bundle (auto-load).

.DESCRIPTION
  The APPLOAD Startup Suite refuses folders that are not in TRUSTEDPATHS when
  SECURELOAD=1, which is the default and which reports failure only as a bare
  "Error" dialog. Bundles under ApplicationPlugins are Autodesk's supported
  deployment path and are implicitly trusted, so this sidesteps both TRUSTEDPATHS
  and the Startup Suite.

  Installs per-user under %APPDATA%, so no elevation is required.

.PARAMETER Uninstall
  Remove the bundle instead of installing it.
#>
param(
  [string] $SourceDir,
  [string] $BundleRoot = (Join-Path $env:APPDATA "Autodesk\ApplicationPlugins"),
  [string] $BundleName = "Civil3DMcp.bundle",
  [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'

$repoRoot  = Split-Path -Parent $PSScriptRoot

if (-not $Uninstall) {
  if (-not $SourceDir) {
    $SourceDir = Join-Path $repoRoot "Civil3D-MCP-Plugin\bin\Release\net10.0-windows"
  }
  $dll = Join-Path $SourceDir "Civil3DMcpPlugin.dll"
  if (-not (Test-Path $dll)) {
    throw "Plugin not built at '$dll'. Run scripts\build-2027.ps1 first."
  }

  # The bundle's RuntimeRequirements series must match the Civil 3D release the
  # DLL was compiled against, or AutoCAD silently never loads it. The build's
  # target framework (last path segment of the output dir) tells us which:
  # net48 is the 2024 build (R24.3), net8.0-windows the 2026 build (R25.1),
  # net10.0-windows the 2027 build (R26.0).
  $tfm = Split-Path -Leaf $SourceDir
  $series = switch ($tfm) {
    "net48"           { "R24.3" }
    "net8.0-windows"  { "R25.1" }
    "net10.0-windows" { "R26.0" }
    default { throw "Cannot infer the Civil 3D series from '$tfm'. Pass a build output dir named by target framework (net48, net8.0-windows or net10.0-windows)." }
  }

  # Civil 3D 2024 gets its own bundle (and package GUIDs below), so installing it
  # never replaces the 2026/2027 bundle. Uninstall it with -BundleName Civil3DMcp2024.bundle.
  if ($series -eq "R24.3" -and -not $PSBoundParameters.ContainsKey('BundleName')) {
    $BundleName = "Civil3DMcp2024.bundle"
  }
}

$bundleDir = Join-Path $BundleRoot $BundleName
$contents  = Join-Path $bundleDir "Contents"

# A running Civil 3D holds a lock on the loaded bundle DLL; deleting or copying
# over it fails partway and leaves a partial or mixed-version bundle. Probe the
# actual file locks rather than matching acad.exe by name: plain AutoCAD uses
# the same executable, and it does not load this Civil 3D bundle.
function Get-LockedBundleFile([string] $dir) {
  if (-not (Test-Path $dir)) { return $null }
  foreach ($f in Get-ChildItem $dir -Recurse -File -Filter *.dll) {
    $fs = $null
    try {
      $fs = [System.IO.File]::Open($f.FullName, 'Open', 'ReadWrite', 'None')
    } catch [System.IO.IOException] {
      return $f.FullName
    } catch [System.UnauthorizedAccessException] {
      # Access denied reads the same as locked for our purposes: we cannot
      # safely replace the file, so refuse with the friendly message.
      return $f.FullName
    } finally {
      if ($fs) { $fs.Dispose() }
    }
  }
  return $null
}

$locked = Get-LockedBundleFile $bundleDir
if ($locked) {
  throw "'$locked' is loaded by a running Civil 3D. Close it completely, then re-run."
}

if ($Uninstall) {
  if (Test-Path $bundleDir) {
    Remove-Item $bundleDir -Recurse -Force
    Write-Host "Removed $bundleDir"
  } else {
    Write-Host "Nothing to remove at $bundleDir"
  }
  Write-Host "Restart Civil 3D for the change to take effect."
  exit 0
}

New-Item -ItemType Directory -Path $contents -Force | Out-Null

# The six Autodesk references are Private=false in the csproj, so they never
# land in bin\; deny-list them anyway so a hand-copied set can't be shipped.
$autodeskRefs = @("accoremgd.dll", "AcDbMgd.dll", "acmgd.dll", "AecBaseMgd.dll", "AeccDbMgd.dll", "AeccPressurePipesMgd.dll")
$copied = 0
foreach ($f in Get-ChildItem $SourceDir -File) {
  if ($autodeskRefs -contains $f.Name) { continue }
  Copy-Item $f.FullName -Destination (Join-Path $contents $f.Name) -Force
  $copied++
}

$appVersion = (Get-Item $dll).VersionInfo.FileVersion
if (-not $appVersion) { $appVersion = "1.0.0.0" }

# Stable GUIDs so reinstalls upgrade in place rather than registering a duplicate.
# The 2024 bundle is a separate package installed alongside, so it has its own pair.
$productCode, $upgradeCode = if ($series -eq "R24.3") {
  "{6C5A9E86-FF56-4723-9DBA-291AA6A14F3B}", "{E0449849-1ABF-40AF-8B37-F6DCA9AE57BC}"
} else {
  "{3F7C1A94-6E2D-4B85-9C13-5A8E0D2F7B41}", "{8B24E5D7-1C3F-4A69-B0E2-7D9146C8F3A5}"
}
$xml = @"
<?xml version="1.0" encoding="utf-8"?>
<ApplicationPackage
  SchemaVersion="1.0"
  AppVersion="$appVersion"
  Author="Sacred-G"
  ProductCode="$productCode"
  UpgradeCode="$upgradeCode"
  Name="Civil 3D MCP Plugin"
  PreferNewestAcross="AppData|ProgramFiles"
  >
  <CompanyDetails Name="Sacred-G" Url="https://github.com/Sacred-G/Civil3D-mcp" Email="" />
  <RuntimeRequirements Platform="Civil3D" SeriesMin="$series" SeriesMax="$series" OS="Win64" SupportPath="./Contents" />
  <Components>
    <ComponentEntry AppName="Civil3DMcp" ModuleName="./Contents/Civil3DMcpPlugin.dll"
                    LoadOnAutoCADStartup="true" LoadOnRequest="false" AppDescription="Civil 3D MCP JSON-RPC bridge">
    </ComponentEntry>
  </Components>
  <DisplayInAppManager>true</DisplayInAppManager>
</ApplicationPackage>
"@

# PackageContents.xml must be UTF-8 without BOM or AutoCAD ignores the bundle.
[System.IO.File]::WriteAllText(
  (Join-Path $bundleDir "PackageContents.xml"),
  $xml,
  (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Installed bundle: $bundleDir"
Write-Host "  PackageContents.xml  (AppVersion $appVersion)"
Write-Host "  Contents\            ($copied file(s))"
Write-Host ""
Write-Host "Close Civil 3D COMPLETELY and reopen it. The plugin auto-loads;"
Write-Host "no NETLOAD, no Startup Suite, no TRUSTEDPATHS entry needed."
Write-Host "Verify with C3DMCPSTART (echoes the port)."
