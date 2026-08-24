@echo off
setlocal
dotnet run --project "%~dp0src\DogdouSpec.Cli\DogdouSpec.Cli.csproj" -c Debug -- %*
exit /b %ERRORLEVEL%
