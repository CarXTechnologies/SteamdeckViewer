# SteamdeckViewer

Приложение для Windows (Avalonia, .NET 10), которое управляет Steam Deck по Wi‑Fi или проводу (USB‑C хаб с Ethernet). Заливает и запускает тестовые Linux-билды Unity и помогает подключить к ним отладчик Rider.

Исследование и план — в [RESEARCH.md](RESEARCH.md).

## Что умеет

| Вкладка | Возможности |
|---|---|
| **Steam Deck** (верхняя панель) | Поиск в сети (mDNS), добавление по IP, сопряжение через Developer Mode (протокол SteamOS Devkit), установка ключа по паролю, подключение по SSH |
| **Устройство** | Версия SteamOS, режим (Game Mode / рабочий стол), батарея, температуры, память, диск. Переключение Game Mode ↔ рабочий стол, перезапуск Steam, перезагрузка и выключение, SSH-терминал (Windows Terminal), включение CEF-отладки Steam |
| **Файлы** | Обзор Deck по SFTP, загрузка файлов и папок (кнопками или перетаскиванием из Проводника), скачивание, создание, переименование, удаление |
| **Билды** | Профили билдов, заливка с дельтой (повторно уходят только изменённые файлы), ярлык «Devkit Game» в библиотеке Steam, запуск и остановка, живой `Player.log` |
| **Отладка (Rider)** | Адрес и порт отладчика Unity-плеера из `Player.log`, повтор анонса плеера в сети ПК, чтобы Rider видел его сам |
| **Консоль** | Разовые команды на Deck |

## Первый запуск

1. На Deck: **Настройки → Система → Enable Developer Mode**, затем **Настройки → Developer → Pair new host**.
2. В приложении: **Найти в сети** (или **Добавить…** по IP), затем **Сопрячь (Developer Mode)** и подтвердить запрос на Deck.
3. **Подключить**.

Если Deck уже сопряжён официальным SteamOS Devkit Client, его ключ подхватывается сам: достаточно добавить Deck и нажать «Подключить».

Без Developer Mode: в Konsole на Deck задать пароль (`passwd`), включить SSH (`sudo systemctl enable --now sshd`) и нажать **Ключ по паролю…**. Пароль не сохраняется.

Ключ приложения лежит в `%APPDATA%\SteamdeckViewer\keys\deck_rsa`, настройки — в `%APPDATA%\SteamdeckViewer\settings.json`.

## VPN

Клиенты на WireGuard/AmneziaWG (в том числе AmneziaVPN) при маршрутизации всего трафика включают kill switch. Он блокирует всю локальную сеть: ping показывает «Общий сбой», а приложение пишет, что соединение заблокировано VPN. Чтобы работать с Deck, не выключая VPN, это настраивается один раз:

- **AmneziaVPN**, проверенный вариант: «Раздельное туннелирование приложений» → добавить `SteamdeckViewer.exe` и `C:\Windows\System32\OpenSSH\ssh.exe` (для SSH-терминала). Если Rider подключается к игре при включённом VPN, его тоже нужно исключить, либо исключить подсеть, как в следующем пункте.
- **AmneziaVPN**, исключение по адресам: «Раздельное туннелирование сайтов» → режим «Адреса из списка не должны открываться через VPN» → добавить подсеть офиса (например, `10.23.0.0/16`).
- **Конфиг WireGuard/AmneziaWG напрямую**: в `AllowedIPs` заменить `0.0.0.0/0` на `0.0.0.0/1, 128.0.0.0/1`. Весь интернет по-прежнему идёт через VPN, но kill switch не включается ([netquirk.md](https://github.com/amnezia-vpn/amneziawg-windows-client/blob/master/docs/netquirk.md)).

## Тестовый билд CarX Street

1. **Build/Street Build Settings**: платформа Linux, Development и Script Debugging (для Rider), EAC выключен.
2. Вкладка **Билды**: указать папку билда (`CarX_Street.x86_64` подставится сам) и нажать **Залить и запустить**.
3. Игра появится в библиотеке Steam как «Devkit Game: CarX-Street» и запустится в Game Mode.

Удаление лишних файлов на Deck включено по умолчанию: проверка целостности игры (`player_layout.bundle`, ошибка E29) не терпит старых `.so`/`.dll` в папке билда.

Отладка: Rider → **Run → Attach to Unity Process** → плеер из списка или **Add player address manually** с адресом с вкладки «Отладка». Анти-отладка `DD.cs` закрывает игру при подключённом отладчике, если в билде нет `DEV_BUILD` или `DISABLE_CHECK_DEFENCE`.

## Сборка и запуск

```bash
dotnet run --project src/SteamdeckViewer
```

Релизная сборка в один exe:

```bash
dotnet publish src/SteamdeckViewer -c Release -r win-x64 -o publish/win-x64
```

Тесты (скрипты для Deck прогоняются в WSL, без WSL эти тесты пропускаются):

```bash
dotnet test
```

## Устройство проекта

- `src/SteamdeckViewer.Core` — всё без UI:
  - протокол devkit (`DevkitService`, `DeckDiscovery`, `DeckKeys`);
  - SSH/SFTP (`DeckConnection`);
  - заливка tar-потоком (`FolderSync`);
  - ярлыки и запуск через `~/.steam/steam.pipe` (`DevkitGames`);
  - разбор `Player.log` и повтор анонса для Rider (`UnityPlayer`).
- `src/SteamdeckViewer` — окно Avalonia, разметка на C# (без XAML), по одной partial-части на вкладку.
- `tests/SteamdeckViewer.Tests` — xUnit: фильтры, разбор логов, формат ключей против `ssh-keygen`, протокол devkit на поддельной службе, скрипты для Deck и tar в Linux (WSL).
