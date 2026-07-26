# MasterDocumentation UI screen inventory

Дата аудита: 26.07.2026  
Текущий стек: .NET 8, WPF, WebView2, TipTap 3

## Обозначения

- **Есть** — поверхность существует и доступна пользователю.
- **Частично** — сценарий реализован внутри другой поверхности или не имеет всех состояний.
- **Нет** — отдельной реализации в проекте нет. Такая поверхность не считается существующей и не должна имитироваться статическим макетом.
- **Legacy** — файл существует, но не участвует в текущей пользовательской навигации.

## Основные поверхности

| Поверхность | Статус | Реализация | Навигация и состояние |
|---|---|---|---|
| Главное окно | Есть | `Views/MainWindow.xaml` | WindowChrome, верхняя строка, библиотека, структура, редактор, свойства, status bar |
| Стартовый экран | Есть | `MainWindow.EmptyHint` | Создание, шаблон, импорт, недавние документы, быстрые действия |
| Редактор документа | Есть | `MainWindow` + `Editor/TiptapEditor` | TipTap является основным редактором; RichTextBox оставлен как fallback |
| Экран настроек | Есть | `Views/SettingsView.xaml` | Открывается внутри главного окна вместо структуры, редактора и правой панели |
| Старое окно настроек | Legacy | `Views/SettingsWindow.xaml` | Не вызывается из production-навигации; не удаляется без подтверждения |
| Все документы | Частично | Режим дерева `MainViewModel.ShowAll` | Отдельной страницы нет |
| Избранное | Частично | Режим дерева `ShowFavorites` | Отдельного empty state нет |
| Недавние | Частично | Режим дерева `ShowRecent` | Отдельного empty state нет |
| Корзина | Частично | Режим дерева `ShowTrash` | Restore/permanent delete доступны из context menu |
| Шаблоны | Частично | Режим дерева `ShowTemplates` | Шаблон создаётся из документа или через создание нового шаблона |
| Приложения | Есть | `Views/ApplicationsWindow.xaml` | Ножницы Windows, Paint, калькулятор, папка вложений |
| Поиск | Есть | `Views/SearchWindow.xaml` | FTS-поиск, фильтры статуса, тега, избранного, вложений и корзины |
| Command palette | Нет | — | Верхний поиск открывает только поиск документов |

## Документы и структура

| Сценарий | Статус | Реализация | Примечание |
|---|---|---|---|
| Создание документа | Есть | `Views/NewItemDialog.xaml` | Название, расположение, необязательный шаблон |
| Создание папки/раздела | Есть | `NewItemDialog` / `TextPrompt` | Вызывается из меню «Создать» и `+` у разделов |
| Переименование | Есть | `TextPrompt` | Вызывается из context menu и F2 |
| Перемещение | Частично | Drag-and-drop дерева | Отдельного окна нет; отсутствует полноценный drop indicator/error state |
| Дублирование | Есть | `MainWindow.Duplicate*` | Копируются теги, вложения и структурированное содержимое |
| Избранное | Есть | Context menu дерева | Состояние отражается только через данные дерева |
| Создание шаблона | Есть | Context menu / меню создания | Содержимое нового документа независимо от шаблона |
| Вкладки документов | Есть | `MainWindow.Tabs` | Открытие без дублей, Ctrl+W, Ctrl+Tab; нет drag reorder и overflow menu |
| Структура заголовков | Есть | `StructureTree`, `HeadersList`, `TocTree` | Строится из TipTap headings, поддерживает вложенность и переход |

## Свойства и вложения

| Поверхность | Статус | Реализация | Состояния |
|---|---|---|---|
| Компактные свойства | Есть | Правая панель `MainWindow` | Статус, теги, даты, автор |
| Расширенные свойства | Есть | `DocumentPropertiesWindow` | Пользовательские статусы и поля |
| Теги | Есть | `TextPrompt` | Chips отсутствуют |
| Вложения документа | Есть | Правая вкладка `AttachmentsPanel` | Карточка файла, открыть, сохранить как, удалить связь |
| PDF внутри документа | Частично | HTML-ссылка при импорте | Отдельного attachment block в редакторе нет |
| Отсутствующий файл | Частично | MessageBox при открытии | Нет inline error-card |
| Копирование/загрузка вложения | Нет | — | Операции синхронны, progress отсутствует |

## Операции

| Операция | Статус | Реализация | Текущее подтверждение |
|---|---|---|---|
| Автосохранение | Есть | debounce timer + аварийный draft | Status bar |
| Ручное сохранение | Есть | `ApplicationCommands.Save` / Ctrl+S | Status bar |
| Экспорт документа | Есть | PDF, DOCX, HTML, Markdown, TXT | Стандартный SaveFileDialog + MessageBox |
| Экспорт всего хранилища | Есть | `BackupService.Export` | OpenFolderDialog + MessageBox |
| Печать | Есть | WebView2 print / WPF PrintDialog | Системный PrintDialog |
| История версий | Есть | `HistoryWindow` | Просмотр, закрепление, удаление, восстановление |
| Резервное копирование | Есть | `BackupService` | Ручное/автоматическое, необязательное AES-256-GCM |
| Восстановление копии | Есть | `BackupService.Restore` | OpenFileDialog + MessageBox |
| Проверка базы | Есть | Настройки / footer | MessageBox |
| Перенос хранилища | Есть | `SettingsView.ChangeDataPath` | Копирование в фоне, финальный MessageBox |

## Окна и диалоги

| XAML | Тип | Назначение | Тема/состояния до переработки |
|---|---|---|---|
| `MainWindow.xaml` | Window | Основная оболочка | Частично кастомный WindowChrome |
| `ApplicationsWindow.xaml` | Window | Локальные инструменты | Нативная рамка, текстовые glyph-иконки |
| `ColorPickerDialog.xaml` | Window | HSV/RGB/HEX-палитра | Рабочая палитра, локальные HEX |
| `DocumentPickerDialog.xaml` | Window | Внутренняя ссылка | Нет empty/error state |
| `DocumentPropertiesWindow.xaml` | Window | Свойства и статусы | Нативная рамка, DataGrid без общей темы |
| `FirstRunWizard.xaml` | Window | Первый запуск | Нативная рамка, неполная light theme |
| `HistoryWindow.xaml` | Window | Версии документа | Нативная рамка, MessageBox подтверждения |
| `ListFormatDialog.xaml` | Window | Стиль списка | Нативная рамка |
| `NewItemDialog.xaml` | Window | Создание | Локально переопределяет базовые стили |
| `ParagraphFormatDialog.xaml` | Window | Параметры абзаца | Нативная рамка |
| `PasswordDialog.xaml` | Window | Пароль backup | Нативная рамка, inline validation отсутствует |
| `SearchWindow.xaml` | Window | Поиск | Нет command results/loading state |
| `SettingsWindow.xaml` | Window | Legacy-настройки | Не используется |
| `TextPrompt.xaml` | Window | Универсальный ввод | Уже использует WindowChrome |

## Popup, меню и подсказки

| Элемент | Статус | Реализация |
|---|---|---|
| Главное context menu | Есть | Кнопка меню приложения |
| Context menu дерева | Есть | Открыть, копия, ссылка, избранное, шаблон, rename, trash |
| Меню создания | Есть | Popup в левой панели |
| Меню `+` разделов | Есть | Popup «документ/папка» |
| Расширенное форматирование | Есть | Popup toolbar |
| Меню блоков | Есть | Popup toolbar |
| ComboBox popup | Есть | Общий шаблон в `App.xaml` |
| Тематический ToolTip | Нет | Используется стандартный WPF ToolTip |
| Toast / in-app notification | Нет | Успехи и ошибки показываются MessageBox/status text |

## Состояния данных

| Состояние | Статус до переработки |
|---|---|
| Нет документов | Стартовый экран существует, но дерево не имеет собственного empty state |
| Пустой раздел | Нет объяснения и действия внутри дерева |
| Нет избранного/недавних | Пустое дерево |
| Корзина пуста | Пустое дерево |
| Документ без заголовков | Пустая структура/оглавление |
| Нет вложений | Пустой список |
| Поиск без результатов | Только счётчик `Найдено: 0` |
| Загрузка большого документа | Нет отдельного loading state |
| Экспорт/backup в процессе | Нет локального loading state |
| Ошибка автосохранения | Текст в status bar без действия «Повторить» |
| Недоступное хранилище | Системный MessageBox до инициализации главного UI |

## Keyboard и accessibility baseline

Реализованы: Ctrl+N, Ctrl+Shift+N, Ctrl+S, Ctrl+Shift+S, Ctrl+K, Ctrl+O,
Ctrl+P, Ctrl+W, Ctrl+Tab, Ctrl+Shift+Tab, Ctrl+B/I/U, Ctrl+Z/Y, F2, Delete,
F11 и поиск внутри документа через `ApplicationCommands.Find`.

Отсутствуют или не завершены: Ctrl+1…9, Shift+F10 как явная команда, F6 между
областями, focus mode, accessible names у большинства icon-only controls,
логичный Tab-order для всей оболочки, screen-reader live region и reduced motion.

## Проверки baseline

- `dotnet build MasterDocumentation.sln -c Debug`: 0 ошибок, 0 предупреждений.
- `dotnet test MasterDocumentation.sln -c Debug --no-build`: 28/28 тестов.
- UI-тестового проекта нет.
- Сохранены текущие скриншоты 1536×900, 1180×720, вкладок и редактора в `artifacts/`.
