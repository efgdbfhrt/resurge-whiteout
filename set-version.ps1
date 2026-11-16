param([Parameter(Mandatory)][string]$Version)
# Sets the launcher version everywhere it appears (csproj, sidebar label, HTTP user agents, build default).
$root = $PSScriptRoot
$src = Join-Path $root "resurge_portable_source"
$edits = @(
    @{ File = "$src\ResurgeLauncherCSharp.csproj"; Pattern = '<FileVersion>[\d.]+</FileVersion>'; Value = "<FileVersion>$Version.0.0</FileVersion>" },
    @{ File = "$src\ResurgeLauncherCSharp.csproj"; Pattern = '<InformationalVersion>[\d.]+</InformationalVersion>'; Value = "<InformationalVersion>$Version</InformationalVersion>" },
    @{ File = "$src\MainWindow.xaml"; Pattern = 'Text="Whiteout [\d.]+"'; Value = "Text=""Whiteout $Version""" },
    @{ File = "$src\Services\WorkshopBrowser.cs"; Pattern = 'ResurgeWhiteout/[\d.]+'; Value = "ResurgeWhiteout/$Version" },
    @{ File = "$src\Services\GumIcons.cs"; Pattern = 'ResurgeWhiteout/[\d.]+'; Value = "ResurgeWhiteout/$Version" },
    @{ File = "$src\Services\BackgroundArt.cs"; Pattern = 'ResurgeWhiteout/[\d.]+'; Value = "ResurgeWhiteout/$Version" },
    @{ File = "$root\build.ps1"; Pattern = '\[string\]\$Version = "[\d.]+"'; Value = "[string]`$Version = ""$Version""" }
)
foreach ($edit in $edits) {
    $text = [IO.File]::ReadAllText($edit.File)
    $new = [regex]::Replace($text, $edit.Pattern, $edit.Value)
    if ($new -ne $text) { [IO.File]::WriteAllText($edit.File, $new, (New-Object Text.UTF8Encoding $false)) }
}
"version set to $Version"
