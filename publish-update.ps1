param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Exe = "",
    [string]$Notes = "",
    # Public URL of the storage folder the parts will be uploaded to (Supabase: .../storage/v1/object/public/releases/<version>)
    [string]$BaseUrl = "",
    [string]$OutDir = ""
)
# Prepares an in-app update: splits the launcher exe into parts below Supabase's 50 MB upload limit and
# prints the SQL row for public.launcher_releases. Upload the parts to the URLs shown, then run the SQL.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path ([Environment]::GetFolderPath("Desktop")) "Resurge Whiteout $Version Windows x64\Resurge Whiteout.exe" }
if (-not $OutDir) { $OutDir = Join-Path $root "_release\$Version" }
if (-not (Test-Path $Exe)) { throw "Launcher exe not found: $Exe" }
$product = (Get-Item $Exe).VersionInfo.ProductVersion
if ($product -ne $Version) { throw "The exe says version $product, not $Version." }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$bytes = [IO.File]::ReadAllBytes($Exe)
$partSize = 45MB
$parts = @()
for ($i = 0; $i * $partSize -lt $bytes.Length; $i++) {
    $name = "whiteout-$Version.part$($i + 1)"
    $count = [Math]::Min($partSize, $bytes.Length - $i * $partSize)
    $chunk = New-Object byte[] $count
    [Array]::Copy($bytes, $i * $partSize, $chunk, 0, $count)
    [IO.File]::WriteAllBytes((Join-Path $OutDir $name), $chunk)
    $parts += $name
}
$sha = (Get-FileHash $Exe -Algorithm SHA256).Hash.ToLowerInvariant()
$urls = if ($BaseUrl) { $parts | ForEach-Object { "$($BaseUrl.TrimEnd('/'))/$_" } } else { $parts | ForEach-Object { Join-Path $OutDir $_ } }

# Local manifest (same shape the server returns) for testing with WHITEOUT_UPDATE_MANIFEST.
$manifest = @(@{ version = $Version; urls = @($urls); sha256 = $sha; size = $bytes.Length; notes = $Notes })
ConvertTo-Json -InputObject $manifest -Depth 4 | Set-Content (Join-Path $OutDir "manifest.json") -Encoding utf8

$sqlUrls = ($urls | ForEach-Object { "'" + ($_ -replace "'", "''") + "'" }) -join ", "
$sql = "insert into public.launcher_releases (version, urls, sha256, size, notes) values ('$Version', array[$sqlUrls], '$sha', $($bytes.Length), '$($Notes -replace "'", "''")') on conflict (version) do update set urls = excluded.urls, sha256 = excluded.sha256, size = excluded.size, notes = excluded.notes, published_at = now();"
Set-Content (Join-Path $OutDir "release.sql") $sql -Encoding utf8
"Parts:   $($parts -join ', ')  ($([Math]::Round($bytes.Length / 1MB, 1)) MB)"
"SHA-256: $sha"
"Folder:  $OutDir (manifest.json, release.sql)"
