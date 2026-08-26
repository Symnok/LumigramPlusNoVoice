# Increments the app version so each build installs as an upgrade.
#
# Windows Phone treats a package with the same ProductID and a HIGHER version as
# an update: isolated storage survives, so the signed-in session, the media cache
# and any settings are kept. An equal or lower version is refused outright, which
# is why a rebuilt fix otherwise looks like it changed nothing.
#
# Three files have to agree, and they are written differently:
#
#   Properties/WMAppManifest.xml   <App ... Version="1.0.0.5" ...>
#   Package.appxmanifest           <Identity ... Version="1.0.0.5" />
#   Properties/AssemblyInfo.cs     [assembly: AssemblyVersion("1.0.0.5")]
#
# Every field is 16-bit, so 65535 is the ceiling; the revision rolls into the
# build number rather than overflowing.
#
# Run manually, or let the build do it - see the BumpPackageVersion target in
# Lumigram.Phone.csproj. Pass -Show to report the version without changing it.
param(
    [switch]$Show,
    # Prints only the resulting version, so a build can capture it and use it in
    # the package filename.
    [switch]$Quiet
)
$ErrorActionPreference = 'Stop'

$phoneDir = Join-Path $PSScriptRoot 'Phone'
$wmPath   = Join-Path $phoneDir 'Properties\WMAppManifest.xml'
$appxPath = Join-Path $phoneDir 'Package.appxmanifest'
$asmPath  = Join-Path $phoneDir 'Properties\AssemblyInfo.cs'

foreach ($p in @($wmPath, $appxPath, $asmPath)) {
    if (-not (Test-Path $p)) { throw "not found: $p" }
}

$wm = Get-Content $wmPath -Raw

# Match the App element's own Version, not AppPlatformVersion.
$appMatch = [regex]::Match($wm, '(?<prefix><App\b[^>]*?\sVersion=")(?<version>\d+\.\d+\.\d+\.\d+)(?<suffix>")')
if (-not $appMatch.Success) { throw "could not find the App Version in $wmPath" }

$current = [version]$appMatch.Groups['version'].Value

if ($Show) {
    if ($Quiet) { Write-Output "$current" } else { Write-Output "current version: $current" }
    exit 0
}

$major    = $current.Major
$minor    = $current.Minor
$build    = $current.Build
$revision = $current.Revision + 1

# Version fields are 16-bit.
if ($revision -gt 65535) {
    $revision = 0
    $build++
}
if ($build -gt 65535) { throw "build number exhausted; raise the minor version by hand" }

$next = "$major.$minor.$build.$revision"

# WMAppManifest: only the App element's Version
$wm = $wm.Remove($appMatch.Groups['version'].Index, $appMatch.Groups['version'].Length)
$wm = $wm.Insert($appMatch.Groups['version'].Index, $next)
[System.IO.File]::WriteAllText($wmPath, $wm)

# Package.appxmanifest: the Identity element's Version
$appx = Get-Content $appxPath -Raw
$identity = [regex]::Match($appx, '(?<prefix><Identity\b[^>]*?\sVersion=")(?<version>\d+\.\d+\.\d+\.\d+)(?<suffix>")')
if ($identity.Success) {
    $appx = $appx.Remove($identity.Groups['version'].Index, $identity.Groups['version'].Length)
    $appx = $appx.Insert($identity.Groups['version'].Index, $next)
    [System.IO.File]::WriteAllText($appxPath, $appx)
} else {
    Write-Warning "no Identity Version found in $appxPath"
}

# AssemblyInfo: keeps the assembly in step, so the About screen can show it
$asm = Get-Content $asmPath -Raw
$asm = [regex]::Replace($asm, 'AssemblyVersion\("\d+\.\d+\.\d+\.\d+"\)', "AssemblyVersion(""$next"")")
$asm = [regex]::Replace($asm, 'AssemblyFileVersion\("\d+\.\d+\.\d+\.\d+"\)', "AssemblyFileVersion(""$next"")")
[System.IO.File]::WriteAllText($asmPath, $asm)

if ($Quiet) { Write-Output "$next" } else { Write-Output "version $current -> $next" }
