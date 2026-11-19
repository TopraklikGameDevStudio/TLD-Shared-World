@echo off
setlocal EnableDelayedExpansion
title TLD Shared World - install / kurulum
echo.
echo  TLD Shared World (Ortak Dunya) - install / kurulum
echo  --------------------------------------------------
set "HEDEF="
if exist "C:\Program Files\Epic Games\TheLongDark\tld.exe" set "HEDEF=C:\Program Files\Epic Games\TheLongDark"
if not defined HEDEF if exist "C:\Program Files (x86)\Steam\steamapps\common\TheLongDark\tld.exe" set "HEDEF=C:\Program Files (x86)\Steam\steamapps\common\TheLongDark"
set "CEVAP=N"
if defined HEDEF (
  echo  Game found / Oyun bulundu: !HEDEF!
  set /p "CEVAP= Install here? / Buraya kurulsun mu? (Y/N - E/H): "
)
if /i "!CEVAP!"=="E" set "CEVAP=Y"
if /i not "!CEVAP!"=="Y" (
  echo.
  echo  Type the game folder, the one with tld.exe in it.
  echo  Oyunun klasorunu yaz, icinde tld.exe olan klasor.
  set /p "HEDEF= Folder / Klasor: "
)
if not exist "!HEDEF!\tld.exe" (
  echo.
  echo  ERROR: no tld.exe in "!HEDEF!". Nothing was installed.
  echo  HATA: "!HEDEF!" icinde tld.exe yok. Kurulum yapilmadi.
  pause
  exit /b 1
)
if not exist "!HEDEF!\MelonLoader" (
  echo.
  echo  MelonLoader is not installed in this folder: do step 1 of INSTALL.txt first.
  echo  Bu klasorde MelonLoader kurulu degil: once KURULUM.txt icindeki 1. adimi yap.
  pause
  exit /b 1
)
if not exist "!HEDEF!\Mods" mkdir "!HEDEF!\Mods"
if not exist "!HEDEF!\UserLibs" mkdir "!HEDEF!\UserLibs"
copy /y "%~dp0Mods\TLDCoop.dll" "!HEDEF!\Mods\TLDCoop.dll" >nul
if errorlevel 1 goto hata
for %%F in (LiteNetLib.dll NAudio.Core.dll NAudio.WinMM.dll) do (
  copy /y "%~dp0UserLibs\%%F" "!HEDEF!\UserLibs\%%F" >nul
  if errorlevel 1 goto hata
)
echo.
echo  Done: the mod is installed. Start the game; the MelonLoader window should list
echo  "TLD Shared World (Ortak Dunya)".
echo  Tamam: mod kuruldu. Oyunu ac; MelonLoader penceresinde
echo  "TLD Shared World (Ortak Dunya)" yazisini gormelisin.
pause
exit /b 0
:hata
echo.
echo  ERROR: the files could not be copied. Close the game, right-click this file,
echo  choose "Run as administrator" and try again.
echo  HATA: dosyalar kopyalanamadi. Oyunu kapat, bu dosyaya sag tiklayip
echo  "Yonetici olarak calistir" sec ve tekrar dene.
pause
exit /b 1
