param(
    [Parameter(Mandatory = $false)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version = '1.0.0',
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    # Подпись кода: без неё SmartScreen показывает «Система Windows защитила ваш компьютер».
    # Сертификат задаётся либо PFX-файлом, либо отпечатком уже установленного в хранилище.
    [string]$CertificatePath,
    [string]$CertificatePassword,
    [string]$CertificateThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
# В Windows PowerShell сборки со сжатием нужно загрузить явно, в PowerShell 7 Add-Type для них
# завершается ошибкой, а сама сборка подгружается по имени средой выполнения. Пробуем оба
# способа и прерываем сборку, только если тип так и не появился.
foreach ($assembly in 'System.IO.Compression', 'System.IO.Compression.FileSystem') {
    try { Add-Type -AssemblyName $assembly -ErrorAction Stop } catch { }
}
if (-not ('System.IO.Compression.ZipFile' -as [type])) {
    foreach ($assembly in 'System.IO.Compression.ZipFile', 'System.IO.Compression.FileSystem') {
        try { [void][Reflection.Assembly]::Load($assembly) } catch { }
    }
}
if (-not ('System.IO.Compression.ZipFile' -as [type])) { throw 'System.IO.Compression.ZipFile is not available in this PowerShell host.' }

# Сертификат подписи в сборке приходит из секрета в виде base64.
if (-not $CertificatePath -and -not $CertificateThumbprint -and $env:SIGNING_CERTIFICATE) {
    $CertificatePath = Join-Path ([IO.Path]::GetTempPath()) 'masterdocumentation-signing.pfx'
    [IO.File]::WriteAllBytes($CertificatePath, [Convert]::FromBase64String($env:SIGNING_CERTIFICATE))
    if (-not $CertificatePassword) { $CertificatePassword = $env:SIGNING_PASSWORD }
}

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\MasterDocumentation.App\MasterDocumentation.App.csproj'
$setupProject = Join-Path $root 'src\MasterDocumentation.Setup\MasterDocumentation.Setup.csproj'
$editor = Join-Path $root 'src\MasterDocumentation.Editor\web'
$artifacts = Join-Path $root 'artifacts'
$folderName = "MasterDocumentation-v$Version-win-x64"
$publish = Join-Path $artifacts $folderName
$archive = Join-Path $artifacts "$folderName.zip"
$setupStub = Join-Path $artifacts 'setup-stub'
$installer = Join-Path $artifacts "MasterDocumentation-Setup-v$Version.exe"

function Assert-ArtifactPath([string]$Path) {
    $artifactRoot = [IO.Path]::GetFullPath($artifacts).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $candidate = [IO.Path]::GetFullPath($Path)
    if (-not $candidate.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the artifacts directory: $candidate"
    }
}

function Get-SigningCertificate {
    if ($script:signingCertificate -ne $null) { return $script:signingCertificate }
    if ($CertificatePath) {
        if (-not (Test-Path -LiteralPath $CertificatePath)) { throw "Certificate file was not found: $CertificatePath" }
        $script:signingCertificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2 @($CertificatePath, $CertificatePassword, 'Exportable,PersistKeySet')
    }
    elseif ($CertificateThumbprint) {
        $found = Get-ChildItem -Path Cert:\CurrentUser\My, Cert:\LocalMachine\My | Where-Object { $_.Thumbprint -eq $CertificateThumbprint }
        if (-not $found) { throw "Certificate with thumbprint $CertificateThumbprint was not found." }
        $script:signingCertificate = $found[0]
    }
    return $script:signingCertificate
}

# Подписывается EXE, а не архив: ZIP подписать нельзя, поэтому портативная сборка получает
# подписанный MasterDocumentation.exe внутри. Установщик подписывается уже вместе с дистрибутивом
# в конце файла — метка footer'а ищется поиском с конца и переживает таблицу сертификатов.
function Invoke-CodeSign([string]$Path) {
    $certificate = Get-SigningCertificate
    if (-not $certificate) { return }
    $result = Set-AuthenticodeSignature -FilePath $Path -Certificate $certificate -HashAlgorithm SHA256 -TimestampServer $TimestampUrl
    if ($result.Status -ne 'Valid') { throw "Signing failed for $Path : $($result.StatusMessage)" }
    Write-Host "Signed: $Path"
}

function Write-Sha256([string]$Path) {
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath ($Path + '.sha256') -Value "$hash  $([IO.Path]::GetFileName($Path))" -Encoding ascii
    return $hash
}

Assert-ArtifactPath $publish
Assert-ArtifactPath $archive
Assert-ArtifactPath $setupStub
Assert-ArtifactPath $installer

Push-Location $editor
try {
    npm ci
    npm run build
}
finally { Pop-Location }

dotnet restore (Join-Path $root 'MasterDocumentation.sln')
dotnet build (Join-Path $root 'MasterDocumentation.sln') -c Release --no-restore -p:Version=$Version
if (-not $SkipTests) {
    dotnet test (Join-Path $root 'tests\MasterDocumentation.Tests\MasterDocumentation.Tests.csproj') -c Release --no-build -p:Version=$Version
}

# --- Портативная сборка ------------------------------------------------------
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

Invoke-CodeSign (Join-Path $publish 'MasterDocumentation.exe')

if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
# Архив собирается вручную, а не через Compress-Archive/CreateFromDirectory: в Windows PowerShell
# CreateFromDirectory записывает имена записей с обратными слэшами, из-за чего архив некорректен
# для распаковщиков, следующих спецификации ZIP. Здесь имена всегда с прямыми слэшами.
$publishFull = [IO.Path]::GetFullPath($publish).TrimEnd([IO.Path]::DirectorySeparatorChar)
$zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -LiteralPath $publish -Recurse -File) {
        $relative = $file.FullName.Substring($publishFull.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, "$folderName/$relative", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally { $zip.Dispose() }
$archiveHash = Write-Sha256 $archive

# --- Установщик с графическим интерфейсом ------------------------------------
if (-not $SkipInstaller) {
    if (Test-Path -LiteralPath $setupStub) { Remove-Item -LiteralPath $setupStub -Recurse -Force }
    dotnet publish $setupProject -c Release -r win-x64 --self-contained true -p:Version=$Version -p:PublishSingleFile=true -o $setupStub
    $stub = Join-Path $setupStub 'MasterDocumentation-Setup.exe'
    if (-not (Test-Path -LiteralPath $stub)) { throw 'Setup stub was not published.' }

    if (Test-Path -LiteralPath $installer) { Remove-Item -LiteralPath $installer -Force }
    Copy-Item -LiteralPath $stub -Destination $installer

    # Дистрибутив дописывается в конец EXE: [ZIP][длина Int64][сигнатура MDSETUP1].
    $payloadLength = (Get-Item -LiteralPath $archive).Length
    $output = [IO.File]::Open($installer, [IO.FileMode]::Append, [IO.FileAccess]::Write)
    try {
        $input = [IO.File]::OpenRead($archive)
        try { $input.CopyTo($output) } finally { $input.Dispose() }
        $output.Write([BitConverter]::GetBytes([int64]$payloadLength), 0, 8)
        $output.Write([Text.Encoding]::ASCII.GetBytes('MDSETUP1'), 0, 8)
    }
    finally { $output.Dispose() }

    Invoke-CodeSign $installer
    $installerHash = Write-Sha256 $installer
    Remove-Item -LiteralPath $setupStub -Recurse -Force
}

Write-Host "Portable archive: $archive"
Write-Host "SHA-256: $archiveHash"
if (-not $SkipInstaller) {
    Write-Host "Installer: $installer"
    Write-Host "SHA-256: $installerHash"
}
