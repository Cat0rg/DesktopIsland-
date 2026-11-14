# Desktop Island (Windows 10 / 11)

Нативное лёгкое приложение в стиле Dynamic Island для Windows, написанное на **C# + WPF (.NET 8)**.

## Расположение и структура
- `E:\Applications\DesktopIsland\App\DesktopIsland.exe` — собранное исполняемое приложение.
- `E:\Applications\DesktopIsland\Source\` — полный исходный код проекта (`DesktopIsland.csproj`, `MainWindow`, `Views`, `Services`, `Models`).
- `E:\Applications\DesktopIsland\Assets\` — иконка приложения (`Icons\app.ico`) и логотип (`Images\logo.png`).
- `E:\Applications\DesktopIsland\Config\settings.json` — сохранённые пользовательские настройки.
- `E:\Applications\DesktopIsland\Logs\` — ежедневный журнал ошибок и событий.

## Реализованные функции
1. **Дизайн и поведение капсулы:**
   - Чёрная скруглённая капсула по центру верхней части экрана.
   - Компактный режим (время, дата, CPU/RAM, сеть) и плавное раскрытие при наведении мыши (либо закрепление кнопкой 📌).
   - Размер раскрытия зависит от выбранной вкладки; не перекрывает экран и не появляется в `Alt+Tab`.
   - Поддержка Per-Monitor DPI v2, масштабирования Windows и выбора монитора.
2. **Быстрый запуск:** закрепление программ, добавление/удаление/сортировка, извлечение иконок, запуск в 1 клик.
3. **Системный и сетевой монитор:** загрузка CPU (`GetSystemTimes`), использованная/свободная RAM (`GlobalMemoryStatusEx`), скорость сети (↓/↑), опрос с низким потреблением ресурсов.
4. **Монитор дисков:** состояние дисков `C:`, `D:`, `E:` (занято / свободно / всего).
5. **Буфер обмена:** прослушивание через Win32 `AddClipboardFormatListener` (0% нагрузки в простое), история, повторное копирование кликом, очистка, лимит и отключение.
6. **Безопасный модуль очистки:** анализ и удаление временных файлов `%TEMP%`, `C:\Windows\Temp`, кэша миниатюр, отчётов WER. Спорные категории (Корзина) требуют подтверждения. Защищены: пароли, cookies, данные браузеров, npm/Gradle/Expo/Android SDK, исходный код; не используется агрессивный сброс RAM.
7. **Автозапуск и трей:** значок в области уведомлений, регистрация в `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, полное завершение через меню или настройки.

## Сборка из исходников
```powershell
dotnet publish E:\Applications\DesktopIsland\Source\DesktopIsland.csproj -c Release -o E:\Applications\DesktopIsland\App --no-self-contained
```
