<#
.SYNOPSIS
Создаёт самоподписанный сертификат подписи кода для проверки цепочки сборки.

.DESCRIPTION
Нужен, чтобы проверить, что подпись встраивается в EXE и установщик, и чтобы раздать
доверие внутри своей организации (сертификат ставится на компьютеры групповой политикой).

ВАЖНО: самоподписанный сертификат НЕ убирает предупреждение SmartScreen у сторонних
пользователей. Windows доверяет только сертификатам доверенных поставщиков, а Smart App
Control — только RSA-сертификатам от них же. Для публичных выпусков нужен сертификат
Trusted Signing (Microsoft) либо OV/EV от удостоверяющего центра — см. docs/CODE_SIGNING.md.

.EXAMPLE
.\scripts\new-signing-certificate.ps1 -Subject 'CN=Максим Симан' -OutputPath .\artifacts\test-signing.pfx -Password 'пароль'
#>
param(
    [Parameter(Mandatory = $true)][string]$Subject,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [Parameter(Mandatory = $true)][string]$Password,
    [int]$Years = 3,
    # Ставит сертификат в доверенные корневые центры и доверенные издатели этого компьютера:
    # только для своих машин и только осознанно — это меняет доверие всей системы.
    [switch]$InstallToTrustedRoot
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') { throw 'Скрипт работает только в Windows.' }

# RSA обязателен: Smart App Control не принимает подписи на эллиптических кривых.
$certificate = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -KeyAlgorithm RSA `
    -KeyLength 3072 `
    -HashAlgorithm SHA256 `
    -KeyExportPolicy Exportable `
    -KeyUsage DigitalSignature `
    -CertStoreLocation Cert:\CurrentUser\My `
    -NotAfter (Get-Date).AddYears($Years)

$secure = ConvertTo-SecureString -String $Password -Force -AsPlainText
$directory = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
if ($directory -and -not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
Export-PfxCertificate -Cert $certificate -FilePath $OutputPath -Password $secure | Out-Null
$publicPath = [IO.Path]::ChangeExtension($OutputPath, '.cer')
Export-Certificate -Cert $certificate -FilePath $publicPath | Out-Null

if ($InstallToTrustedRoot) {
    Import-Certificate -FilePath $publicPath -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
    Import-Certificate -FilePath $publicPath -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
    Write-Host 'Сертификат добавлен в доверенные корневые центры и доверенные издатели этого компьютера.'
}

Write-Host "Отпечаток: $($certificate.Thumbprint)"
Write-Host "PFX: $OutputPath"
Write-Host "Открытая часть (для раздачи на компьютеры): $publicPath"
Write-Host ''
Write-Host 'Сборка подписанного выпуска:'
Write-Host "  .\scripts\build-release.ps1 -Version 1.5.0 -CertificatePath '$OutputPath' -CertificatePassword '<пароль>'"
Write-Host ''
Write-Host 'Удалить сертификат из личного хранилища:'
Write-Host "  Remove-Item Cert:\CurrentUser\My\$($certificate.Thumbprint)"
