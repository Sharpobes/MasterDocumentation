param(
    [Parameter(Mandatory = $false)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version = '1.0.0',
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    # Подпись кода: без неё SmartScreen показывает «Система Windows защитила ваш компьютер»,
    # а Smart App Control блокирует запуск. Подписывать нужно сертификатом RSA (ECC не
    # поддерживается) от доверенного поставщика — см. docs/CODE_SIGNING.md.
    # Способ 1: PFX-файл или отпечаток сертификата, уже установленного в хранилище.
    [string]$CertificatePath,
    [string]$CertificatePassword,
    [string]$CertificateThumbprint,
    # Способ 2 (рекомендован Microsoft): Trusted Signing, бывш. Azure Code Signing.
    [string]$TrustedSigningEndpoint,
    [string]$TrustedSigningAccount,
    [string]$TrustedSigningProfile,
    # Служба меток времени RFC 3161: без метки подпись «протухает» вместе с сертификатом.
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    # Прервать сборку, если подписать не удалось (для выпусков: неподписанный релиз бесполезен).
    [switch]$RequireSignature
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
# Параметры Trusted Signing тоже можно передать переменными окружения (секретами сборки).
if (-not $TrustedSigningEndpoint) { $TrustedSigningEndpoint = $env:TRUSTED_SIGNING_ENDPOINT }
if (-not $TrustedSigningAccount) { $TrustedSigningAccount = $env:TRUSTED_SIGNING_ACCOUNT }
if (-not $TrustedSigningProfile) { $TrustedSigningProfile = $env:TRUSTED_SIGNING_PROFILE }

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

# Способ подписи: Trusted Signing (рекомендован Microsoft), обычный сертификат или ничего.
function Get-SigningMode {
    if ($TrustedSigningEndpoint -and $TrustedSigningAccount -and $TrustedSigningProfile) { return 'trusted-signing' }
    if ($CertificatePath -or $CertificateThumbprint) { return 'certificate' }
    return 'none'
}

# signtool.exe входит в Windows SDK и не лежит в PATH: ищем самую свежую версию.
function Get-SignTool {
    if ($script:signTool) { return $script:signTool }
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { $script:signTool = $command.Source; return $script:signTool }
    $roots = @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "$env:ProgramFiles\Windows Kits\10\bin")
    $candidates = foreach ($root in $roots) {
        if (Test-Path -LiteralPath $root) { Get-ChildItem -LiteralPath $root -Filter 'signtool.exe' -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match '\\x64\\' } }
    }
    $script:signTool = ($candidates | Sort-Object -Property FullName -Descending | Select-Object -First 1).FullName
    return $script:signTool
}

# Клиент Trusted Signing — глобальный dotnet-инструмент `sign` (dotnet/sign).
function Get-SignCli {
    if ($script:signCli) { return $script:signCli }
    $command = Get-Command sign.exe -ErrorAction SilentlyContinue
    if (-not $command) {
        Write-Host 'Устанавливается dotnet-инструмент sign для Trusted Signing…'
        dotnet tool install --global sign --prerelease | Out-Host
        $env:PATH = "$env:PATH;$env:USERPROFILE\.dotnet\tools"
        $command = Get-Command sign.exe -ErrorAction SilentlyContinue
    }
    if (-not $command) { throw 'Не удалось получить инструмент sign для Trusted Signing.' }
    $script:signCli = $command.Source
    return $script:signCli
}

<#
.SYNOPSIS
Подписывает файлы Authenticode-подписью SHA-256 с меткой времени RFC 3161.

.DESCRIPTION
Подписывается EXE и наши сборки, а не архив: ZIP подписать нельзя, поэтому портативная версия
получает подписанные файлы внутри. Установщик подписывается уже вместе с дистрибутивом в конце
файла — метка footer'а ищется поиском с конца и переживает таблицу сертификатов.
Smart App Control учитывает только сертификаты RSA от доверенных поставщиков, поэтому SHA-1 и
ECC здесь не используются.
#>
function Invoke-CodeSign([string[]]$Paths) {
    $files = @($Paths | Where-Object { $_ -and (Test-Path -LiteralPath $_) })
    if ($files.Count -eq 0) { return }
    switch (Get-SigningMode) {
        'trusted-signing' {
            $cli = Get-SignCli
            & $cli code trusted-signing @files `
                --trusted-signing-endpoint $TrustedSigningEndpoint `
                --trusted-signing-account $TrustedSigningAccount `
                --trusted-signing-certificate-profile $TrustedSigningProfile `
                --timestamp-url $TimestampUrl --file-digest SHA256 --timestamp-digest SHA256 | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "Trusted Signing завершился с кодом $LASTEXITCODE." }
        }
        'certificate' {
            $tool = Get-SignTool
            if ($tool) {
                # signtool sign /fd SHA256 /tr <RFC3161> /td SHA256 — рекомендованный набор ключей.
                $arguments = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256', '/v')
                if ($CertificatePath) {
                    $arguments += @('/f', $CertificatePath)
                    if ($CertificatePassword) { $arguments += @('/p', $CertificatePassword) }
                }
                else { $arguments += @('/sha1', $CertificateThumbprint) }
                & $tool @arguments @files | Out-Host
                if ($LASTEXITCODE -ne 0) { throw "signtool завершился с кодом $LASTEXITCODE." }
            }
            else {
                # Windows SDK не установлен: подписываем средствами PowerShell.
                Write-Warning 'signtool.exe не найден, используется Set-AuthenticodeSignature.'
                $certificate = Get-SigningCertificate
                foreach ($file in $files) {
                    $result = Set-AuthenticodeSignature -FilePath $file -Certificate $certificate -HashAlgorithm SHA256 -TimestampServer $TimestampUrl
                    Assert-SignatureStatus $file $result
                }
            }
        }
        default {
            $message = 'Сертификат подписи не задан: файлы остаются неподписанными, Windows покажет предупреждение SmartScreen. См. docs/CODE_SIGNING.md.'
            if ($RequireSignature) { throw $message }
            Write-Warning $message
            return
        }
    }
    foreach ($file in $files) { Write-Host "Подписан: $file" }
    Test-Signature $files
}

# Состояние подписи. NotTrusted/UnknownError означает, что файл подписан, но цепочка сертификата
# не доверена на этой машине — так всегда бывает с самоподписанным сертификатом. Это не ошибка
# сборки: доверие проверяется там, где файл запускают.
function Assert-SignatureStatus([string]$Path, $Signature) {
    switch ($Signature.Status) {
        'Valid' { return }
        { $_ -in 'NotTrusted', 'UnknownError' } {
            Write-Warning "Файл $Path подписан, но сертификат не доверен на этой машине ($($Signature.StatusMessage.Trim())). Для публичных выпусков нужен сертификат от доверенного поставщика — см. docs/CODE_SIGNING.md."
            return
        }
        default { throw "Подпись файла $Path недействительна: $($Signature.Status) $($Signature.StatusMessage)" }
    }
}

# Проверка подписи — та же, что делает Windows при запуске файла.
function Test-Signature([string[]]$Paths) {
    foreach ($file in $Paths) {
        $signature = Get-AuthenticodeSignature -LiteralPath $file
        Assert-SignatureStatus $file $signature
        if (-not $signature.SignerCertificate) { throw "Файл $file остался без подписи." }
        if ($signature.SignerCertificate.PublicKey.Oid.FriendlyName -ne 'RSA') {
            throw "Файл $file подписан не RSA-сертификатом: Smart App Control такие подписи не принимает."
        }
        if (-not $signature.TimeStamperCertificate) { Write-Warning "Файл $file подписан без метки времени: подпись перестанет действовать вместе с сертификатом." }
    }
}

# Наши собственные сборки: подписываются вместе с EXE, файлы среды выполнения .NET и WebView2
# уже подписаны Microsoft.
function Get-OwnBinaries([string]$Folder) {
    # Фильтр через Where-Object, а не -Include: с -LiteralPath -Include не применяется и в список
    # попадали все файлы подряд, включая README и INSTALLATION.md — подписать их нельзя.
    return @(Get-ChildItem -LiteralPath $Folder -Recurse -File |
        Where-Object { $_.Name -like 'MasterDocumentation*' -and ($_.Extension -eq '.exe' -or $_.Extension -eq '.dll') } |
        Select-Object -ExpandProperty FullName)
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

# В корне остаётся только MasterDocumentation.exe рядом с папкой Runtime: отладочные символы,
# XML-документация пакетов и загрузчики WebView2 для чужих архитектур пользователю не нужны —
# нужный WebView2Loader.dll упакован в сам EXE вместе с остальным машинным кодом.
Get-ChildItem -LiteralPath $publish -Filter '*.pdb' -File | Remove-Item -Force
Get-ChildItem -LiteralPath $publish -Filter '*.xml' -File | Remove-Item -Force
$loaders = Join-Path $publish 'runtimes'
if (Test-Path -LiteralPath $loaders) { Remove-Item -LiteralPath $loaders -Recurse -Force }
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'docs\INSTALLATION.md') -Destination (Join-Path $publish 'INSTALLATION.md')

if (-not (Test-Path -LiteralPath (Join-Path $publish 'MasterDocumentation.exe'))) { throw 'MasterDocumentation.exe was not published.' }
if (-not (Test-Path -LiteralPath (Join-Path $publish 'Runtime\Editor\index.html'))) { throw 'Local TipTap editor is missing from publish output.' }
if (-not (Get-ChildItem -LiteralPath (Join-Path $publish 'Runtime\WebView2') -Filter 'msedgewebview2.exe' -Recurse -ErrorAction SilentlyContinue)) { throw 'Fixed WebView2 Runtime is missing from publish output.' }

Invoke-CodeSign (Get-OwnBinaries $publish)

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

    Invoke-CodeSign @($installer)
    $installerHash = Write-Sha256 $installer
    Remove-Item -LiteralPath $setupStub -Recurse -Force
}

Write-Host "Portable archive: $archive"
Write-Host "SHA-256: $archiveHash"
if (-not $SkipInstaller) {
    Write-Host "Installer: $installer"
    Write-Host "SHA-256: $installerHash"
}
