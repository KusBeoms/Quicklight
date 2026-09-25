# Quicklight 기술 명세

사용법과 UI/UX 기능은 [README.md](../README.md)에 있습니다. 이 문서는 구현, 빌드, 릴리스, 보안에 관한 내용입니다.

## 구조

```
src/Quicklight.Core    검색 엔진 (UI 없음)
  Matching/            퍼지 매칭, 초성, 한/영 자판 변환, 자모 순서 오타 보정
  Everything/          Everything IPC 클라이언트
  Shell/               앱 목록(shell:AppsFolder), 모든 앱 그리드 정리, 셸 아이콘, 실행
  Providers/           앱, 파일, 계산기, 단위, 날짜, URL, 경로, 설정/명령, 웹 검색(키워드), 열린 창, 프로세스, 스니펫, 사용자 명령
  SearchEngine.cs      병렬 조회, 점수 통합, 학습, 중복 제거
src/Quicklight         WPF 런처 (단축키, 창, 애니메이션, 트레이). Program.cs 가 --mcp 면 MCP 서버로 실행
src/Quicklight.Mcp     MCP 서버 라이브러리
tests/Quicklight.Tests xUnit 테스트
```

## 구현 메모

- **검색**: 모든 공급자(앱, 파일, 설정, 명령, 계산기, URL, 경로, 웹 검색)를 병렬로 조회하고 하나의 점수 체계로 정렬합니다. 고른 결과를 `history.json` 에 기억해서 같은 검색어나 그 앞부분을 다시 치면 위로 올립니다.
- **Everything**: SDK DLL 없이 Everything IPC(WM_COPYDATA)를 직접 구현했습니다. 빠른 이름 앞부분 검색 결과를 먼저 보여 주고 전체 검색 결과로 이어서 채웁니다. 폴더는 따로 조회해서, 같은 이름의 파일이나 앱이 많아도 이름이 맞는 폴더가 결과에 남습니다. 폴더에 점수 가산은 없어서, 폴더와 파일 구역의 순서는 각 구역의 최고 점수로 정해집니다. Alt+. 은 Everything 트레이 메뉴 명령(EVERYTHING_IPC_ID_TRAY_OPTIONS = 40005)으로 옵션 창을 엽니다.
- **내장 Everything 설치 위치**: `C:\Program Files\Quicklight\Everything`. NTFS 색인을 읽는 Everything 서비스는 관리자 권한으로 돌기 때문에, 서비스 파일을 사용자 폴더에 두면 다른 프로그램이 바꿔치기해 관리자 권한을 얻을 수 있습니다. 그래서 설치에 UAC가 한 번 필요합니다.
- **단축키**: `RegisterHotKey` 로 등록하고, 다른 프로그램이 이미 등록했다면 저수준 키보드 후크로 대신 가로챕니다.
- **트레이 아이콘**: Windows 11은 새 아이콘을 숨김 영역에 넣습니다. `HKCU\Control Panel\NotifyIconSettings` 의 `IsPromoted` 를 처음 한 번 켜서 작업 표시줄로 꺼내고, 이후에는 사용자의 선택을 따릅니다.
- **애니메이션**: 모니터 주사율에 맞춰 120/144/240Hz로 재생합니다. 창은 레이어드 창(`AllowsTransparency`) 대신 DWM 프레임 확장(`NativeUi.MakeGpuTransparent`)으로 투명하게 합니다. 레이어드 창은 매 프레임을 CPU로 다시 읽어 와서 프레임이 절반으로 떨어졌습니다. 블러는 패널 내용(`Body`)에만 걸고, 애니메이션 중에는 `TextHintingMode.Animated` 로 글자가 픽셀 격자에 맞춰 흔들리지 않게 합니다.
- **흐린 배경**: 창을 여는 순간의 화면을 캡처해 흐리게 한 것입니다.
- **환율**: [open.er-api.com](https://www.exchangerate-api.com) 의 하루 단위 기준 환율(약 166개 통화)을 쓰고, 실패하면 유럽중앙은행 기준의 [Frankfurter](https://frankfurter.dev) 로 넘어갑니다. `%LOCALAPPDATA%\Quicklight\rates.json` 에 저장하고 6시간마다 새로 받습니다.
- **대상 프레임워크**: `Directory.Build.props` 에서 `net8.0-windows10.0.19041.0` (최소 `SupportedOSPlatformVersion` 10.0.17763). 클립보드 기록에 쓰는 WinRT(`Windows.ApplicationModel.DataTransfer.Clipboard`) 프로젝션이 들어와 exe가 커집니다.
- **오타 보정**: 검색어를 한/영 변환한 것과 `Hangul.FixJamoOrder` 로 고친 것(자음 앞에 친 홀모음을 자음과 맞바꾸고 다시 조합, `ㅏㅈ` → `자`)을 `IsAlternate` 문맥으로 함께 조회합니다. 대체 문맥은 파일 검색을 하지 않고 더 강한 일치만 받습니다.
- **클립보드 기록**: `ClipboardHistoryProvider`(앱 프로젝트)가 `GetHistoryItemsAsync` 로 Win+V와 같은 목록을 읽습니다. 텍스트와 이미지만 보여 주고, Enter는 `SetHistoryItemAsContent` 후 런처를 열기 직전의 전경 창으로 돌아가 Ctrl+V를 보냅니다(`ActionType.Paste`, 스니펫도 같음). WinRT는 전경 앱에만 기록을 주므로 MCP에는 없습니다.
- **열린 창**: `EnumWindows` 로 Alt+Tab에 나오는 창(보이는 소유자 없는 창, 도구 창과 DWM cloaked 창 제외)을 찾습니다. 테스트에서는 `includeWindows: false` 로 빠집니다. 가상 데스크톱 전환은 공개 API가 없어 넣지 않았습니다.
- **미리보기**: Space 키다운을 삼키고 1초 타이머를 돌립니다. 150ms 뒤부터 선택한 줄이 늘어나고(Scale/Translate), 1초 전에 떼면 `ElasticEase` 로 되돌아가며 띄어쓰기를 직접 넣습니다. 1초가 되면 `BackEase` 로 미리보기 패널이 결과 목록 자리에 열립니다. 이미지는 `BitmapImage`, 텍스트는 앞 64KB(UTF-8, 실패하면 CP949), 그 밖은 STA 스레드에서 셸 썸네일(`ShellIcons.Get(thumbnail: true)`)입니다. IME가 조합 중인 Space(`Key.ImeProcessed`)는 건드리지 않습니다.
- **빌드 정보**: `Quicklight.csproj` 의 `GenerateBuildInfo` 타깃이 `obj\...\BuildInfo.g.cs` 에 버전과 빌드 시각(yyMMddHHmm)을 상수로 넣습니다. 트레이 메뉴에 표시됩니다. 결정적 빌드라 PE 타임스탬프는 쓸 수 없고, WPF의 wpftmp 컴파일 단계에서도 보이도록 `CoreCompile` 앞에 걸려 있습니다.

## 빌드

```bat
dotnet build Quicklight.sln
dotnet test tests\Quicklight.Tests
src\Quicklight\bin\Debug\net8.0-windows10.0.19041.0\Quicklight.exe --show     :: 바로 창 띄우기
src\Quicklight\bin\Debug\net8.0-windows10.0.19041.0\Quicklight.exe --pinned   :: 포커스를 잃어도 닫히지 않음 (디버깅용)
```

배포용 빌드는 항상 `publish.bat` 을 씁니다. 결과물은 .NET 런타임과 Everything(voidtools, MIT 라이선스)이 들어 있는 `Quicklight.exe` 하나입니다 (약 71MB).

```bat
publish.bat 0.2.0                                      :: 테스트 후 버전 0.2.0 으로 dist\Quicklight.exe 를 만들고, 서명해서 dist\Quicklight_v0.2.0.zip 으로 묶음
powershell -ExecutionPolicy Bypass -File install.ps1   :: %LOCALAPPDATA%\Programs\Quicklight 에 설치하고 실행
```

## 업데이트

확인 주기: 시작 30초 뒤와 6시간마다 GitHub 최신 릴리스(`updateRepository`)를 확인합니다. 설치는 다음 순서로 진행됩니다.

1. 릴리스의 `Quicklight_v0.2.0.zip` 같은 zip을 받습니다.
2. zip 안의 `Quicklight.exe` 가 **릴리스 키로 서명되었는지** `Quicklight.exe.sig` 로 확인합니다. 서명이 없거나 맞지 않으면 설치하지 않습니다. GitHub 계정이 탈취되어도 서명 키 없이는 업데이트를 퍼뜨릴 수 없습니다.
3. 서명된 exe 안의 버전이 지금 버전보다 새로운지 확인합니다. 태그만 바꿔 예전 빌드를 올리는 식의 버전 되돌리기도 막습니다.
4. 실행 중인 exe와 바꾸고 새 버전을 실행합니다. 새 버전은 이전 파일을 지웁니다. 새 버전이 실행되지 않으면 원래 파일로 되돌립니다.

### 릴리스 올리는 법

1. `publish.bat` 으로 패키징합니다 (버전을 생략하면 `Directory.Build.props` 의 버전). 버전 번호를 exe에 기록하고, `Quicklight.exe.sig` 를 만들어 둘을 `dist\Quicklight_v0.2.0.zip` 으로 묶습니다.
2. GitHub에서 태그 `v0.2.0` 으로 릴리스를 만들고 `dist\Quicklight_v0.2.0.zip` 을 첨부합니다. 본문에는 `RELEASE.md` 의 해당 버전 절을 붙여 넣습니다. 파일 이름은 `Quicklight_v버전.zip` 형식이어야 합니다 (`Quicklight.zip` 도 받습니다). 초안(draft)과 사전 릴리스(pre-release)는 업데이트 대상에서 빠집니다.

### 서명 키

- 비밀 키는 `%USERPROFILE%\.quicklight\update-signing-key.pem` 에 있습니다. 이 PC의 현재 사용자만 읽을 수 있고, 저장소에는 들어가지 않습니다. 다른 곳의 키를 쓰려면 환경 변수 `QUICKLIGHT_SIGNING_KEY` 에 경로를 지정합니다.
- **이 파일을 안전한 곳에 백업하십시오.** 잃어버리면 이미 설치된 Quicklight들이 이후 업데이트를 받지 못합니다. 새 키를 만들면 새 공개 키가 들어간 버전을 사용자가 직접 한 번 설치해야 합니다.
- 공개 키는 `src\Quicklight.Core\Update\UpdateSignature.cs` 에 있습니다. 키를 새로 만들려면 `tools\new-signing-key.ps1` 을 실행하고, 출력된 공개 키를 이 파일에 넣습니다.

## MCP 서버

`Quicklight.exe --mcp` 는 stdio MCP 서버입니다 (프로토콜 2025-06-18, 2025-03-26, 2024-11-05). 런처와 같은 exe이며, 런처가 켜져 있는지와 관계없이 따로 동작합니다. 각 도구의 텍스트 응답은 런처 화면을 마크다운으로 옮긴 형태이고, 같은 데이터를 `structuredContent` JSON으로도 함께 보냅니다.

`open` 도구는 문서, 폴더, http(s) URL, `ms-settings:`, 색인된 앱만 허용합니다. 실행 파일, 스크립트, 바로가기, 네트워크 경로는 거부합니다 (AI가 속아서 프로그램을 실행하지 않도록).
