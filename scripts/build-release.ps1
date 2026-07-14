param(
    [Parameter(Mandatory = $false)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version = '1.0.0',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\MasterDocumentation.App\MasterDocumentation.App.csproj'
$editor = Join-Path $root 'src\MasterDocumentation.Editor\web'
$artifacts = Join-Path $root 'artifacts'
$folderName = "MasterDocumentation-v$Version-win-x64"
$publish = Join-Path $artifacts $folderName
$archive = Join-Path $artifacts "$folderName.zip"

function Assert-ArtifactPath([string]$Path) {
    $artifactRoot = [IO.Path]::GetFullPath($artifacts).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $candidate = [IO.Path]::GetFullPath($Path)
    if (-not $candidate.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the artifacts directory: $candidate"
    }
}

Assert-ArtifactPath $publish
Assert-ArtifactPath $archive

Push-Location $editor
try {
    npm ci
    npm run build
}
finally { Pop-Location }

dotnet restore (Join-Path $root 'MasterDocumentation.sln')
dotnet build (Join-Path $root 'MasterDocumentation.sln') -c Release --no-restore -p:Version=$Version
if (-not $SkipTests) {
    dotnet test (Join-Path $root 'MasterDocumentation.sln') -c Release --no-build -p:Version=$Version
}

if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publish | Out-Null
dotnet publish $project -c Release -r win-x64 --self-contained true --no-build -p:Version=$Version -o $publish

Get-ChildItem -LiteralPath $publish -Filter '*.pdb' -File | Remove-Item -Force
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'docs\INSTALLATION.md') -Destination (Join-Path $publish 'INSTALLATION.md')

if (-not (Test-Path -LiteralPath (Join-Path $publish 'MasterDocumentation.exe'))) { throw 'MasterDocumentation.exe was not published.' }
if (-not (Test-Path -LiteralPath (Join-Path $publish 'Editor\index.html'))) { throw 'Local TipTap editor is missing from publish output.' }
if (-not (Get-ChildItem -LiteralPath (Join-Path $publish 'WebView2') -Filter 'msedgewebview2.exe' -Recurse -ErrorAction SilentlyContinue)) { throw 'Fixed WebView2 Runtime is missing from publish output.' }

if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
Compress-Archive -Path $publish -DestinationPath $archive -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($archive + '.sha256') -Value "$hash  $([IO.Path]::GetFileName($archive))" -Encoding ascii

Write-Host "Release archive: $archive"
Write-Host "SHA-256: $hash"
