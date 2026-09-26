# Quicklight 기술 명세

사용법과 UI/UX 기능은 [README.md](../README.md)에 있습니다. 이 문서는 구현, 빌드, 릴리스, 보안에 관한 내용입니다.

## 구조

```
src/Quicklight.Core    검색 엔진 (UI 없음)
  Matching/            퍼지 매칭, 초성, 한/영 자판 변환, 자모 순서 오타 보정
  Everything/          Everything IPC 클라이언트
  Shell/               앱 목록(자체 수집, 묶기, SQLite), 모든 앱 그리드 정리, 셸 아이콘, 실행
  Providers/           앱, 파일, 계산기, 단위, 날짜, URL, 경로, 설정/명령, 웹 검색(키워드), 열린 창, 프로세스, 스니펫, 사용자 명령
  SearchEngine.cs      병렬 조회, 점수 통합, 학습, 중복 제거
src/Quicklight         WPF 런처 (단축키, 창, 애니메이션, 트레이, 설정 창). Program.cs 가 --mcp 면 MCP 서버로 실행
src/Quicklight.Mcp     MCP 서버 라이브러리
tests/Quicklight.Tests xUnit 테스트
```

## 구현 메모

- **검색**: 모든 공급자(앱, 파일, 설정, 명령, 계산기, URL, 경로, 웹 검색)를 병렬로 조회하고 하나의 점수 체계로 정렬합니다. 런처는 검색을 스레드 풀에서 돌리고(`SearchOffUiAsync`), 결과 카드는 `Dispatcher.Yield(Background)` 로 이미 들어온 키 입력을 먼저 처리한 뒤에 그립니다. 그사이 새 검색어가 오면 그리지 않습니다. 공급자는 UI 스레드에 기대지 않아야 하고, 클립보드 기록(WinRT)만 `ClipboardHistoryProvider` 가 UI 스레드로 넘겨 읽습니다. 아이콘 콜백도 `Background` 우선순위라 키 입력보다 늦게 처리됩니다. 모든 앱 그리드(`app` 등)는 타일 배치가 무거워 `Background` 우선순위로 미뤄 띄우고(그사이 검색어가 바뀌면 띄우지 않음), 타일은 컬렉션을 통째로 바꿔 한 번에 넣습니다.
- **상세 설정**: `advancedSearch` 가 꺼지면 검색은 `QuicklightSettings` 의 `ResultLimit`, `FileResultLimit`, `FolderResultLimit`, `DemotedPathsInEffect`, `ExcludedPathsInEffect` 를 통해 기본값(`new QuicklightSettings()`)을 읽고, 저장된 값은 건드리지 않습니다. 설정 창의 "기본값으로 되돌리기"는 스니펫과 사용자 명령을 뺀 폼만 기본값으로 채우고, 저장해야 적용됩니다.
- **배경**: 흐리게 비침은 흐린 화면 캡처 위에 반투명 틴트(`PanelBrush`, 어두운 테마 50%)를, 불투명은 캡처를 숨기고 완전 불투명 `SolidPanelBrush` 를 씁니다. 틴트가 70%일 때는 어두운 화면 앞에서 두 모드가 거의 같아 보였습니다.
- **오타 보정**: `Typo` 가 네 글자 이상의 영어 검색어를 이름의 각 단어(와 검색어 길이 ±1의 앞부분, +0.25)와 키보드 가중 편집 거리로 비교합니다. QWERTY 자리(행 어긋남 포함)에서 이웃한 키로 바꾸기 0.5, 두 글자 자리 바꿈 0.6, 그 밖의 바꾸기/빠짐/더함 1입니다. 거리가 1(다섯 글자 이하) 또는 1.5 이하면 68 - 12×거리 점을 줍니다. 글자 단계가 62 미만일 때만 계산하고, 둘 다 0이면 한글 ↔ 영어 소리 비교로 넘어갑니다.
- **결과 스크롤**: 결과를 새로 그린 직후의 `ScrollIntoView` 는 줄 배치 전이라 효과가 없어서, `Loaded` 우선순위로 한 번 더 선택 항목을 보이게 합니다.
- **한글 ↔ 영어**: 두 가지입니다. (1) 시작 메뉴 바로 가기 이름은 `SHGetFileInfo(SHGFI_DISPLAYNAME)` 로 Windows가 보여 주는 이름(지역화된 "파일 탐색기")을 쓰고, 파일 이름("File Explorer")은 `AppEntry.Aliases` 로 남겨 함께 검색합니다(`apps.db` 스키마 2, `aliases` 열). (2) `Phonetic` 이 한글 검색어와 이름의 라틴 글자(또는 반대)를 자음 소리(P T K S L M N H, 모음 제외, 붙은 같은 소리는 하나)로 줄여 비교합니다. 애플뮤직 = PLMSK = apple music. 전체가 같으면 74, 이름 앞부분이면 64(두 소리면 55), 뒤 단어의 앞부분이면 58점으로, 글자로 맞는 단계가 모두 0일 때만 씁니다(`FuzzyMatcher.Score`). 영어 철자 규칙은 대략이라(ph, th, ch/chr, tion, 자음 앞 r, talk의 l 등만) 틀리는 경우가 있습니다.
- **모든 앱 그리드 바로 이동**: 검색어가 모든 앱 키워드면(`LockToGrid`) 검색창을 읽기 전용으로 하고 IME를 끕니다. 글자 입력(`PreviewTextInput`)은 최대 10자 버퍼(`_typeAhead`)로 가고, 1초 입력이 없으면 비웁니다. 한 글자는 그 구역(`AppCatalog.SectionOf`, 없으면 한글 자판으로 읽은 초성 구역)의 첫 앱, 여러 글자는 `FuzzyMatcher` 점수(자판 변환한 한글, 별칭 포함)가 가장 높은 앱을 고릅니다(같으면 앞의 앱). 버퍼는 검색창 오른쪽(`TypeAhead`)에 보입니다. Backspace는 버퍼를 먼저 지우고, 비었으면 잠금을 풀어 검색어를 지웁니다(그리드에서 나감). Esc도 버퍼부터 비웁니다. 붙여넣거나 최근 검색어로 불러온 `app 검색어` 는 여전히 `AppCatalog.ScopedQuery` 로 앱만 검색합니다.
- **미리보기 표시**: `ResultItem.HasPreview`(`CanPreview`: 앱, 파일, 폴더, 경로, 클립보드 항목)면 결과 줄 오른쪽에 셰브런(`E76C`)을 보여 줍니다.
- **최근 검색어**: `RecentQueries` 가 결과를 실행, 복사, 붙여넣기, 관리자 실행, 폴더에서 보기 한 검색어를 최신순으로 10개까지 `recent.json` 에 씁니다(같은 검색어는 맨 앞으로). 런처는 닫힐 때 검색창을 비웁니다. 검색창이 비었거나 글자 전체가 선택되어 있으면(`BrowsingRecent`) ↑ ↓ 는 최근 검색어로 갑니다. 상태를 따로 두지 않고, 지금 글자가 목록의 몇 번째인지로 위치를 정합니다(목록에 없는 글자에서 ↑ 는 가장 최근 것, ↓ 는 아무 일도 하지 않음). 불러온 검색어는 전체 선택해서 넣고, ↓ 로 가장 최신을 지나면 빈 칸이 됩니다. 캐럿만 있거나 일부만 선택되면 ↑ ↓ 는 결과를 고르고, Ctrl+A나 드래그로 전체를 다시 선택하면 최근 검색어로 돌아갑니다. 파일 결과 중 목록에 나온 앱의 부분(실행 파일, 바탕 화면 바로 가기, 설치 파일)은 `AppProvider.OwnerOf` 로 찾아 빼서 앱 한 줄만 남깁니다.
- **사용 기록**: `history.json` 에는 검색어를 쓰지 않습니다. 항목은 결과 키, 시각, 친 글자 수(`Typed`), 그때 검색어가 결과 제목과 맞은 정도(`Strength`, FuzzyMatcher 점수, 한/영 변환과 자모 순서 보정 중 최고값)입니다. 학습 가산은 지금 검색어가 그 결과와 기록 당시보다 10점 넘게 덜 맞으면 주지 않고(Chrome을 `ch` 로 골랐다고 `cm` 에서 올리지 않음), 글자 수가 같으면 2배, 적으면 비율만큼, 많으면 1배입니다. 예전 형식(`Query` 필드)은 읽을 때 `Typed = Query.Length`, `Strength = 0` 으로 바꾸고 곧바로 다시 써서 검색어를 지웁니다. 로그에도 검색어를 남기지 않습니다.
- **앱 목록**: Windows "모든 앱"(`shell:AppsFolder`)을 쓰지 않고 직접 모읍니다 (`ShellApps.ScanAsync`, STA 스레드, 이 PC에서 약 370개에 2.3초). 출처는 시작 메뉴(공용과 사용자, 하위 폴더 포함)와 바탕 화면(공용과 사용자, 최상위, exe를 가리키는 것만)의 `.lnk`(`IShellLinkW` 로 대상과 인자를 읽음), `Uninstall` 레지스트리(HKLM 64/32비트, HKCU. `SystemComponent=1` 과 `ParentKeyName` 이 있는 것은 뺌), 다운로드 폴더 최상위의 설치 파일(`.msi`, 이름에 setup/install/설치가 들어간 `.exe`), 스토어 앱(`PackageManager.FindPackagesForUser` 의 `GetAppListEntries`. Windows 10 2004 미만에서는 뺌)입니다.
- **앱 묶기** (`ShellApps.Group`): 바로 가기 이름으로 역할을 나눕니다. uninstall/제거는 제거 프로그램, readme/help/website 등은 버림, install/setup/설치는 설치 파일, updater/업데이터/error report/recovery/repair 등은 도구(`AppPartKind.Tool`), 나머지는 앱입니다. 대상 exe와 인자가 같거나 이름(소문자, 글자와 숫자만)이 같으면 한 앱입니다. 버전이 다르면 다른 앱입니다 ("Python 3.11" ≠ "Python 3.12"). 제거, 설치, 도구 바로 가기와 다운로드의 설치 파일은 이름의 낱말(설치와 도구 단어, app/tool 같은 흔한 낱말, 아키텍처, 버전을 뺌. 파일 이름은 camelCase도 나눔)이 앱 이름과 같거나 앱 이름의 낱말 하나와 같으면, 또는 바로 가기 대상 폴더 아래에 앱의 exe가 있으면 그 앱에 붙습니다. 아무 앱에도 붙지 않은 설치나 도구 바로 가기는 따로 앱이 됩니다 ("Visual Studio Installer"). 레지스트리 항목은 버전까지 같은 이름이거나, `DisplayIcon` 이 앱의 exe이거나, 앱의 exe가 `InstallLocation` 이나 제거 프로그램 폴더 아래에 있으면 붙고(인자가 있는 바로 가기는 제외. Chrome 웹 앱이 Chrome의 제거 프로그램을 받지 않도록), 제거 명령, 게시자, 버전, 설치 위치를 줍니다. Program Files, Windows, AppData 같은 공용 폴더는 "아래에 있음" 판단에 쓰지 않습니다. 어느 앱에도 붙지 않은 레지스트리 항목은 `DisplayIcon` 이 디스크에 있는 프로그램이면(설치/제거 이름, `unins*`, `*inst`, Windows 폴더, `Package Cache`, `\Installer\`, `\DIFX\` 드라이버 패키지 제외) 그 exe로 앱이 됩니다. 바로 가기를 지운 Chrome, Edge 같은 경우입니다. 앱을 exe 파일 이름으로도 찾지만(`code` → Visual Studio Code), 인자가 있는 바로 가기의 exe는 남의 프로그램이라(Chrome 웹 앱의 `chrome_proxy.exe`) 쓰지 않습니다. 레지스트리 명령줄로 된 제거 줄은 exe 이름 대신 "앱 이름 제거"로 표시합니다.
- **Enter 우선순위**: `AppEntry.Launch` 는 스토어 앱 → 바로 가기 → 실행 파일(바로 가기의 인자로) → 설치 파일 순으로, 지금 디스크에 있는 첫 번째 것입니다. 도구와 제거 프로그램은 앱의 Enter로 실행하지 않습니다. 앱 미리보기의 부분 줄(`AppProvider.PartResult`)이나 우클릭 메뉴에서 실행하고, 제거 명령줄은 `ShellLauncher.SplitCommandLine` 으로 파일과 인자로 나눕니다. 미리보기에서 제거 줄은 `RequiresConfirmation` 이라 Enter 두 번이 필요합니다.
- **앱 DB**: `%LOCALAPPDATA%\Quicklight\apps.db` (Microsoft.Data.Sqlite). `app(id, name, publisher, version, location)`, `part(app, kind, target, args)` 두 표이고 `PRAGMA user_version` 이 다르면 표를 다시 만듭니다. 시작하면 먼저 DB를 읽어 바로 검색에 쓰고, 다시 모은 목록으로 한 트랜잭션 안에서 통째로 바꿉니다. 목록이 비어 있을 때만 진행 표시줄을 띄웁니다. 풀링은 끕니다(파일을 붙잡지 않도록).- **Everything**: SDK DLL 없이 Everything IPC(WM_COPYDATA)를 직접 구현했습니다. 빠른 이름 앞부분 검색 결과를 먼저 보여 주고 전체 검색 결과로 이어서 채웁니다. Everything 조회는 입력이 200ms 멈춘 뒤에 보냅니다. 색인이 큰 PC에서는 조회 하나가 약 1초씩 Everything의 UI 스레드를 잡아서, 글자마다 조회하면 Windows가 Everything을 응답 없음(hung)으로 봤습니다. 상태줄의 "Everything 응답 없음"은 런처가 열려 있는 동안 1초마다 도는 `GetDbState` 폴링으로 정합니다. 실행 중이 아니면 바로, 응답이 없으면 5번 연속(약 5초)일 때 띄우고, 응답하면 바로 지웁니다. 다른 메시지가 떠 있으면 건드리지 않습니다. 폴더는 따로 조회해서, 같은 이름의 파일이나 앱이 많아도 이름이 맞는 폴더가 결과에 남습니다. 폴더에 점수 가산은 없어서, 폴더와 파일 구역의 순서는 각 구역의 최고 점수로 정해집니다. Alt+Shift+. 은 Everything 트레이 메뉴 명령(EVERYTHING_IPC_ID_TRAY_OPTIONS = 40005)으로 옵션 창을 엽니다.
- **내장 Everything 설치 위치**: `C:\Program Files\Quicklight\Everything`. NTFS 색인을 읽는 Everything 서비스는 관리자 권한으로 돌기 때문에, 서비스 파일을 사용자 폴더에 두면 다른 프로그램이 바꿔치기해 관리자 권한을 얻을 수 있습니다. 그래서 설치에 UAC가 한 번 필요합니다.
- **단축키**: `RegisterHotKey` 로 등록하고, 다른 프로그램이 이미 등록했다면 저수준 키보드 후크로 대신 가로챕니다.
- **트레이 아이콘**: Windows 11은 새 아이콘을 숨김 영역에 넣습니다. `HKCU\Control Panel\NotifyIconSettings` 의 `IsPromoted` 를 처음 한 번 켜서 작업 표시줄로 꺼내고, 이후에는 사용자의 선택을 따릅니다.
- **애니메이션**: 모니터 주사율에 맞춰 120/144/240Hz로 재생합니다. 창은 레이어드 창(`AllowsTransparency`) 대신 DWM 프레임 확장(`NativeUi.MakeGpuTransparent`)으로 투명하게 합니다. 레이어드 창은 매 프레임을 CPU로 다시 읽어 와서 프레임이 절반으로 떨어졌습니다. 블러는 열 때만, 패널 내용(`Body`)에만 겁니다. 닫을 때는 페이드만 합니다. 선명한 글자가 블러된 캐시 비트맵으로 바뀌는 순간 한 프레임 지글거렸습니다. 크기 변화(scale)는 쓰지 않습니다. 캐시된 글자(placeholder, 돋보기)를 픽셀 이하 단위로 다시 샘플링하면 hinting 과 상관없이 지글거렸습니다. 창은 화면 아래 끝까지 닿는 고정 크기이고, 검색 알약(`BarPanel`)과 결과 카드(`ListSurface`)는 따로 그림자, 배경 스냅숏, 틴트, 테두리를 가집니다. 스냅숏은 한 장을 찍어 카드 쪽은 `CardOffset` 만큼 올려 씁니다. 카드 높이만 내용(`Body`, Canvas 안에서 제 높이를 유지) 높이로 애니메이션하고, 0에서 나타날 때는 페이드인과 함께 내용이 흐림에서 선명해지고, 0이 되면 페이드아웃합니다. 카드 모서리는 검색 알약과 같은 31입니다. 창 크기를 매 프레임 바꾸지 않기 위해서입니다. 패널 밖의 빈 곳을 누르면 런처를 닫습니다.
- **흐린 배경**: 창을 여는 순간의 화면을 캡처해 흐리게 한 것입니다.
- **환전 숫자 글자**: `CurrencyParser.DigitsForWords` 가 파싱 전에 일~구, 십백천만억으로 된 글자 묶음을 숫자로 바꿉니다(`오백` → `5백`, 단위로 시작하면 1을 붙여 `천오백` → `1천5백`). 단위가 없는 묶음(`이야` 의 이, `오달러` 의 오)은 글자로 둡니다. 금액 패턴은 끝에 단위 없는 숫자(`2십5`)도 받습니다.
- **아이콘**: `IconLoader` 는 셸에 256px(jumbo) 아이콘을 받아 `RenderTargetBitmap` 과 `BitmapScalingMode.HighQuality`(Fant)로 96px로 줄여 캐시합니다. 64px를 달라고 하면 셸이 48px 그림을 키워 주는 경우가 있어 흐렸습니다. 96px는 44 DIP 타일을 200% 배율까지 선명하게 채웁니다. 큰 그림이 없는 옛 아이콘은 셸이 작은 그림을 256px 가운데에 불투명한 검은 바탕(또는 투명)으로 넣어 주므로, 셸이 가장자리에서 5px쯤 안쪽까지 흐린 테두리(알파 0x1A~0x33)를 그리므로, 가장자리 1/24를 빼고 그 안쪽 색을 바탕으로 봅니다(투명 바탕이면 알파 32 이하도 바탕). 바탕과 다른 부분이 60%가 안 되면 그 크기로 셸에 다시 받아(`ShellIcons.GetLargest`/`NativeSize`, 예: 48px, 알파 있음) 96px로 확대합니다. 이 PC에서는 46개가 해당합니다. 그 밖에 그려진 부분(알파 > 16)이 캔버스의 70%가 안 되는 아이콘(큰 그림 없이 32/48px만 가운데에 놓인 옛 프로그램 등)은 그 부분을 12% 여백을 두고 잘라 96px로 확대합니다. 흐려도 다른 아이콘과 크기가 맞습니다. 모든 앱 화면의 카드는 940 DIP(클립보드 화면 840, 평소 720)이고 타일 패널은 900 DIP(한 줄에 100 DIP 타일 9개)입니다. 창은 그만큼 넓습니다(988). 카드와 검색창 모서리는 모두 반지름 31입니다.
- **환율**: [open.er-api.com](https://www.exchangerate-api.com) 의 하루 단위 기준 환율(약 166개 통화)을 쓰고, 실패하면 유럽중앙은행 기준의 [Frankfurter](https://frankfurter.dev) 로 넘어갑니다. `%LOCALAPPDATA%\Quicklight\rates.json` 에 저장하고 6시간마다 새로 받습니다.
- **대상 프레임워크**: `Directory.Build.props` 에서 `net8.0-windows10.0.19041.0` (최소 `SupportedOSPlatformVersion` 10.0.17763). 클립보드 기록에 쓰는 WinRT(`Windows.ApplicationModel.DataTransfer.Clipboard`) 프로젝션이 들어와 exe가 커집니다.
- **오타 보정**: 검색어를 한/영 변환한 것과 `Hangul.FixJamoOrder` 로 고친 것(자음 앞에 친 홀모음을 자음과 맞바꾸고 다시 조합, `ㅏㅈ` → `자`)을 `IsAlternate` 문맥으로 함께 조회합니다. 대체 문맥은 파일 검색을 하지 않고 더 강한 일치만 받습니다.
- **클립보드 기록**: `ClipboardHistoryProvider`(앱 프로젝트)가 `GetHistoryItemsAsync` 로 Win+V와 같은 목록을 읽습니다. 텍스트와 이미지만 보여 주고, Enter는 `SetHistoryItemAsContent` 후 런처를 열기 직전의 전경 창으로 돌아가 Ctrl+V를 보냅니다(`ActionType.Paste`, 스니펫도 같음). WinRT는 전경 앱에만 기록을 주므로 MCP에는 없습니다.
- **열린 창**: `EnumWindows` 로 Alt+Tab에 나오는 창(보이는 소유자 없는 창, 도구 창과 DWM cloaked 창 제외)을 찾습니다. 테스트에서는 `includeWindows: false` 로 빠집니다. 가상 데스크톱 전환은 공개 API가 없어 넣지 않았습니다.
- **미리보기**: Space 키다운을 삼키고 1초 타이머를 돌립니다. 150ms 뒤부터 선택한 줄이 늘어나고(Scale/Translate), 1초 전에 떼면 `ElasticEase` 로 되돌아가며 띄어쓰기를 직접 넣습니다. 1초가 되면 `BackEase` 로 미리보기 패널이 결과 목록 자리에 열립니다. 이미지는 `BitmapImage`, 텍스트는 앞 64KB(UTF-8, 실패하면 CP949), 그 밖은 STA 스레드에서 셸 썸네일(`ShellIcons.Get(thumbnail: true)`)입니다. 앱(`SearchResult.App`)은 파일을 읽지 않고 `PreviewParts` 목록에 `AppEntry` 의 부분을 결과 줄과 같은 모양(아이콘, 이름, 종류와 경로)으로 보여 줍니다. 앱의 Enter로 실행되는 것이 맨 위이고, ↑ ↓, 마우스 휠, Enter는 이 목록에 갑니다 (텍스트 미리보기에서 휠은 스크롤). 모든 앱 그리드에서도 열리며(`_gridShown` 이 그리드 상태를 유지), 닫으면 그리드로 돌아갑니다. IME가 조합 중인 Space(`Key.ImeProcessed`)는 건드리지 않습니다.
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

`open` 도구는 문서, 폴더, http(s) URL, `ms-settings:`, 색인된 앱만 허용합니다. 색인된 앱은 그 앱의 스토어 앱, 바로 가기, 실행 파일 경로를 말하고, 설치 파일과 제거 프로그램은 들어가지 않습니다. 그 밖의 실행 파일, 스크립트, 바로가기, 네트워크 경로는 거부합니다 (AI가 속아서 프로그램을 실행하지 않도록).
