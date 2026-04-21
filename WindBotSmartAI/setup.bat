@echo off
REM ============================================================
REM  WindBot Smart AI — Quick Setup
REM  Run this from C:\ProjectIgnis\WindBotSmartAI\
REM ============================================================

SET PROJ=C:\ProjectIgnis
SET SRC=%PROJ%\WindBotSrc

echo.
echo === Step 1: Clone WindBot source ===
if exist "%SRC%" (
    echo   Already cloned at %SRC%, pulling latest...
    cd /d "%SRC%"
    git pull
) else (
    cd /d "%PROJ%"
    git clone https://github.com/ProjectIgnis/WindBot.git WindBotSrc
    cd /d "%SRC%"
)

echo.
echo === Step 2: Copy SmartAI files ===
copy /Y "%PROJ%\WindBotSmartAI\CardDatabase.cs"  "%SRC%\Game\AI\CardDatabase.cs"
copy /Y "%PROJ%\WindBotSmartAI\SmartExecutor.cs" "%SRC%\Game\AI\Decks\SmartExecutor.cs"
echo   Done.

echo.
echo === Step 3: Reminder — manual edits needed ===
echo.
echo   Open %SRC%\Game\AI\DecksManager.cs
echo   and change:
echo       return new DefaultNoExecutor(ai, duel);
echo   to:
echo       return new SmartExecutor(ai, duel);
echo.
echo   Open %SRC%\Program.cs (or EntryPoint) and add:
echo       CardDatabase.LoadDirectory(^<path to expansions folder^>);
echo.
echo   See INSTALL.md for full details.

echo.
echo === Step 4: Build ===
cd /d "%SRC%"
dotnet build -c Release
IF %ERRORLEVEL% NEQ 0 (
    echo.
    echo   BUILD FAILED — check the errors above.
    pause
    exit /b 1
)

echo.
echo === Step 5: Deploy ===
for /D %%d in ("%SRC%\bin\Release\net*") do (
    if exist "%%d\ExecutorBase.dll" (
        echo   Deploying from %%d
        copy /Y "%%d\ExecutorBase.dll" "%PROJ%\WindBot\ExecutorBase.dll"
        if exist "%%d\WindBot.exe" copy /Y "%%d\WindBot.exe" "%PROJ%\WindBot\WindBot.exe"
    )
)

echo.
echo === All done! ===
echo   Launch EDOPro and the bot will now use SmartExecutor for any deck
echo   that does not have a hand-crafted executor.
pause
