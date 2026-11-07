@echo off
title 微信输入连接器
cd /d "%~dp0"
start "" "%~dp0artifacts\status-receipts\Connector.Probe.exe" --data-dir "%~dp0.local"
exit
