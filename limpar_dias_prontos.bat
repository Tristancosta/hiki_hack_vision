@echo off
rem Duplo clique neste arquivo para abrir o limpador de videos.
rem Precisa estar na mesma pasta que limpar_dias_prontos.ps1
title Limpar dias da pasta prontos
echo Informe o intervalo de dias a MANTER. Tudo fora dele sera apagado.
echo Formato dia/mes, por exemplo 26/09
echo.
set /p INICIO=Dia de inicio (dd/MM):
set /p FIM=Dia de fim (dd/MM):
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0limpar_dias_prontos.ps1" -Inicio "%INICIO%" -Fim "%FIM%"
echo.
pause
