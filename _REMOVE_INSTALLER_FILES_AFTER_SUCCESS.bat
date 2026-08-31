@echo off
cd /d "%~dp0"
del /q "_INSTALL_CHASTER_ADDON.bat" 2>nul
del /q "_INSTALL_CHASTER_ADDON.ps1" 2>nul
del /q "_chaster-card.xaml.snippet" 2>nul
del /q "_REMOVE_INSTALLER_FILES_AFTER_SUCCESS.bat" 2>nul
