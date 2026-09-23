@echo off
setlocal
chcp 65001 >nul

:: Modding Support Tool 자동 실행 및 덤프 스크립트
:: 사용법 1 (더블클릭): 기본 게임/어셈블리 경로를 자동 감지하여 덤프
:: 사용법 2 (드래그 앤 드롭): 폴더나 DLL 파일을 이 .bat 파일 위로 드래그하여 바로 덤프
:: 사용법 3 (명령줄): run_dump.bat [입력경로] [출력폴더] [타겟DLL목록]

set "SCRIPT_DIR=%~dp0"
if exist "%SCRIPT_DIR%ModdingSupportTool.exe" (
    set "TOOL_EXE=%SCRIPT_DIR%ModdingSupportTool.exe"
) else (
    set "TOOL_EXE=%SCRIPT_DIR%Publish\ModdingSupportTool.exe"
)

if not exist "%TOOL_EXE%" (
    echo [경고] 게시된 ModdingSupportTool.exe를 찾을 수 없습니다.
    if exist "%SCRIPT_DIR%publish.bat" (
        echo 프로젝트를 빌드/게시합니다...
        call "%SCRIPT_DIR%publish.bat"
    )
    if not exist "%TOOL_EXE%" (
        echo [오류] 실행 파일을 찾거나 빌드할 수 없습니다.
        pause
        exit /b 1
    )
)

set "INPUT_PATH=%~1"
set "OUTPUT_PATH=%~2"
set "TARGETS=%~3"

if "%OUTPUT_PATH%"=="" (
    set "OUTPUT_PATH=%SCRIPT_DIR%Decompiled"
)

echo ===================================================
echo   Modding Support Tool - 자동 덤프 배치 실행기
echo ===================================================

if "%INPUT_PATH%"=="" (
    echo [안내] 입력 경로가 지정되지 않았습니다. 기본 경로로 실행합니다.
    "%TOOL_EXE%" --cli
) else (
    echo [입력 경로] %INPUT_PATH%
    echo [출력 폴더] %OUTPUT_PATH%
    if not "%TARGETS%"=="" (
        echo [타겟 어셈블리] %TARGETS%
        "%TOOL_EXE%" "%INPUT_PATH%" "%OUTPUT_PATH%" "%TARGETS%"
    ) else (
        "%TOOL_EXE%" "%INPUT_PATH%" "%OUTPUT_PATH%"
    )
)

set "EXIT_CODE=%ERRORLEVEL%"
echo.
if %EXIT_CODE% equ 0 (
    echo [성공] 덤프 완료! 출력 폴더: %OUTPUT_PATH%
) else (
    echo [경고] 작업이 종료 코드 %EXIT_CODE% 로 끝났습니다.
)

echo.
echo 창을 닫으려면 아무 키나 누르세요...
pause >nul