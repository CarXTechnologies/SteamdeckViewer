# SteamdeckViewer — исследование

Цель: приложение на ПК (Windows), которое подключается к Steam Deck по Wi‑Fi или проводу и позволяет:

1. видеть экран Deck и управлять им (Game Mode и рабочий стол Linux);
2. заходить в настройки (Steam и системные);
3. переносить файлы;
4. заливать и запускать тестовые билды;
5. по возможности подключаться отладчиком Rider.

Дата исследования: октябрь 2026. Актуальная стабильная версия SteamOS — **3.8.x** (вышла 17.06.2026).

---

## 0. Коротко о выводах

| Требование | Реализуемо? | Как |
|---|---|---|
| Подключение по Wi‑Fi | ✅ | mDNS-обнаружение + SSH. Так же работает официальный клиент Valve |
| Подключение по проводу | ✅ | USB‑C хаб с Ethernet (рекомендуемый путь). Прямой USB‑C кабель ПК↔Deck тоже возможен, но это эксперимент (gadget mode через BIOS) |
| Сопряжение без пароля | ✅ | Протокол Valve devkit: `POST http://deck:32000/register` с нашим публичным ключом, пользователь подтверждает на Deck |
| Экран и управление | ✅ (берём готовое) | Sunshine на Deck + Moonlight на ПК. Своё видео-кодирование писать не стоит |
| Настройки | ✅ | Steam UI — через поток экрана или через CEF DevTools (`SteamClient.*` JS API). Система — через SSH-команды |
| Рабочий стол Linux | ✅ | `steamos-session-select plasma` / `gamescope` + тот же поток экрана |
| Перенос файлов | ✅ | SFTP (SSH.NET), дельта-синхронизация |
| Тест билдов | ✅ | Заливка → ярлык Steam (non-Steam shortcut) → запуск → логи → остановка. Это повторяет Valve Devkit Client |
| Отладка Rider (Unity, C#) | ✅ | Development Build + Script Debugging → Rider «Attach to Unity Process» → игрок по IP:порт. Наше приложение может само находить порт в Player.log |
| Отладка Rider (обычный .NET) | ✅ | Rider «Attach to Remote Process» через SSH |
| Отладка нативного C++ | ⚠️ частично | Windows-билд под Proton → Visual Studio + msvsmon (официально от Valve). Linux-нативный → gdbserver/lldb (CLion/VS Code). Нативной удалённой отладки C++ в Rider нет |

**Рекомендуемый стек: C# / .NET 10 + Avalonia UI + SSH.NET.** Это твой стек (Rider/Unity), из него же можно сделать Editor-пакет для Unity («Build & Deploy to Deck») и при необходимости агента для Deck (NativeAOT, linux‑x64).

**Честная оговорка:** многое из списка уже делает официальный бесплатный **SteamOS Devkit Client** от Valve (заливка билдов, ярлыки, запуск, консоль CEF, отладка Proton через Visual Studio). Чего у него нет: удалённого экрана, нормального файлового менеджера, интеграции с Rider/Unity и «всё в одном окне». Ценность нашего приложения именно в этом. При этом мы совместимы с его протоколом, так что они не будут конфликтовать.

---

## 1. Что есть в Steam Deck «из коробки» (SteamOS 3.8)

- **ОС:** SteamOS 3 на базе Arch Linux, ядро 6.16. Корневая ФС **только для чтения** (`steamos-readonly disable/enable`). Всё, что ставится через `pacman`, **слетает при обновлении**. Переживают обновления `/home` и `/etc`. Значит, всё наше на Deck должно жить в `/home/deck` (скрипты, агент, flatpak `--user`).
- **Пользователь:** `deck`. По умолчанию **пароля нет**, поэтому `sudo` не работает, пока пользователь не задаст его через `passwd`.
- **Game Mode:** композитор **gamescope**, интерфейс Steam (Big Picture) построен на CEF (Chromium).
- **Desktop Mode:** KDE Plasma **6.4**. С 3.8 по умолчанию **Wayland**, X11 остался как опция.
- **Developer Mode:** Настройки → Система → «Enable Developer Mode». Появляется раздел «Developer» с пунктами:
  - **Pair new host** — сопряжение с devkit-клиентом;
  - **CEF Remote Debugging** — удалённая отладка интерфейса Steam.
- **Devkit service:** служба Valve на Deck. Анонсируется через mDNS как `_steamos-devkit._tcp`, слушает HTTP на порту **32000**.
- **KDE Connect** входит в базовый образ (с 3.4).
- **Python 3** есть в системе: на нём написаны скрипты `devkit-utils` от Valve.
- **Железо:** один порт USB‑C (USB 3.2 Gen 2, DisplayPort Alt Mode). Это не USB4/Thunderbolt, поэтому сеть «по Thunderbolt-кабелю» не получится.

То же самое относится к **Steam Machine** и другим устройствам на SteamOS: Valve прямо пишет, что инструменты devkit одинаковы для Deck и Steam Machine. Наше приложение автоматически будет работать и с ними.

---

## 2. Существующие инструменты: что переиспользовать

| Инструмент | Что делает | Как используем |
|---|---|---|
| **SteamOS Devkit Client** (Valve, бесплатно в Steam, app 943760; код открыт на gitlab.steamos.cloud, Python + Dear ImGui) | Сопряжение, заливка через rsync поверх SSH, ярлык «Devkit Game: …», запуск, консоль CEF, отладка Proton через VS (msvsmon) | Повторяем его протокол (сопряжение, пути, ярлыки), чтобы быть совместимыми. Подсматриваем реализацию |
| **mcp-steamos-devkit** (GitHub, Python) | Сторонняя реимплементация протокола devkit, больше 60 операций | Отличный справочник по протоколу и командам (см. §4) |
| **Sunshine** (хост) + **Moonlight** (клиент) | Поток экрана с низкой задержкой, клавиатура, мышь, геймпад | Ставим Sunshine на Deck (flatpak `dev.lizardbyte.app.Sunshine`, user-scope). Наше приложение запускает Moonlight на ПК |
| **decky-sunshine** (плагин Decky) | Включение/выключение Sunshine прямо из Game Mode | Удобная опция для пользователя |
| **Steam Remote Play / Steam Link** | Стриминг Steam | Deck как *хост* работает нестабильно, особенно для non-Steam игр. Не рассчитываем на него |
| **KRdp** (RDP-сервер KDE, Plasma 6.1+) | RDP в Desktop Mode, подключение из `mstsc` | ⚠️ Нужно проверить, входит ли пакет в SteamOS 3.8. Были баги с чёрным экраном у клиента Windows |
| **Chrome Remote Desktop / VNC** | Удалённый рабочий стол | Запасной вариант только для Desktop Mode |
| **DeckMTP** (Decky) | Deck виден в Проводнике Windows как MTP-устройство по USB‑C | Вариант «файлы по проводу без сети» |
| **WinSCP / KDE Connect / Warpinator** | Файлы | То, что мы встроим в приложение |

---

## 3. Подключение: Wi‑Fi и провод

Основа любого варианта — **IP-сеть между ПК и Deck**. Дальше всё одинаково: SSH, SFTP, HTTP, стрим.

### 3.1 Wi‑Fi
- Обнаружение через **mDNS** (`_steamos-devkit._tcp.local`, имя хоста обычно `steamdeck.local`). Обязательно нужен ручной ввод IP: некоторые роутеры режут multicast между Wi‑Fi-клиентами.
- На время отладки и стрима стоит выключать энергосбережение Wi‑Fi, иначе задержки скачут: `sudo iw dev wlan0 set power_save off` (нужен пароль sudo).

### 3.2 Провод: USB‑C хаб с Ethernet ✅ рекомендуется
- Хаб или док с RJ45 → роутер/свитч, в той же сети, что и ПК. Valve сама советует этот вариант для быстрой заливки.
- Через тот же хаб можно одновременно заряжаться (USB PD).

### 3.3 Провод: Ethernet-кабель напрямую ПК ↔ Deck
- Раз нет DHCP, нужно задать статические адреса (например, `192.168.77.1/24` на ПК и `.2` на Deck) или включить на ПК «Общий доступ к интернету» (ICS).
- Наше приложение может настроить это само: один раз подключиться по Wi‑Fi и выполнить `nmcli connection add type ethernet … ipv4.method manual ipv4.addresses 192.168.77.2/24`.
- IPv6 link-local (`fe80::`) работает без всякой настройки, mDNS по нему тоже.

### 3.4 Провод: USB‑C кабель напрямую (gadget mode) ⚠️ эксперимент
- В BIOS (Vol+ и Power → Setup Utility → Advanced → USB Configuration → **USB Dual-Role Device = DRD** вместо XHCI) USB‑C порт Deck может работать как *устройство*.
- После этого через `configfs` можно поднять **USB-Ethernet (NCM/ECM)**. В Windows 10/11 есть встроенный драйвер NCM. Для доступа только к файлам есть MTP через DeckMTP.
- Минусы:
  - пока порт занят, Deck **не заряжается**;
  - хаб не подключить;
  - нужен root;
  - при DRD порт не работает в Windows на Deck (если она там стоит);
  - может не работать загрузка с USB.
- Вывод: оставить как опцию «на потом», основной проводной путь — §3.2.

### 3.5 Удалённо, не из дома
- Tailscale на обоих концах. SSH, стрим и отладка работают поверх него без проброса портов.

---

## 4. Сопряжение и протокол Valve devkit

Протокол восстановлен по открытому коду клиента Valve и реимплементации `mcp-steamos-devkit`.

**Обнаружение:** mDNS-сервис `_steamos-devkit._tcp.local.`

**HTTP-служба на Deck, порт 32000:**

| Запрос | Назначение |
|---|---|
| `GET /properties.json` | Свойства устройства |
| `GET /login-name` | Имя пользователя для SSH (обычно `deck`) |
| `POST /register` | Регистрация ключа. `Content-Type: text/plain`, тело: `"<ssh-public-key> 900b919520e4cf601998a71eec318fec\n"` (вторая часть — «магическая фраза» из клиента Valve) |

**Порядок сопряжения:**
1. На Deck включаем Developer Mode → Developer → **Pair new host**.
2. Наше приложение генерирует ключ (Valve использует RSA 2048, файл `devkit_rsa`) и отправляет `POST /register`.
3. Пользователь подтверждает запрос **на экране Deck**.
4. Ключ попадает в `authorized_keys`. Дальше работаем по `ssh deck@<ip> -i <key>`.

**Пути и скрипты Valve на Deck:**
- `~/devkit-game/<GameName>/` — залитые билды;
- `~/devkit-utils/` — Python-скрипты, которые клиент заливает на устройство:
  - `steamos-get-status --json`
  - `steamos-list-games`
  - `steamos-prepare-upload --gameid …`
  - `steam-client-create-shortcut`
  - `steam-devkit-rpc run-game`
  - `steamos-set-steam-client`
  - `steamos-delete`
  - `steamos-dump-controller-config`
- `~/.local/share/Steam/logs` — логи Steam.

**Управление сессией (так делает devkit):**
- перезапуск Game Mode: `/usr/bin/steamos-session-select gamescope`
- перезагрузка: `/usr/bin/steamos-polkit-helpers/steamos-reboot-now`

`steamos-polkit-helpers` работают **без пароля sudo**. Это важно: после devkit-сопряжения пароля у `deck` может не быть.

**Запасной путь без Developer Mode:**
1. Пользователь задаёт `passwd` и включает `sudo systemctl enable --now sshd`.
2. Приложение один раз входит по паролю, кладёт свой ключ в `~/.ssh/authorized_keys`.
3. По желанию — отключить вход по паролю.

> ⚠️ Нужно проверить лицензию репозитория `steamos-devkit`, прежде чем копировать `devkit-utils` к себе. Альтернатива — написать свои аналоги (это небольшие Python-скрипты) или переиспользовать те, что уже залил на Deck официальный клиент.

---

## 5. Требования по пунктам

### 5.1 Экран и управление (Game Mode и Desktop)

**Решение: Sunshine на Deck + Moonlight на ПК.** Писать свой видеостриминг (захват gamescope/KMS, аппаратное кодирование VAAPI, передача по сети, ввод) — это месяцы работы, а готовое решение уже даёт низкую задержку, HDR и геймпад.

Что автоматизирует наше приложение:
1. Установка: `flatpak install --user flathub dev.lizardbyte.app.Sunshine` по SSH. Для KMS-захвата нужны повышенные права (`cap_sys_admin`); плагин decky-sunshine решает это за пользователя.
2. Запуск и остановка Sunshine (systemd `--user` юнит или через плагин).
3. Сопряжение: Sunshine Web API (порт 47990, HTTPS, basic auth), `POST /api/pin` с PIN, который показывает Moonlight.
4. Запуск Moonlight на ПК из нашего приложения. У moonlight-qt есть CLI: `pair`, `list`, `stream <host> "<app>"`. ⚠️ Точный синтаксис проверить через `--help`.
5. Порты Sunshine: TCP 47984, 47989, 47990, 48010; UDP 47998–48000.

> ⚠️ **Свежая проблема (сентябрь 2026):** с Sunshine flatpak **2026.906+** в Game Mode часть подключений даёт чёрный экран при живом звуке (`Couldn't get drm fb for plane`, LizardByte/Sunshine#5839). Обходной путь — принудительный композитинг gamescope, за счёт лишней нагрузки на GPU:
> ```
> DISPLAY=:0 xprop -root -f GAMESCOPE_COMPOSITE_FORCE 32c -set GAMESCOPE_COMPOSITE_FORCE 1
> ```
> Наше приложение может включать это на время сессии и выключать (`… -set … 0`) после.

**Desktop Mode:** работает тот же Sunshine. Если в 3.8 есть KRdp, можно добавить кнопку «RDP» (`mstsc`).

**Лёгкий режим без стрима (идея на потом):** периодические скриншоты и отправка ввода через агента (`uinput`). Подходит для «глянуть, что на экране».

### 5.2 «Заходить в настройки»

**Настройки Steam (Game Mode):**
- через поток экрана — просто тыкаем мышью/геймпадом;
- **программно через CEF DevTools.** Включаем в Developer → «CEF Remote Debugging». Steam UI открывает DevTools по сети: `http://<deck-ip>:8081`, локально на Deck `:8080`. Нужные вкладки — `SharedJSContext` (там живёт основная JS-логика) и `Steam Big Picture Mode`.
  - Через Chrome DevTools Protocol (обычный WebSocket) можно выполнять JS вроде `SteamClient.Apps.*`, `SteamClient.Console.ExecCommand("…")` и т.п. На этом же механизме построены Decky Loader и кнопка «CEF Console» в devkit-клиенте Valve.
  - Это даёт кнопки «открыть настройки», «запустить игру», «выполнить команду консоли Steam» без видео.
  - ⚠️ API `SteamClient` не документирован и меняется между версиями Steam. Использовать точечно.

**Системные настройки — через SSH:**

| Действие | Команда | Нужен sudo |
|---|---|---|
| В рабочий стол | `steamos-session-select plasma` | нет |
| В Game Mode | `steamos-session-select gamescope` | нет |
| Перезагрузка | `steamos-polkit-helpers/steamos-reboot-now` | нет |
| Сон | `systemctl suspend` | ⚠️ проверить |
| Громкость | `wpctl set-volume @DEFAULT_AUDIO_SINK@ 50%` | нет |
| Wi‑Fi / сеть | `nmcli …` | обычно нет (polkit) |
| Батарея, температуры, частоты | `/sys/class/power_supply/BAT1/*`, `/sys/class/hwmon/*`, `/sys/class/drm/card*/device/gpu_busy_percent` | нет (чтение) |
| Обновления | `steamos-update check` | ⚠️ проверить |
| Логи | `journalctl --user -f`, `~/.local/share/Steam/logs` | нет |

Это превращается в «панель устройства»: графики CPU/GPU/батареи, кнопки питания и режимов.

### 5.3 Рабочий стол Linux
- Переключение режимов — команды из §5.2.
- Изображение и ввод — Sunshine/Moonlight (или RDP, если доступен).
- SSH-терминал прямо в приложении (SSH.NET shell stream + терминальный контрол) или кнопка «открыть в Windows Terminal» (`ssh deck@…`).

### 5.4 Перенос файлов
- **SFTP** (SSH.NET): двухпанельный файловый менеджер, drag&drop, прогресс, очередь.
- **Быстрая заливка папок (билдов):**
  - первая заливка: поток `tar` поверх SSH-канала (`tar -x -C <dst>` на Deck) — намного быстрее, чем тысячи мелких SFTP-операций;
  - повторные: **дельта-синхронизация** — сверяем манифест (размер, mtime, хеш) локально и на Deck, отправляем только изменённое, удаляем лишнее (аналог «Clean upload» у Valve);
  - Valve делает это через `rsync`. На Windows rsync «из коробки» нет: devkit-клиент возит свою сборку, есть вариант cwRsync/WSL. Своя дельта-синхронизация на C# избавит от этой зависимости.
- Права на исполняемые файлы при заливке выставлять явно (`chmod +x`), потому что NTFS их не хранит.

### 5.5 Тест билдов

Конвейер, совместимый с devkit Valve:
1. **Заливка** в `~/devkit-game/<Name>/` (§5.4).
2. **Ярлык в Steam** (non-Steam shortcut «Devkit Game: <Name>»): команда запуска, аргументы, галочка Proton для Windows-билдов. Это нужно, чтобы игра:
   - нормально появилась и получила фокус в Game Mode — gamescope показывает игры, запущенные через Steam; если запустить бинарник напрямую по SSH, окна в Game Mode может быть не видно;
   - получила Steam Input и оверлей.
3. **Запуск и остановка** (`steam-devkit-rpc run-game`, `steam://rungameid/<id>`, kill по PID).
4. **Логи в реальном времени:**
   - Unity Linux: `~/.config/unity3d/<Company>/<Product>/Player.log`; можно передать `-logFile <путь>` в аргументах запуска;
   - Unity Windows под Proton: внутри префикса `~/.local/share/Steam/steamapps/compatdata/<appid>/pfx/drive_c/users/steamuser/AppData/LocalLow/<Company>/<Product>/Player.log`;
   - Proton: `PROTON_LOG=1` → `~/steam-<appid>.log`;
   - краши: `coredumpctl list`.
5. **Производительность:** встроенный MangoHud/оверлей производительности, логирование MangoHud в файл, Unity Profiler (см. §5.6).

**Linux-нативный или Windows под Proton?** Для Unity проще и честнее тестировать **Linux-билд** (IL2CPP или Mono, x86_64). Windows-билд под Proton тоже работает, это полезно, если в Steam выходит именно Windows-версия. Поддерживаем оба варианта.

### 5.6 Отладка в Rider

#### A. Unity-билд (managed C#) ✅ главный сценарий
Требования к билду: **Development Build** + **Script Debugging**. Опционально **Wait For Managed Debugger**, чтобы игра ждала подключения отладчика на старте.

Как это устроено:
- Плеер открывает TCP-порт отладчика Mono: **`56000 + (guid % 1000)`**, где guid случайный, поэтому порт заранее не известен. В Player.log пишется строка вида `Starting managed debugger on port 56xxx`.
- Плеер шлёт multicast-анонсы на **`225.0.0.222:54997`** со своим IP и портом. Rider их слушает и показывает плеер в **Run → Attach to Unity Process**.
- Если multicast не доходит (типично для Wi‑Fi): **Add player address manually** → IP Deck + порт.
- Работает и для **IL2CPP**-плееров (нужен Script Debugging).
- Брандмауэр Windows должен пропускать входящий UDP для Rider. На SteamOS по умолчанию брандмауэр не мешает.

Что может добавить наше приложение:
1. Читает Player.log по SSH, находит порт отладчика и показывает `IP:порт` с кнопкой «скопировать».
2. **Multicast-ретранслятор:** приложение само рассылает в локальную сеть ПК тот же анонс, что и плеер (с IP Deck). Тогда Deck-плеер автоматически появляется в списке Rider даже по Wi‑Fi. ⚠️ Проверить формат пакета; он виден в Player.log и в доке Unity.
3. Запуск с «Wait For Managed Debugger» и подсказкой, когда можно подключаться.
4. **Unity Profiler:** тот же Development Build; Profiler → подключение по IP Deck. Порт player connection начинается с 55000.

**Windows-билд под Proton:** сокеты Wine отображаются на сокеты Linux, поэтому Mono-отладчик плеера должен быть доступен так же по IP Deck. ⚠️ Проверить на практике, особенно multicast из-под Wine. Путь к Player.log см. в §5.5.

#### B. Обычное .NET-приложение (не Unity) ✅
- Rider: **Run → Attach to Remote Process**, подключение по SSH (ключ, пароль или OpenSSH config). Rider сам заливает на Deck свои debugger tools и подключается к процессу .NET / Mono.
- На Deck нет .NET runtime, поэтому публиковать self-contained (`-r linux-x64 --self-contained`).

#### C. Нативный код (C++, нативный уровень IL2CPP) ⚠️
- Windows-билд под Proton: официальный путь Valve — **Visual Studio + msvsmon** (галочка «Steam Play debug» в devkit-клиенте, только с Proton Experimental, нужно положить debug runtime DLL рядом с билдом). Rider так не умеет.
- Linux-нативный: `gdbserver`/`lldb-server` на Deck → CLion / VS Code / gdb. В Rider удалённой нативной отладки нет.

---

## 6. Архитектура

### 6.1 Выбор языка

| Вариант | Плюсы | Минусы |
|---|---|---|
| **C# / .NET 10 + Avalonia** ✅ | Твой стек (Rider, Unity). SSH.NET зрелая. Single-file publish. Общий код с Unity Editor-пакетом и CLI. Агент на Deck можно собрать NativeAOT | Встроить видео внутрь окна сложно, поэтому Moonlight будет отдельным окном |
| C# + WPF | Проще, если нужен только Windows | Только Windows |
| Python + ImGui (форк клиента Valve) | Сразу получаем весь функционал devkit | Не твой стек, неудобно упаковывать под Windows |
| Go + Wails / Rust + Tauri | Один бинарник, хорошие SSH-библиотеки | Новый язык и UI на веб-стеке |
| Electron / TS | Быстрый UI | Тяжело, SSH через node-библиотеки |

### 6.2 Схема

```mermaid
flowchart LR
  subgraph PC[ПК Windows]
    UI[SteamdeckViewer UI<br/>Avalonia]
    CLI[deck CLI<br/>dotnet tool]
    UPKG[Unity Editor package<br/>Build & Deploy]
    CORE[Core: discovery, pairing,<br/>SSH/SFTP, deploy, CDP, logs]
    ML[Moonlight]
    RIDER[Rider]
    UI --> CORE
    CLI --> CORE
    UPKG --> CLI
    UI -. запускает .-> ML
  end
  subgraph DECK[Steam Deck / SteamOS]
    DS[devkit service :32000]
    SSHD[sshd :22]
    SUN[Sunshine :47984-48010]
    CEF[Steam CEF DevTools :8081]
    GAME[Unity player<br/>debugger :56xxx]
    AG[опц. агент<br/>systemd --user]
  end
  CORE -- mDNS + HTTP register --> DS
  CORE -- SSH / SFTP / tar --> SSHD
  CORE -- WebSocket CDP --> CEF
  ML -- стрим + ввод --> SUN
  RIDER -- Mono soft debugger --> GAME
  GAME -. multicast 225.0.0.222:54997 .-> RIDER
  AG -. метрики, скриншоты, ввод .-> CORE
```

### 6.3 Модули и библиотеки

| Модуль | Ответственность | Библиотеки (NuGet) |
|---|---|---|
| Discovery | mDNS `_steamos-devkit._tcp`, ручной IP, история устройств | `Makaretu.Dns.Multicast` или `Zeroconf` |
| Pairing | Генерация ключа, `POST /register`, запасной вход по паролю | `SSH.NET` (поддерживает ed25519/RSA), `HttpClient` |
| Remote shell | Выполнение команд, терминал | `SSH.NET` |
| Files | SFTP-менеджер, tar-стрим, дельта-синхронизация | `SSH.NET`, `System.Formats.Tar` |
| Deploy | Заливка → ярлык → запуск/стоп → логи | собственный код + `devkit-utils` или свои скрипты |
| Steam UI | CDP-клиент к CEF | `System.Net.WebSockets` |
| Streaming | Установка и настройка Sunshine, запуск Moonlight | `Process`, `HttpClient` |
| Debug helper | Парсинг Player.log, multicast-ретранслятор | `UdpClient` |
| Telemetry | Чтение `/sys` по SSH (или от агента), графики | `LiveCharts2` / `ScottPlot` |
| UI | Avalonia + MVVM | `Avalonia`, `CommunityToolkit.Mvvm` |

### 6.4 Структура репозитория (предложение)

```
SteamdeckViewer/
  src/
    SteamdeckViewer.Core/      # протокол, SSH, SFTP, deploy, CDP
    SteamdeckViewer.Cli/       # `deck pair|push|run|logs|shell` — для CI и Unity
    SteamdeckViewer.App/       # Avalonia UI
    SteamdeckViewer.Agent/     # (позже) агент для Deck, NativeAOT linux-x64
  unity/
    com.steamdeckviewer.deploy/  # Editor-меню "Build & Deploy to Steam Deck"
  deck-scripts/                # свои скрипты для Deck (если не берём devkit-utils)
  RESEARCH.md
```

**Агент на Deck не нужен для MVP:** почти всё делается командами по SSH. Агент появится, когда понадобятся постоянные метрики, скриншоты или ввод (`uinput`). Ставится в `~/.local/bin`, запускается как `systemd --user`, поэтому переживает обновления SteamOS.

---

## 7. План работ

| Этап | Содержание | Результат |
|---|---|---|
| **0. Подготовка Deck** | Developer Mode, Pair new host (или `passwd` + `sshd`), проверка `ssh deck@steamdeck.local` | Ручной доступ работает |
| **1. MVP-ядро** | Discovery, Pairing (`/register`), список устройств, SSH-консоль, панель «инфо + питание + режимы» | Подключаемся и управляем без пароля |
| **2. Файлы** | SFTP-менеджер, tar-заливка, дельта-синхронизация | Удобный перенос файлов |
| **3. Билды** | Профили билдов (папка, exe, аргументы, Proton вкл/выкл), ярлык Steam, запуск/стоп, живой лог | Цикл «собрал → залил → запустил» в один клик |
| **4. Rider/Unity** | Поиск порта отладчика, multicast-ретранслятор, Unity Editor-пакет + CLI, подключение Profiler | Отладка с брейкпоинтами на Deck |
| **5. Экран** | Автоустановка и сопряжение Sunshine, запуск Moonlight, обход чёрного экрана, кнопка RDP (если есть KRdp) | Видим и управляем Deck с ПК |
| **6. Расширения** | CDP-команды Steam UI, телеметрия и графики, агент, скриншоты, USB gadget-сеть | «Всё в одном» |

---

## 8. Риски и что проверить на реальном Deck

1. Включает ли devkit-сопряжение `sshd` само, или нужно `systemctl enable --now sshd` (без sudo это невозможно).
2. Есть ли пакет KRdp в SteamOS 3.8 (System Settings → Remote Desktop).
3. Стабильность Sunshine в Game Mode на текущей версии (баг 2026.906+) и способ дать KMS-права без root (decky-sunshine).
4. Rider ↔ Unity-плеер под Proton: доступность порта отладчика и multicast из-под Wine.
5. Формат multicast-пакета Unity для ретранслятора.
6. Лицензия `steamos-devkit` (можно ли включать `devkit-utils` в наш дистрибутив).
7. Нестабильность недокументированного `SteamClient` JS API между версиями Steam.
8. Режим USB gadget (DRD): работает ли NCM на текущем ядре и не ломает ли док-станцию.

---

## 9. Вопросы к тебе перед стартом

1. **Движок и билды:** Unity? Какая версия? Mono или IL2CPP? Тестируем Linux-билд, Windows через Proton или оба?
2. **UI:** Avalonia (кроссплатформенно) или WPF (только Windows, проще)?
3. **Где вести код:** создать git-репозиторий в этой папке? Нужен ли GitHub?
4. **Модель Deck** (LCD/OLED) и версия SteamOS. Включён ли уже Developer Mode, задан ли пароль `deck`?
5. **Порядок:** предлагаю начать с этапов 1–3 (подключение, файлы, билды), затем Rider, затем экран. Подходит?

---

## 11. Удалённый экран: исследование и решение (октябрь 2026)

### Что выяснилось
- **Захват Game Mode возможен только через KMS.** Gamescope не умеет XDG Portal и KWin screencast, поэтому Sunshine нужен `CAP_SYS_ADMIN`, то есть права root.
- **Flatpak и AppImage Sunshine KMS-захват официально не поддерживают.**
- **Нативный пакет через pacman не годится.** Корень SteamOS только для чтения, а такие пакеты стираются при обновлении ОС. Так ставится форк Polaris, и после обновлений его приходится переустанавливать.
- **decky-sunshine решает это иначе:**
  - flatpak Sunshine ставится в system-установку;
  - запускается от root (бэкенд Decky работает как root);
  - `FLATPAK_BWRAP` указывает на setuid-копию `/usr/bin/bwrap`, так песочница сохраняет `CAP_SYS_ADMIN`;
  - конфиг лежит в `/root/.var/app/dev.lizardbyte.app.Sunshine/config/sunshine/`.

  Схема проверена пользователями на Deck.
- **Чёрный экран в Game Mode.** У Sunshine 2026.906+ на SteamOS 3.8 он бывает при части подключений (баг LizardByte/Sunshine#5839, не исправлен). KMS-захват берёт только первую плоскость. Обход: `GAMESCOPE_COMPOSITE_FORCE=1` через `xprop` на время стрима. Это немного нагружает GPU.
- **API Sunshine** на порту 47990 (HTTPS с самоподписанным сертификатом, Basic auth):
  - с версии 2026.9 сопряжение адресное: `GET /api/pin` отдаёт ожидающие запросы Moonlight (`id`, `name`, `address`), `POST /api/pin` `{"pairing_id","pin","name"}` подтверждает один из них и отвечает только после обмена ключами (до `ping_timeout`, по умолчанию 10 с), `DELETE /api/pin` `{"pairing_id"}` отменяет. Без `pairing_id` приходит 400. Moonlight шлёт постоянный `uniqueid`, поэтому неотвеченный запрос прошлой попытки блокирует новую (409), пока его не отменить или он не истечёт (5 мин);
  - до 2026.9: `POST /api/pin` `{"pin","name"}`, а `GET /api/pin` нет (404);
  - `GET /api/clients/list` — список сопряжённых клиентов.

  Клиентам без заголовков Origin/Referer CSRF-токен не нужен. Логин и пароль задаются командой `sunshine --creds <user> <pass>`.
- **Moonlight-qt CLI:**
  - `pair <host> --pin <4 цифры>`;
  - `stream <host> <app>` с `--display-mode windowed|fullscreen|borderless`, `--resolution WxH`, `--fps`, `--bitrate` (Кбит/с), `--absolute-mouse` (режим удалённого рабочего стола), `--performance-overlay`;
  - также `list` и `quit`.
- **mDNS на SteamOS.** Публикация avahi по умолчанию выключена, Deck сам о себе не объявляет. Поэтому автопоиск надёжнее делать перебором подсети на порт 32000.
- **Рабочий стол через KMS получается повёрнутым** (проверено на Deck, см. также LizardByte/Sunshine#2439). Панель Deck физически вертикальная, 800×1280.
  - В Game Mode gamescope поворачивает картинку аппаратно, свойством `rotation` плоскости DRM. Sunshine это свойство читает и разворачивает кадр.
  - KWin 6.4 поворачивает аппаратно только полноэкранные окна. Рабочий стол он рисует уже повёрнутым в вертикальный буфер, и KMS-захват отдаёт его лёжа на боку.
  - Переменной окружения, которая включила бы аппаратный поворот для всего рабочего стола, в KWin нет.
- **Захват рабочего стола без поворота.** SteamOS 3.8 по умолчанию запускает рабочий стол на Wayland, поэтому захват `x11` не подходит: Xwayland не видит окна Wayland.
  - Документация Sunshine для KDE Plasma советует захват `portal` (xdg-desktop-portal) или `kwin`.
  - Постоянное разрешение без диалога на Deck даёт `flatpak permission-set kde-authorized remote-desktop dev.lizardbyte.app.Sunshine yes`.
  - Захват `kwin` из flatpak упирается в права KWin (нужен файл .desktop с `X-KDE-Wayland-Interfaces`) и в ограниченный доступ flatpak к PipeWire.
  - Захват `portal` работает в любой установке, но только в сеансе пользователя (шина D-Bus сеанса), а не от root.
- **Sunshine выбирает способ захвата один раз при старте.** Его можно задать в командной строке (`sunshine capture=portal`), это перекрывает `sunshine.conf`. Каталог настроек задаётся переменной `CONFIGURATION_DIRECTORY` (`$CONFIGURATION_DIRECTORY/sunshine`), она важнее `XDG_CONFIG_HOME`.
- **root внутри flatpak — не настоящий root.** Даже с setuid-копией `bwrap` песочница работает в своём пространстве пользователей. Файлы чужих пользователей там доступны только по правам «для всех», поэтому закрытый `/home/deck` для неё недоступен.
- **Абсолютная мышь в Game Mode не работает** (проверено на Deck, ошибка gamescope ValveSoftware/gamescope#2458, открыта). Sunshine 2026.x создаёт для режима Moonlight `--absolute-mouse` устройство с `ABS_X`/`ABS_Y` 0..65535. gamescope берёт `libinput_event_pointer_get_absolute_x()` без пересчёта в размер экрана, и курсор стоит в правом нижнем углу. Клавиатура и относительная мышь работают. Поэтому в Game Mode Moonlight запускается с `--no-absolute-mouse`, а на рабочем столе (KWin) — с `--absolute-mouse`. Флаг передаётся всегда, иначе Moonlight возьмёт режим из своих настроек.
- **Ввод от имени пользователя.** Мыши, клавиатуре и геймпаду Sunshine нужны `/dev/uinput` и `/dev/uhid`. Правило udev из пакета (`share/sunshine/udev/rules.d/60-sunshine.rules`, `TAG+="uaccess"`) даёт к ним доступ активному пользователю.

### Решение
Повторяем схему decky-sunshine, но без Decky. Одна установка с паролем sudo (пароль вводится в приложении и не сохраняется):
1. `flatpak install --system flathub dev.lizardbyte.app.Sunshine`.
2. Системная служба `sdv-sunshine.service`. Перед запуском (`ExecStartPre`) она каждый раз кладёт свежую setuid-копию `bwrap` в `/var/lib/steamdeckviewer`, затем запускает Sunshine от root с `FLATPAK_BWRAP` и `PULSE_SERVER` пользователя `deck`.
3. Правило polkit: пользователь `deck` может запускать и останавливать только эту службу без пароля. Дальше «Запустить» и «Остановить» работают по SSH без sudo.
4. Правило udev из пакета Sunshine (или запасное для `uinput` и `uhid`) и автозагрузка модуля `uhid`.
5. `sunshine.conf` в `/root/.var/app/dev.lizardbyte.app.Sunshine/config/sunshine`: `encoder = vaapi`, `adapter_name = /dev/dri/renderD128`, `system_tray = disabled`, а `capture` не задаётся. Логин и пароль веб-интерфейса генерирует приложение.

Службы две, и у каждой свой каталог настроек:
- **Game Mode:** системная `sdv-sunshine.service` от root с `capture=kms` и каталогом в `/root`.
- **Рабочий стол:** пользовательская `sdv-sunshine-desktop.service` от `deck` с `capture=portal` (`x11` в сеансе X11) и каталогом в `~/.var/app/dev.lizardbyte.app.Sunshine/config/sunshine`. Скрипт запуска создаёт её и выдаёт разрешение `kde-authorized`.

Почему каталоги разные. Песочница flatpak работает в отдельном пространстве пользователей, и root в ней не может зайти в закрытый домашний каталог `deck`: `bwrap: Can't find source path …: Permission denied`, проверено на Deck. Чтобы у служб были одни ключи, `uniqueid`, логин и сопряжения и Moonlight видел один хост, каталоги синхронизирует `/var/lib/steamdeckviewer/sync-config.sh`. Он работает вне песочницы: как `ExecStartPre` (от `deck` к root) и `ExecStopPost` (от root к `deck`). Каталог копируется целиком, только если `sunshine_state.json` на исходной стороне новее. Одновременно работает одна служба, поэтому новее всегда каталог той, что работала последней. Установка сначала забирает свежие сопряжения от `deck`, затем принудительно копирует итоговые настройки ему.

Скрипт запуска выбирает службу по тому, что на экране Deck (gamescope или Plasma), и останавливает другую: им нужны одни и те же порты.

`/etc` и `/var` сохраняются при обновлениях SteamOS, так что установка их переживает. Кнопка «Удалить» убирает всё перечисленное.

На ПК:
- «Открыть экран» запускает службу под текущий режим, при необходимости проводит сопряжение и открывает Moonlight.
- Пока Moonlight открыт (и ещё 30 с после его закрытия), приложение каждые 3 секунды проверяет режим Deck. Если новый режим держится две проверки подряд, оно запускает другую службу и открывает Moonlight заново.
- Сопряжение: приложение отменяет зависшие запросы с адресов ПК, генерирует PIN и запускает `Moonlight.exe pair <ip> --pin`. Затем ждёт в `GET /api/pin` новый запрос с адреса ПК (или единственный новый) и отправляет для него тот же PIN. На старых версиях PIN уходит без `pairing_id`. Запросы идут через SSH (`curl` на самом Deck), поэтому не мешают ни VPN, ни сертификат.
- Стрим: `Moonlight.exe stream <ip> Desktop` с разрешением Deck 1280×800.

При включённом AmneziaVPN в исключения приложений нужно добавить и `Moonlight.exe`.

---

## 10. Источники

- Valve, Steamworks — загрузка игр на Steam Deck/Steam Machine (Devkit Client, сопряжение, Title Upload, CEF Console): https://partner.steamgames.com/doc/steamhardware/loadgames
- Valve, Steamworks — отладка Windows-билдов под Proton (Visual Studio, msvsmon): https://partner.steamgames.com/doc/steamhardware/debugging
- Proton — DEBUGGING-WINDOWS.md: https://github.com/ValveSoftware/Proton/blob/bleeding-edge/docs/DEBUGGING-WINDOWS.md
- GamingOnLinux — Valve открыла исходники SteamOS Devkit Client/Service: https://www.gamingonlinux.com/2022/03/valve-open-sources-steamos-devkit-client-for-steam-deck
- mcp-steamos-devkit (реимплементация протокола: порт 32000, `/register`, `devkit-utils`): https://github.com/elliotttate/mcp-steamos-devkit
- rubenwardy — как devkit заливает игру (пользователь `deck`, `devkit_rsa`, `~/devkit-game`, rsync): https://blog.rubenwardy.com/2022/10/16/steam-deck-upload-game/
- SteamOS 3.8.10 stable (Plasma 6.4, Wayland по умолчанию, Steam Machine): https://phoronix.com/news/SteamOS-3.8.10-Stable , https://www.comss.ru/page.php?id=20977
- SteamOS 3.7 (Plasma 6.2.5): https://steamdeckhq.com/news/steamos-3-7-0-preview-channel-huge-changes/
- Decky — CEF debugging (порты 8080/8081, вкладки): https://wiki.deckbrew.xyz/plugin-dev/cef-debugging
- decky-sunshine и баг с чёрным экраном на Sunshine 2026.906+: https://github.com/s0t7x/decky-sunshine , https://github.com/s0t7x/decky-sunshine/issues/129
- Sunshine — документация: https://docs.lizardbyte.dev/projects/sunshine/
- Rider — отладка Unity-плееров по сети: https://www.jetbrains.com/help/rider/Debugging_Unity_Applications.html , https://blog.jetbrains.com/dotnet/2020/08/11/debugging-unity-players-over-network-and-usb-with-rider-2020-2/
- Rider — Attach to Remote Process (SSH): https://www.jetbrains.com/help/rider/attach-to-process.html , https://blog.jetbrains.com/dotnet/2018/11/29/remote-debugging-comes-rider-2018-3/
- Unity — troubleshooting managed debugging (multicast 225.0.0.222:54997, TCP-порт): https://docs.unity3d.com/Manual/managed-debugging-troubleshooting.html
- Unity — порт отладчика `56000 + guid % 1000`: https://docs.unity3d.com/ru/Manual/ManagedCodeDebugging.html
- Unity — сборка под Linux (IL2CPP, символы): https://docs.unity3d.com/6000.2/Documentation/Manual/build-for-linux.html
- Steam Deck USB‑C как устройство (BIOS DRD, DeckMTP): https://tech.yahoo.com/computing/articles/steam-decks-usb-c-port-110023697.html
- Remote Play с Deck как хостом — проблемы: https://www.gamingonlinux.com/2024/02/remote-play-broken-on-steam-deck-with-the-february-stable-update/
- Включение SSH на Deck: https://pulsegeek.com/articles/enable-ssh-on-steam-deck-secure-remote-access
- decky-sunshine — запуск Sunshine от root с setuid-копией bwrap, API, GAMESCOPE_COMPOSITE_FORCE: https://github.com/s0t7x/decky-sunshine/blob/main/py_modules/sunshine.py
- Sunshine API (Basic auth, CSRF для не-браузерных клиентов, /api/pin, /api/clients/list): https://docs.lizardbyte.dev/projects/sunshine/latest/md_docs_2api.html
- Sunshine — настройки (capture, encoder, adapter_name): https://docs.lizardbyte.dev/projects/sunshine/latest/md_docs_2configuration.html
- Sunshine — KMS недоступен во flatpak/AppImage, `--creds`: https://docs.lizardbyte.dev/projects/sunshine/latest/md_docs_2troubleshooting.html
- Чёрный экран в Game Mode, Sunshine 2026.906+: https://github.com/LizardByte/Sunshine/issues/5839
- Gamescope без portal-захвата, KMS обязателен: https://docs.bazzite.gg/Advanced/sunshine/
- Повёрнутая картинка KMS-захвата на Deck и обход через X11: https://github.com/LizardByte/Sunshine/issues/2439
- Абсолютная мышь Sunshine в gamescope застревает в углу: https://github.com/ValveSoftware/gamescope/issues/2458
- Sunshine — захват `portal`/`kwin` для KDE Plasma и `kde-authorized`: https://github.com/LizardByte/Sunshine/blob/master/docs/troubleshooting.md
- Sunshine — разбор поворота панели в KMS-захвате и захват KWin: https://github.com/LizardByte/Sunshine/blob/master/src/platform/linux/kmsgrab.cpp , https://github.com/LizardByte/Sunshine/blob/master/src/platform/linux/kwingrab.cpp
- KWin 6.4 — аппаратный поворот только при прямом выводе: https://github.com/KDE/kwin/blob/Plasma/6.4/src/backends/drm/drm_pipeline.cpp
- Polaris на SteamOS (пакет pacman, avahi без публикации): https://github.com/papi-ux/polaris/blob/HEAD/docs/steamos.md
- Moonlight-qt — параметры командной строки: https://github.com/moonlight-stream/moonlight-qt/blob/master/app/cli/commandlineparser.cpp
