@echo off
setlocal

echo == Building DogdouSpec (Debug) ==
dotnet build "%~dp0DogdouSpec.slnx" -c Debug
if %ERRORLEVEL% neq 0 (
    echo [ERROR] Build failed with exit code %ERRORLEVEL%
    exit /b %ERRORLEVEL%
)

echo == Running DogdouSpec Tests ==
dotnet test "%~dp0DogdouSpec.slnx" -c Debug --no-build
if %ERRORLEVEL% neq 0 (
    echo [ERROR] Tests failed with exit code %ERRORLEVEL%
    exit /b %ERRORLEVEL%
)

echo == Build and Tests Succeeded ==
exit /b 0
