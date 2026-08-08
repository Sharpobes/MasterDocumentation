param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$appRoot = Join-Path $ProjectRoot 'src\MasterDocumentation.App'
$values = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

$xamlAttributePattern = '(?:Text|Content|Header|Title|ToolTip|AutomationProperties\.Name|AutomationProperties\.HelpText)="((?:&quot;|[^"])*)"'
Get-ChildItem $appRoot -Recurse -Filter *.xaml | ForEach-Object {
    $source = [IO.File]::ReadAllText($_.FullName)
    foreach ($match in [regex]::Matches($source, $xamlAttributePattern)) {
        $value = [Net.WebUtility]::HtmlDecode($match.Groups[1].Value)
        if ($value -match '[\u0410-\u044F\u0401\u0451]' -and -not $value.StartsWith('{Binding', [StringComparison]::Ordinal)) {
            [void]$values.Add($value)
        }
    }
}

# Regular and interpolated C# literals. Verbatim strings are deliberately excluded: they
# are predominantly regular expressions or document payloads rather than interface text.
$csharpStringPattern = '(?<![\\@])\$?"((?:\\.|[^"\\])*[\u0410-\u044F\u0401\u0451](?:\\.|[^"\\])*)"'
Get-ChildItem $appRoot -Recurse -Filter *.cs | ForEach-Object {
    $source = [IO.File]::ReadAllText($_.FullName)
    foreach ($match in [regex]::Matches($source, $csharpStringPattern)) {
        try { $value = [regex]::Unescape($match.Groups[1].Value) } catch { continue }
        if ($value.Length -le 900 -and $value -notmatch '(=>|;\s*(if|return|var)\b)') {
            [void]$values.Add($value)
        }
    }
}

$sources = @($values | Sort-Object)
$translations = [ordered]@{}
$marker = '<<<MDLOC_SPLIT_8E42C1>>>'

function Invoke-Translation([string[]]$items) {
    $query = $items -join "`n$marker`n"
    $uri = 'https://translate.googleapis.com/translate_a/single?client=gtx&sl=ru&tl=en&dt=t&q=' + [uri]::EscapeDataString($query)
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            $response = Invoke-RestMethod -Uri $uri -TimeoutSec 30
            $combined = (($response[0] | ForEach-Object { $_[0] }) -join '')
            $parts = @($combined -split [regex]::Escape($marker))
            if ($parts.Count -eq $items.Count) { return $parts }
        }
        catch {
            if ($attempt -eq 3) { throw }
            Start-Sleep -Milliseconds (300 * $attempt)
        }
    }
    throw "Translation response did not preserve the batch boundary."
}

$batch = [Collections.Generic.List[string]]::new()
$batchSources = [Collections.Generic.List[string]]::new()
$batchCharacters = 0

function Flush-Batch {
    if ($batch.Count -eq 0) { return }
    $translated = Invoke-Translation $batch.ToArray()
    for ($index = 0; $index -lt $batch.Count; $index++) {
        $original = $batchSources[$index]
        $leading = [regex]::Match($original, '^\s*').Value
        $trailing = [regex]::Match($original, '\s*$').Value
        $translations[$original] = $leading + $translated[$index].Trim() + $trailing
    }
    $batch.Clear()
    $batchSources.Clear()
    $script:batchCharacters = 0
}

foreach ($source in $sources) {
    $core = $source.Trim()
    if ($batch.Count -ge 20 -or $batchCharacters + $core.Length -gt 2400) { Flush-Batch }
    $batch.Add($core)
    $batchSources.Add($source)
    $batchCharacters += $core.Length
}
Flush-Batch

$target = Join-Path $appRoot 'Localization\English.json'
$json = $translations | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText($target, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Output "Wrote $($translations.Count) translations to $target"
