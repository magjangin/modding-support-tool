@echo off
setlocal
chcp 65001 >nul

:: ModdingSupportTool 릴리즈 배포(Publish) 스크립트

set "SCRIPT_DIR=%~dp0"
set "PROJ_PATH=%SCRIPT_DIR%ModdingSupportTool\ModdingSupportTool.csproj"
set "PUBLISH_DIR=%SCRIPT_DIR%Publish"

echo ===================================================
echo   Modding Support Tool - 릴리즈 게시(Publish) 시작
echo ===================================================
echo 프로젝트: %PROJ_PATH%
echo 출력위치: %PUBLISH_DIR%
echo.

:: 기존 Publish 폴더 정리
if exist "%PUBLISH_DIR%" (
    echo 기존 Publish 폴더 정리 중...
    rd /s /q "%PUBLISH_DIR%"
)

dotnet publish "%PROJ_PATH%" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o "%PUBLISH_DIR%"

if %ERRORLEVEL% equ 0 (
    echo.
    echo 실행 배치 파일 복사 및 정리 중...
    if exist "%SCRIPT_DIR%run_dump.bat" (
        copy /y "%SCRIPT_DIR%run_dump.bat" "%PUBLISH_DIR%\" >nul
    )
    :: 불필요한 네이티브 PDB 파일 삭제
    del /f /q "%PUBLISH_DIR%\*.pdb" >nul 2>&1

    echo.
    echo [성공] %PUBLISH_DIR% 에 성공적으로 게시되었습니다!
    echo ModdingSupportTool.exe 및 run_dump.bat 배포 완료.
) else (
    echo.
    echo [오류] 게시 도중 문제가 발생했습니다.
)

echo.
pause