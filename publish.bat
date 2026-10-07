@echo off
chcp 65001 >nul
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"
title CarX Deck Tools - сборка

rem ============================================================================
rem  Меню сборки и версии CarX Deck Tools.
rem  Запустите файл двойным кликом и выберите пункт меню.
rem  Для CI/скриптов: publish.bat nopause  - собрать без меню и паузы.
rem ============================================================================

set "PROJECT=src\SteamdeckViewer\SteamdeckViewer.csproj"
set "CONSTS=src\SteamdeckViewer\AppInfo.cs"
set "RID=win-x64"
set "EXE=CarXDeckTools.exe"
set "EMPTY=0"

call :init_colors

if not "%~1"=="" (
    set "NOPAUSE=1"
    call :build
    exit /b !ERRORLEVEL!
)

rem ---------------------------------------------------------------- главное меню
:menu
call :read_versions
cls
call :banner
echo.
echo     %CYN%[1]%R%  %WHT%Собрать%R%                       %DIM%win-x64, один exe%R%
echo     %CYN%[2]%R%  %WHT%Версия программы%R%              %DIM%сейчас%R% %YEL%!VER_APP!%R%
echo.
echo     %CYN%[3]%R%  %WHT%Проверить компиляцию%R%          %DIM%быстро, без публикации%R%
echo     %CYN%[4]%R%  %WHT%Открыть папку publish%R%
echo     %CYN%[5]%R%  %WHT%Очистить папку publish%R%
echo.
echo     %CYN%[0]%R%  %WHT%Выход%R%
echo.
call :rule
set "CH="
set /p "CH=%WHT%  Ваш выбор%R% %DIM%(цифра и Enter):%R% "
if "!CH!"=="" (
    rem ввод закончился (запуск без консоли) - выходим, а не крутимся в меню
    set /a "EMPTY+=1"
    if !EMPTY! GEQ 5 goto :bye
    goto :menu
)
set "EMPTY=0"
if "!CH!"=="1" (
    call :build
    call :hold
    goto :menu
)
if "!CH!"=="2" goto :ver_app
if "!CH!"=="3" goto :quick_build
if "!CH!"=="4" goto :open_publish
if "!CH!"=="5" goto :clean_publish
if "!CH!"=="0" goto :bye
call :warn "Нет такого пункта: !CH!"
call :hold
goto :menu

:bye
echo.
echo   %DIM%Готово. Удачной сборки.%R%
echo.
exit /b 0

rem --------------------------------------------------------- версия программы [2]
:ver_app
cls
call :banner
echo.
echo   %B%%CYN%Версия программы%R%
echo.
echo   %DIM%Что меняется:%R%  константа %WHT%AppVersionText%R% в %WHT%%CONSTS%%R%
echo   %DIM%Куда попадает:%R% заголовок окна программы
echo.
set "VCUR=!VER_APP!"
call :ask_new_value
if not defined NEWVAL goto :menu
set "PP=(?<=const string AppVersionText\s*=\s*\x22)[^\x22]*"
set "PV=!NEWVAL!"
call :patch "%CONSTS%"
set "RC=!ERRORLEVEL!"
echo.
if "!RC!"=="0" call :ok "Версия программы: !VER_APP! » !NEWVAL!   %DIM%(%CONSTS%)%R%"
if "!RC!"=="1" call :err "Не удалось изменить %CONSTS%"
if "!RC!"=="2" call :err "В %CONSTS% не найдена константа AppVersionText"
if "!RC!"=="0" call :remind
call :hold
goto :menu

rem -------------------------------------------------------- проверка сборки [3]
:quick_build
cls
call :banner
call :section "Проверка компиляции (%RID%, без публикации)"
call :need_dotnet
if errorlevel 1 (
    call :hold
    goto :menu
)
dotnet build "%PROJECT%" -c Release -r %RID% --nologo
if errorlevel 1 (
    echo.
    call :err "Код не компилируется - смотрите ошибки выше"
) else (
    echo.
    call :ok "Код компилируется"
)
call :hold
goto :menu

rem --------------------------------------------------------- папка publish [4][5]
:open_publish
if not exist "publish" (
    call :warn "Папки publish ещё нет - сначала соберите проект"
    call :hold
    goto :menu
)
start "" "%~dp0publish"
goto :menu

:clean_publish
cls
call :banner
echo.
echo   %B%%CYN%Очистка папки publish%R%
echo.
if not exist "publish" (
    call :warn "Папки publish и так нет"
    call :hold
    goto :menu
)
echo   %DIM%Будет удалена папка%R% %WHT%%~dp0publish%R% %DIM%со всеми сборками.%R%
echo.
set "CH="
set /p "CH=%WHT%  Удалить?%R% %DIM%(y = да, что угодно другое = нет):%R% "
if /i not "!CH!"=="y" goto :menu
rd /s /q "publish"
echo.
call :ok "Папка publish удалена"
call :hold
goto :menu

rem ============================================================ сборка: процедуры
:build
call :need_dotnet
if errorlevel 1 exit /b 1
call :read_versions
call :section "Сборка %RID%, версия !VER_APP!"
if exist "publish\%RID%" rd /s /q "publish\%RID%"
dotnet publish "%PROJECT%" -c Release -r %RID% -o "publish\%RID%" --nologo
if errorlevel 1 (
    echo.
    call :err "Сборка %RID% завершилась с ошибкой"
    exit /b 1
)
echo.
call :ok "Готово: %DIM%publish\%RID%\%EXE%%R%"
echo.
dir /b "publish\%RID%"
exit /b 0

:need_dotnet
where dotnet >nul 2>&1
if errorlevel 1 (
    echo.
    call :err "Не найден dotnet. Поставьте .NET SDK 10.0+ с https://dotnet.microsoft.com/download"
    exit /b 1
)
exit /b 0

rem ============================================================ версии: процедуры
:read_versions
set "VER_APP=?"
for /f tokens^=2^ delims^=^" %%a in ('findstr /c:"const string AppVersionText" "%CONSTS%"') do set "VER_APP=%%a"
exit /b 0

rem  вход:  VCUR - текущее значение
rem  выход: NEWVAL - новое значение, пусто = отмена
:ask_new_value
set "NEWVAL="
call :suggest_patch
:ask_loop
echo     %DIM%сейчас ......%R% %YEL%!VCUR!%R%
if defined SUGGEST echo     %DIM%предлагаю ...%R% %GRN%!SUGGEST!%R%
echo.
set "IN="
if defined SUGGEST set /p "IN=%WHT%  Новое значение%R% %DIM%(Enter = !SUGGEST!, 0 = отмена):%R% "
if not defined SUGGEST set /p "IN=%WHT%  Новое значение%R% %DIM%(0 = отмена):%R% "
if "!IN!"=="" set "IN=!SUGGEST!"
if "!IN!"=="0" exit /b 0
set "VCHECK=!IN!"
call :validate_ver
if errorlevel 1 (
    echo.
    call :warn "Нужен формат x.y.z, например 1.0.1"
    echo.
    goto :ask_loop
)
if "!IN!"=="!VCUR!" (
    echo.
    call :warn "Там уже !VCUR! - ничего не меняем"
    call :hold
    exit /b 0
)
set "NEWVAL=!IN!"
exit /b 0

:suggest_patch
set "SUGGEST="
set "S1=" & set "S2=" & set "S3="
for /f "tokens=1,2,3 delims=." %%a in ("!VCUR!") do (
    set "S1=%%a"
    set "S2=%%b"
    set "S3=%%c"
)
if not defined S3 exit /b 0
echo(!S3!| findstr /r /c:"^[0-9][0-9]*$" >nul || exit /b 0
set /a "S3=S3+1"
set "SUGGEST=!S1!.!S2!.!S3!"
exit /b 0

:validate_ver
echo(!VCHECK!| findstr /r /c:"^[0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*$" >nul
exit /b !ERRORLEVEL!

rem  вход:  PP - регулярка, PV - новое значение, %1 - файл
rem  выход: 0 - заменено, 1 - ошибка, 2 - совпадений не нашлось
:patch
set "PF=%~1"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$t=[IO.File]::ReadAllText($env:PF); $n=[regex]::Replace($t,$env:PP,$env:PV); if($n -ceq $t){exit 2}; [IO.File]::WriteAllText($env:PF,$n,(New-Object Text.UTF8Encoding $false)); exit 0"
exit /b !ERRORLEVEL!

:remind
echo   %DIM%Дальше: пересоберите программу (пункт [1]) и закоммитьте изменённый файл.%R%
exit /b 0

rem ================================================================== оформление
:init_colors
set "E="
if not defined NO_COLOR for /f %%a in ('echo prompt $E ^| cmd') do set "E=%%a"
if defined E (
    set "R=%E%[0m"
    set "B=%E%[1m"
    set "DIM=%E%[90m"
    set "RED=%E%[91m"
    set "GRN=%E%[92m"
    set "YEL=%E%[93m"
    set "CYN=%E%[96m"
    set "WHT=%E%[97m"
) else (
    set "R=" & set "B=" & set "DIM=" & set "RED=" & set "GRN=" & set "YEL=" & set "CYN=" & set "WHT="
)
exit /b 0

:banner
echo.
echo   %CYN%╔════════════════════════════════════════════════════════════════════╗%R%
echo   %CYN%║%R%  %B%%WHT%C A R X   D E C K   T O O L S%R%                                     %CYN%║%R%
echo   %CYN%║%R%  %DIM%сборка релиза и версия программы%R%                                  %CYN%║%R%
echo   %CYN%╚════════════════════════════════════════════════════════════════════╝%R%
exit /b 0

:rule
echo   %DIM%────────────────────────────────────────────────────────────────────%R%
exit /b 0

:section
echo.
echo   %CYN%──%R% %B%%WHT%%~1%R%
echo.
exit /b 0

:ok
echo   %GRN%[ OK ]%R% %~1
exit /b 0

:warn
echo   %YEL%[ ВНИМАНИЕ ]%R%  %~1
exit /b 0

:err
echo   %RED%[ ОШИБКА ]%R% %~1
exit /b 0

:hold
if defined NOPAUSE exit /b 0
echo.
echo   %DIM%Нажмите любую клавишу...%R%
pause >nul
exit /b 0
