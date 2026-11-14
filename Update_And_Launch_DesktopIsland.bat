@echo off
chcp 65001 >nul
powershell -NoProfile -Command "Start-Process powershell -ArgumentList '-ExecutionPolicy','Bypass','-File','E:\Applications\DesktopIsland\Update_And_Launch_DesktopIsland.ps1' -Verb RunAs"
