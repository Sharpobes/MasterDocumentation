# MasterDocumentation

Полностью локальное portable-приложение для создания и ведения технической документации на Windows. Учётная запись, сервер, облачная синхронизация и интернет для работы не нужны.

## MasterDocumentation V 1.0

**[Скачать MasterDocumentation v1.0.0 — portable ZIP](https://github.com/Sharpobes/MasterDocumentation/releases/download/v1.0.0/MasterDocumentation-v1.0.0-win-x64.zip)**

[Выбрать версию](docs/RELEASES.md) · [GitHub Releases](https://github.com/Sharpobes/MasterDocumentation/releases) · [Подробная установка](docs/INSTALLATION.md) · [История изменений](CHANGELOG.md)

> Ссылка на ZIP начнёт работать после публикации тега `v1.0.0`. Каждый следующий тег `v*` автоматически создаёт отдельный GitHub Release со своим portable-архивом и SHA-256.

## Основные возможности

- разделы, папки, подпапки и документы с drag-and-drop, уникальными названиями, избранным, недавними файлами и корзиной;
- несколько вкладок, восстановление сессии, автосохранение и аварийные черновики;
- TipTap/ProseMirror JSON как основной формат документа, HTML и plain text для экспорта и поиска;
- заголовки H1–H6, живая структура и оглавление с переходом к разделу;
- шрифты, размеры, цвета, интервалы, отступы, выравнивание, регистр, формат по образцу и LTR/RTL;
- списки, чек-листы, цитаты, спойлеры, ссылки, таблицы и блоки кода с локальной подсветкой;
- KaTeX-формулы, Mermaid-диаграммы, callout-блоки, сворачиваемые секции, якоря и безопасный HTML;
- изображения из файла, буфера обмена и drag-and-drop: размер, поворот, подпись, alt-текст, обтекание, обрезка и сжатие;
- вложения с SHA-256-дедупликацией: одинаковый файл не сохраняется на диске повторно;
- SQLite FTS5-поиск по тексту, названию, тегам, статусам, папкам и вложениям;
- встроенные и пользовательские статусы, теги и пользовательские свойства документа;
- шаблоны с переменными `{{Title}}`, `{{Date}}`, `{{Time}}`, `{{Author}}`, `{{Section}}` и своими полями;
- импорт TXT, Markdown, HTML, RTF, DOCX; PDF добавляется как вложение;
- экспорт PDF, DOCX, HTML, Markdown, TXT и системная печать;
- локальные версии документа, сравнение, закрепление и восстановление;
- ручные и автоматические `.mdbackup`, контрольные суммы и AES-256-GCM;
- тёмная, светлая и системная темы;
- перенос папки данных с проверкой целостности SQLite.

## Быстрая установка

1. Откройте раздел [Releases](https://github.com/Sharpobes/MasterDocumentation/releases).
2. Выберите нужную версию, например `v1.0.0`.
3. Скачайте `MasterDocumentation-v1.0.0-win-x64.zip` из блока **Assets**.
4. Распакуйте архив полностью в обычную локальную папку, например `D:\Apps\MasterDocumentation`.
5. Запустите `MasterDocumentation.exe` и пройдите короткий мастер первого запуска.

Не запускайте EXE прямо внутри ZIP. Приложение portable: установщик и права администратора не требуются. В архив уже входят .NET 8 и фиксированный WebView2 Runtime, поэтому основная работа не зависит от установленного браузера и доступа к интернету.

Подробности об обновлении, переносе данных, SmartScreen и проверке SHA-256 находятся в [инструкции по установке](docs/INSTALLATION.md).

## Где хранятся данные

По умолчанию рядом с приложением создаётся папка:

```text
Data/
  master-documentation.db
  settings.json
  Assets/
  Backups/
  Exports/
  Logs/
  Temp/Drafts/
```

Папку можно изменить в `Настройки → Хранение`. Выбранный путь хранится в локальном `data-location.txt`. Для обновления приложения достаточно заменить файлы программы, не удаляя `Data` и `data-location.txt`.

## Структура исходного кода

Каждый исходный файл физически находится только в одном проекте:

```text
src/
  MasterDocumentation.App/             WPF, окна и ViewModel
  MasterDocumentation.Core/            модели предметной области
  MasterDocumentation.Infrastructure/  SQLite, настройки, файлы и журналы
  MasterDocumentation.Editor/          WebView2 и локальный TipTap
  MasterDocumentation.Export/          PDF, DOCX, HTML, Markdown и TXT
  MasterDocumentation.Backup/          создание и восстановление копий
tests/
  MasterDocumentation.Tests/
```

`src/MasterDocumentation.Editor/web/node_modules` в Git не добавляется: это восстанавливаемый npm-кэш. В репозиторий входят `web/src`, `package.json`, `package-lock.json` и `web/dist`, который приложение копирует в portable-сборку.

## Сборка из исходников

Потребуются Windows x64, .NET 8 SDK и Node.js 20 или новее:

```powershell
cd src\MasterDocumentation.Editor\web
npm ci
npm run build
cd ..\..\..
dotnet restore MasterDocumentation.sln
dotnet build MasterDocumentation.sln -c Release --no-restore
dotnet test MasterDocumentation.sln -c Release --no-build
```

Готовый релизный ZIP:

```powershell
.\scripts\build-release.ps1 -Version 1.0.0
```

Результат появится в `artifacts/`. GitHub Actions выполняет те же шаги при отправке тега `v*`.

## Выпуск новой версии

```powershell
git tag -a v1.0.0 -m "MasterDocumentation v1.0.0"
git push origin v1.0.0
```

Workflow соберёт редактор, выполнит тесты, создаст self-contained portable-папку, упакует её в ZIP, рассчитает SHA-256 и прикрепит оба файла к GitHub Release. Перед выпуском следующей версии обновите номер в проекте, `CHANGELOG.md` и таблицу в `docs/RELEASES.md`.

## Системные требования

- Windows 10 1709 или новее / Windows 11;
- процессор x64;
- около 600 МБ свободного места для распакованной portable-версии;
- права на запись в выбранную папку данных.

## Лицензия

[MIT](LICENSE)
