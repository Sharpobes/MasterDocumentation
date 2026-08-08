# Подпись кода: доверие Windows при установке

Неподписанные файлы Windows встречает окном «Система Windows защитила ваш компьютер»
(SmartScreen), а на устройствах с включённым Smart App Control просто не запускает. Убрать это
предупреждение можно только подписью кода — никакие настройки установщика, манифеста или архива
на это не влияют.

Требования Microsoft (см. [Code signing for Smart App Control](https://learn.microsoft.com/ru-ru/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control)):

- сертификат должен быть **RSA**: подписи на эллиптических кривых (ECC) Smart App Control не принимает;
- сертификат должен быть выдан **доверенным поставщиком**. Самоподписанный сертификат подходит
  только для внутреннего использования — публичные пользователи ему не доверяют;
- подпись делается `signtool.exe` из Windows SDK: `sign /fd SHA256 /tr <RFC3161> /td SHA256`
  ([инструкция Microsoft](https://learn.microsoft.com/ru-ru/windows/win32/appxpkg/how-to-sign-a-package-using-signtool)).

## Какой сертификат брать

| Вариант | Что даёт | Что нужно |
|---|---|---|
| **Trusted Signing** (бывш. Azure Code Signing) — рекомендация Microsoft | Подпись без своего HSM, ключи хранит Microsoft; репутация SmartScreen наследуется от подписи | Подписка Azure, проверенная организация или ИП, ~$10/мес |
| **EV-сертификат** удостоверяющего центра | Репутация SmartScreen сразу, без накопления загрузок | Проверка организации, аппаратный токен, ~$250–400/год |
| **OV-сертификат** (обычный code signing) | Имя издателя в окне UAC; предупреждение SmartScreen уходит после накопления репутации | Проверка организации, ~$100–200/год |
| **Самоподписанный** | Только для своих машин и тестирования сборки | Ничего, но доверие надо раздать вручную |

Репутация SmartScreen привязана к сертификату: после смены сертификата она копится заново.
Поэтому для публичных выпусков лучше Trusted Signing или EV.

## Как собрать подписанный выпуск

Trusted Signing (нужен вход в Azure — `az login` или переменные окружения службы):

```powershell
.\scripts\build-release.ps1 -Version 1.5.0 `
  -TrustedSigningEndpoint https://eus.codesigning.azure.net `
  -TrustedSigningAccount <имя-аккаунта> `
  -TrustedSigningProfile <имя-профиля> `
  -RequireSignature
```

PFX-файл от удостоверяющего центра:

```powershell
.\scripts\build-release.ps1 -Version 1.5.0 -CertificatePath .\signing.pfx -CertificatePassword '<пароль>' -RequireSignature
```

Сертификат на аппаратном токене (EV) — по отпечатку из хранилища Windows:

```powershell
.\scripts\build-release.ps1 -Version 1.5.0 -CertificateThumbprint <отпечаток> -RequireSignature
```

Подписываются `MasterDocumentation.exe`, наши библиотеки внутри портативной сборки и установщик
(уже вместе с вшитым дистрибутивом). Ключ `-RequireSignature` прерывает сборку, если подписать не
удалось: выпуск без подписи публиковать бессмысленно. Сам ZIP не подписывается — формат этого не
поддерживает, поэтому подписаны файлы внутри него.

## Сборка на GitHub Actions

Секреты репозитория (настраиваются в Settings → Secrets and variables → Actions):

- `SIGNING_CERTIFICATE` — PFX в base64 (`[Convert]::ToBase64String([IO.File]::ReadAllBytes('signing.pfx'))`), `SIGNING_PASSWORD` — пароль к нему;
- либо `TRUSTED_SIGNING_ENDPOINT`, `TRUSTED_SIGNING_ACCOUNT`, `TRUSTED_SIGNING_PROFILE` вместе с
  учётными данными Azure (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_CLIENT_SECRET`).

Без секретов сборка проходит, но файлы остаются неподписанными.

## Проверка перед публикацией

```powershell
Get-AuthenticodeSignature .\artifacts\MasterDocumentation-Setup-v1.5.0.exe | Format-List Status, SignerCertificate, TimeStamperCertificate
```

`Status` должен быть `Valid`, у сертификата — алгоритм RSA и непустая метка времени. То же самое
делает `signtool verify /pa /v <файл>`.

## Самоподписанный сертификат для проверки и для своей сети

```powershell
.\scripts\new-signing-certificate.ps1 -Subject 'CN=Название организации' -OutputPath .\artifacts\test-signing.pfx -Password '<пароль>'
```

Скрипт создаёт RSA-3072 сертификат подписи кода, экспортирует `.pfx` (для подписи) и `.cer` (для
раздачи доверия). Предупреждение исчезнет только на тех компьютерах, где `.cer` установлен в
«Доверенные корневые центры сертификации» и «Доверенные издатели» — вручную, групповой политикой
или ключом `-InstallToTrustedRoot` (меняет доверие всей системы, применяйте осознанно).

Для проверки локально собранных файлов помните: у файла, собранного на этой же машине, нет метки
Mark-of-the-Web, и SmartScreen для него не срабатывает в принципе. Чтобы увидеть настоящее
поведение, скачайте файл из интернета (например, из выпуска на GitHub).
