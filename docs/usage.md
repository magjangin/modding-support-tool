# 사용법 가이드 (Usage Guide)

`Modding Support Tool`을 사용하는 다양한 방법(GUI, CLI, 배치 파일 드래그앤드롭, 독립 프로젝트 내보내기)을 안내합니다.

---

## 1. GUI 환경에서 사용하기

가장 직관적이고 시각적인 사용 방법입니다.

### 실행 방법
1. `ModdingSupportTool.exe`를 실행합니다 (또는 VS/VS Code에서 `dotnet run --project ModdingSupportTool` 실행).
2. 깔끔한 다크 테마의 창이 표시됩니다.

### 사용 순서
1. **입력 경로 지정**:
   - `[폴더 선택]` 또는 `[DLL 파일 선택]` 버튼을 눌러 게임 폴더나 어셈블리를 선택합니다.
   - 또는 상단의 **빠른 프리셋 버튼**(*Muse Dash*, *Sixtar Gate*, *A Short Hike*, *MiSide*)을 클릭하면 즉시 경로가 세팅됩니다.
2. **백엔드 자동 감지 확인**:
   - 경로를 지정하면 "엔진 판별" 배지에 `Il2Cpp` 또는 `Mono`와 함께 판별 근거가 표시됩니다.
   - 기본적으로 백엔드에 맞춰 엔진(SignatureDumper 또는 ilspycmd)이 자동 전환됩니다.
3. **타겟 어셈블리 및 출력 폴더 확인**:
   - 기본 대상: `Assembly-CSharp.dll, Assembly-CSharp-firstpass.dll`
   - 기본 출력: 프로젝트 내 `Decompiled/` 폴더
4. **[덤프 시작] 클릭**:
   - 하단 콘솔 터미널에 진행 상황이 실시간으로 출력되며 완료 시 탐색기로 결과물을 확인할 수 있습니다.

---

## 2. 배치 파일로 원클릭 / 드래그 앤 드롭 실행 ([`run_dump.bat`](file:///h:/source/repos/modding%20support%20tool/run_dump.bat))

GUI 창을 띄우지 않고 가장 빠르게 덤프를 진행하는 방법입니다.

### 방법 A: 게임 폴더나 DLL 파일을 드래그 앤 드롭
- 탐색기에서 원하는 **게임 설치 폴더** 또는 **`Assembly-CSharp.dll` 파일**을 마우스로 잡고 [`run_dump.bat`](file:///h:/source/repos/modding%20support%20tool/run_dump.bat) 위에 끌어다 놓습니다.
- 자동으로 콘솔 창이 뜨며 백엔드 판별 후 `Decompiled/` 폴더에 덤프를 완료합니다.

### 방법 B: 그냥 더블 클릭
- [`run_dump.bat`](file:///h:/source/repos/modding%20support%20tool/run_dump.bat)을 더블 클릭하면, 기본 설정된 후보 경로(예: `H:\muse dash hwa\...`)를 찾아 자동 덤프를 수행합니다.

---

## 3. CLI (명령줄) 모드로 사용하기

터미널이나 파워셸, 자동화 스크립트에서 호출할 수 있습니다.

```powershell
# 1. 기본 경로로 자동 덤프
.\ModdingSupportTool.exe --cli

# 2. 위치 기반 인자 사용 (입력경로, 출력폴더, 대상DLL목록)
.\ModdingSupportTool.exe "H:\muse dash hwa\MelonLoader\Il2CppAssemblies" ".\MyOutput" "Assembly-CSharp.dll"

# 3. 옵션 플래그 사용
.\ModdingSupportTool.exe -i "H:\steam\steamapps\common\A Short Hike" -o ".\Decompiled" -t "Assembly-CSharp.dll"
```

### 종료 코드 (Exit Code)
- `0`: 덤프 성공
- `1`: 입력 경로 오류 또는 인자 부족
- `2`: 사용자에 의한 취소 (`Ctrl + C`)
- `3`: 덤프 처리 중 예외 발생

---

## 4. 독립 실행형 SignatureDumper 소스 내보내기

모드 프로젝트와 완전히 분리된 가벼운 Standalone CLI 도구가 필요할 때 사용합니다.

1. GUI 화면 우측 상단의 `[📦 SignatureDumper 소스 내보내기]` 버튼을 누릅니다.
2. 소스를 저장할 빈 폴더를 선택합니다.
3. 지정한 위치에 아래와 같은 완벽한 C# 솔루션 소스가 생성됩니다:
   - `SignatureDumper.csproj` (`net472;net8.0` 멀티타깃)
   - `Program.cs` (독립 실행 진입점)
   - `SignatureDumper.cs` (정적 분석 코어)
4. 생성된 폴더에서 바로 `dotnet build` 또는 `dotnet run`으로 독립 실행할 수 있습니다.
