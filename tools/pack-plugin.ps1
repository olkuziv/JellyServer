<#
.SYNOPSIS
  Builds a plugin into a package and adds it to a plugin repository folder you can serve with any static web server (nginx, ...).

.DESCRIPTION
  Publishes the plugin project, zips it as <Name>/<files> (the folder the server expects), computes the MD5 and writes
  <OutDir>/<Name>_<Version>.zip plus <OutDir>/manifest.json (Jellyfin repository format). Running it again with a new version
  adds that version to the plugin's entry; older zips stay available.

  In the dashboard (Plugins > Repositories) add the URL of manifest.json. Download links in the manifest are relative to it.

.EXAMPLE
  ./tools/pack-plugin.ps1 -Project plugins-dev/MyPlugin/MyPlugin.csproj -OutDir C:\nginx\html\plugins `
      -Guid 00000000-0000-0000-0000-000000000001 -Description "What the plugin does" -Owner me
#>
param(
    [Parameter(Mandatory)] [string] $Project,
    [Parameter(Mandatory)] [string] $OutDir,
    # Stable identity of the plugin: keep the same Guid for every version.
    [Parameter(Mandatory)] [guid] $Guid,
    [string] $Name,
    [string] $Version,
    [string] $Description = '',
    [string] $Overview = '',
    [string] $Owner = '',
    [string] $Category = 'General',
    [string] $Changelog = ''
)

$ErrorActionPreference = 'Stop'
$projectPath = (Resolve-Path $Project).Path
[xml]$csproj = Get-Content $projectPath
$props = $csproj.Project.PropertyGroup
if (-not $Name) { $Name = @($props.AssemblyName | Where-Object { $_ })[0] }
if (-not $Name) { $Name = [IO.Path]::GetFileNameWithoutExtension($projectPath) }
if (-not $Version) { $Version = @($props.Version | Where-Object { $_ })[0] }
if (-not $Version) { throw 'The project has no <Version>; pass -Version.' }
$parts = $Version.Split('.')
while ($parts.Count -lt 4) { $parts += '0' }
$Version = ($parts[0..3] -join '.')

$work = Join-Path ([IO.Path]::GetTempPath()) ("pack-plugin-" + [guid]::NewGuid().ToString('N'))
$publish = Join-Path $work $Name
try {
    dotnet publish $projectPath -c Release -o $publish --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
    if (-not (Test-Path (Join-Path $publish "$Name.dll"))) { throw "$Name.dll was not produced; the main assembly must be named like the plugin." }

    New-Item -ItemType Directory -Force $OutDir | Out-Null
    $zipName = "${Name}_$Version.zip"
    $zipPath = Join-Path (Resolve-Path $OutDir).Path $zipName
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($publish, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $true)
    $md5 = (Get-FileHash $zipPath -Algorithm MD5).Hash.ToLowerInvariant()

    $manifestPath = Join-Path (Resolve-Path $OutDir).Path 'manifest.json'
    $manifest = @()
    # ForEach-Object unrolls the array Windows PowerShell 5.1's ConvertFrom-Json returns as one object.
    if (Test-Path $manifestPath) { $manifest = @(Get-Content $manifestPath -Raw | ConvertFrom-Json | ForEach-Object { $_ }) }
    $entry = $manifest | Where-Object { $_.guid -eq $Guid.ToString() } | Select-Object -First 1
    if (-not $entry) {
        $entry = [pscustomobject]@{ guid = $Guid.ToString(); name = $Name; description = $Description; overview = $Overview; owner = $Owner; category = $Category; versions = @() }
        $manifest += $entry
    } else {
        $entry.name = $Name
        if ($Description) { $entry.description = $Description }
        if ($Overview) { $entry.overview = $Overview }
        if ($Owner) { $entry.owner = $Owner }
    }
    $versions = @($entry.versions | Where-Object { $_.version -ne $Version })
    $versions += [pscustomobject]@{
        version = $Version; changelog = $Changelog; targetAbi = ''; sourceUrl = $zipName; checksum = $md5
        timestamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    }
    $entry.versions = @($versions | Sort-Object { [version]$_.version } -Descending)

    # UTF-8 without a BOM (Set-Content -Encoding UTF8 adds one in Windows PowerShell 5.1).
    [IO.File]::WriteAllText($manifestPath, (ConvertTo-Json -InputObject @($manifest) -Depth 6), (New-Object Text.UTF8Encoding $false))
    Write-Host "Packed $zipName (MD5 $md5)"
    Write-Host "Manifest: $manifestPath"
} finally {
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
}
