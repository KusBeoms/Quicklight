# Quicklight

Windows용 Spotlight 스타일 런처입니다. **Alt+Space** 를 누르면 화면 가운데에 검색창이 뜹니다. PowerToys Run을 대신하도록 만들었습니다.

구현, 빌드, 릴리스, 서명 키 같은 기술 내용은 [src/spec.md](src/spec.md)에 있습니다.

## 기능

| 기능 | 설명 |
|---|---|
| 접두 기호 없이 최적의 결과 | 앱, 파일, 설정, 명령, 계산기, URL, 경로, 웹 검색을 한 번에 찾아 가장 알맞은 순서로 보여 줌. 자주 고른 결과는 같은 검색어나 그 앞부분만 쳐도 위로 올라옴 |
| 파일, 폴더 검색 | Everything 색인으로 즉시 검색. 같은 이름의 파일이나 앱이 많아도 이름이 맞는 폴더가 결과에 남음 |
| Spotlight처럼 간결한 UI | 검색창과 결과 패널 모두 같은 알약 곡률의 둥근 모서리. 흐린 배경, "최상위 결과"와 종류별 구역, 열 때 흐림에서 초점이 잡히고 닫을 때 부드럽게 사라지는 애니메이션, 은은하게 깜빡이는 커서, 시스템 밝은/어두운 테마 |
| 모든 앱 보기 | `앱`, `app`, `apps`, `application`, `애플리케이션`, `응용프로그램`, `프로그램` 을 입력하면 설치된 앱 전체를 이름순 그리드로 표시 (A–Z, ㄱ–ㅎ, # 구역. 제거 프로그램과 도움말 링크는 뺌) |
| 우클릭 메뉴 | 열기, 관리자 권한으로 실행, 파일 탐색기에서 열기, 경로 복사, 속성 |
| 미리보기 | 파일이나 폴더를 고른 채 Space를 1초 누르고 있으면 미리보기. 누르는 동안 줄이 당겨지다가 튀어나오듯 열림. 이미지, 텍스트와 코드, 폴더 내용, 그 밖에는 탐색기 썸네일(PDF, 동영상) |
| 클립보드 기록 | `clip`, `클립보드`, `ㅋㄹ` (+ 거를 단어). Win+V와 같은 Windows 클립보드 기록. Enter는 원래 창에 붙여넣기, Del은 기록에서 삭제 |
| 단위 변환 | `5km in mile`, `70kg lb`, `30c f`, `84m2 평`, `1.5gb`. 대상을 안 쓰면 흔히 쓰는 단위로 |
| 날짜, 시간 | `오늘 +100일`, `today +2w`, `d-day 2026-12-25`, `12월 25일까지`, `도쿄 시간`, `now london` |
| 검색 키워드 | `yt 고양이`, `nv 날씨`, `g ...`, `w ...` 로 해당 사이트에서 검색 (`searchEngines` 에서 추가) |
| 열린 창 전환 | 창 제목이나 프로그램 이름으로 찾아 Enter로 그 창으로 이동 (최소화된 창은 복원) |
| 프로세스 종료 | `kill chrome`, `프로세스 notepad`. 같은 이름의 프로세스를 모두 종료 (Enter 두 번) |
| 스니펫, 사용자 명령 | 설정의 `snippets` (키 → 붙여 넣을 글), `commands` (이름 → 실행할 프로그램과 인자) |
| 환전 | `100달러`, `$100`, `5만원 엔`, `100 usd to eur`, `100달러를 유로로`, `100달러는 몇 원?`. 대상을 안 쓰면 Windows 지역 설정의 통화(한국이면 원화)로, 이미 그 통화면 달러로 바꾸고 다른 주요 통화도 함께 보여 줌 |
| 답변 카드 | 계산, 환전 결과가 맨 위에 오면 큰 글씨 카드로 표시. Enter는 결과 복사 |
| MCP 서버 | `Quicklight.exe --mcp` 로 AI가 같은 검색 엔진을 사용. 창을 띄우지 않고 런처 화면을 흉내 낸 마크다운으로 답함 |
| 자동 업데이트 | 새 버전이 나오면 알아서 받아 두었다가 검색창이 닫혀 있을 때 설치하고 다시 시작 |
| 진행 표시줄 | 업데이트 다운로드, 앱 목록과 파일 색인을 결과 아래 얇은 막대로 표시. 양을 알 수 있으면 백분율, 모르면 흐르는 막대 |
| 설치할 것 없음 | exe 하나에 필요한 것이 모두 들어 있음 |

한국어 사용자를 위한 기능:

- 초성 검색: `ㅋㅋㅇㅌ` → 카카오톡
- 한/영 전환을 잊고 친 입력 복구: `rPtksrl` → 계산기, `qmffnxntm` → Bluetooth 설정, `ㅍㄴ챙ㄷ` → VS Code (Caps Lock이 켜져 있어도 됨)
- 설정과 명령을 한국어로: `해상도`, `블루투스`, `환경 변수`, `잠금`, `시스템 종료`
- 모음을 자음보다 먼저 친 오타 보정: `ㅏㅈ` → 자, `ㅏㅋ카오톡` → 카카오톡

## 권장 사양

| 항목 | 최소 | 권장 |
|---|---|---|
| OS | Windows 10 (1809 이상) 64비트 | Windows 11 64비트 |
| CPU | x64 2코어 | x64 4코어 이상 |
| 메모리 | 4GB | 8GB 이상 |
| 디스크 | 약 70MB (exe) | 약 70MB (exe) |
| 파일 시스템 | NTFS (Everything 색인) | NTFS SSD |
| 네트워크 | 필요 없음 | 환율, 업데이트 때만 사용 |

- 메모리 사용량은 Quicklight 약 200MB, Everything은 색인한 파일 수에 따라 수십 MB에서 수백 MB입니다.
- ARM64 Windows에서는 x64 에뮬레이션으로 실행됩니다.

## 설치

`Quicklight.exe` 파일 하나만 있으면 됩니다 (약 71MB). .NET 런타임과 파일 검색 엔진 Everything이 들어 있습니다.

- Everything이 이미 설치된 PC에서는 그것을 씁니다. 없으면 처음 실행할 때 설치할지 묻고, 설치하면 관리자 권한 확인(UAC)이 한 번 나옵니다. 거절하면 파일 검색 없이 실행되고, 트레이 메뉴에서 나중에 설치할 수 있습니다.
- 실행 중에는 트레이에 아이콘이 표시됩니다. 처음 한 번은 작업 표시줄에 꺼내 두고, 이후 사용자가 숨기면 그 선택을 따릅니다. 아이콘을 우클릭하면 버전과 빌드 시각이 보입니다.
- 처음 실행하면 Windows 시작 시 자동 실행이 켜집니다. 트레이 아이콘 메뉴에서 끌 수 있습니다.
- PowerToys Run이 켜져 있으면 Alt+Space를 두고 경쟁합니다. PowerToys 설정에서 PowerToys Run을 끄십시오.

## 업데이트

- **자동**: 시작하고 30초 뒤, 그리고 6시간마다 새 버전을 확인합니다. 새 버전이 있으면 내려받아 두고, 검색창이 닫혀 있을 때 설치하고 다시 시작합니다. 다시 시작하면 트레이 알림으로 알려 줍니다. 설정의 `autoUpdate: false` 로 끌 수 있습니다.
- **수동**: 런처에 `update` 또는 `업데이트` 를 치면 곧바로 확인합니다. 새 버전이 있으면 Enter 없이 바로 설치를 시작하고(진행률은 결과 아래 막대, Esc로 취소), 없으면 "최신 버전입니다"라고 알려 줍니다. Enter를 누르면 다시 확인합니다.
- 서명이 맞지 않거나 지금보다 오래된 버전은 설치하지 않습니다.

## 사용법

| 키 | 동작 |
|---|---|
| Alt+Space | 열기 / 닫기 (창은 마우스가 있는 모니터의 가운데에 뜸) |
| ↑ ↓, PgUp PgDn | 결과 이동 (앱 그리드에서는 ← → 도) |
| Enter | 열기, 실행 (계산 결과는 클립보드로 복사) |
| Ctrl+Enter | 탐색기에서 파일 위치 열기 |
| Ctrl+Shift+Enter | 관리자 권한으로 실행 |
| Alt+Enter | 속성 |
| Space 길게 (1초) | 파일, 폴더 미리보기. Space나 Esc로 닫고, ↑ ↓ 로 다른 항목 미리보기. 짧게 누르면 그냥 띄어쓰기 |
| Del | 클립보드 기록에서 삭제 |
| Ctrl+Shift+C | 경로 복사 |
| 우클릭 | 열기, 관리자 권한으로 실행, 파일 탐색기에서 열기, 경로 복사, 속성 |
| Alt+. | Everything 설정 창 열기 (색인할 폴더와 드라이브, 제외 목록 등) |
| Esc | 입력 지우기, 한 번 더 누르면 닫기 |

- 시스템 종료, 다시 시작, 로그아웃, 휴지통 비우기는 Enter를 두 번 눌러야 실행되고, 이름을 거의 다 입력해야 결과에 나옵니다.
- Everything 문법을 그대로 쓸 수 있습니다: `*.pdf`, `ext:docx 보고서`, `dm:today`, `C:\Project\ *.cs`
- 경로 입력: `C:\Pro` 처럼 치면 하위 항목을 보여 주고, `%APPDATA%`, `~\Downloads` 도 됩니다.

## 설정: `%APPDATA%\Quicklight\settings.json`

트레이 메뉴 "설정 파일 열기"로 편집하고 "설정 다시 불러오기"로 적용합니다.

| 키 | 기본값 | 설명 |
|---|---|---|
| `hotkey` | `"Alt+Space"` | 예: `"Ctrl+Space"`, `"Win+Alt+K"`, `"Alt+F1"` |
| `webSearchUrl` | Google | `{0}` 자리에 검색어가 들어감 |
| `searchEngines` | g, yt, nv, w | `"키워드": "URL"`. `yt 고양이` 처럼 키워드 뒤 검색어가 `{0}` 에 들어감 |
| `snippets` | 없음 | `"키": "글"`. 키를 치고 Enter를 누르면 글을 붙여 넣음 |
| `commands` | 없음 | `[{ "name": "빌드", "path": "C:\\tools\\build.cmd", "arguments": "--release", "aliases": ["build"] }]` |
| `maxResults` | 12 | 표시할 최대 결과 수 |
| `fileSearch` | true | Everything 파일 검색 |
| `maxFileResults` | 6 | 결과 중 파일의 최대 수 |
| `maxFolderResults` | 4 | 결과 중 폴더의 최대 수. 이름이 맞는 폴더는 앱이나 파일이 많아도 3자리까지 보장 |
| `demotedPaths` | AppData, node_modules 등 | 이 경로를 포함한 파일은 순위를 낮춤 |
| `excludedPaths` | 없음 | 이 경로를 포함한 파일은 숨김 |
| `currencyConversion` | true | 환전 결과 표시 |
| `defaultCurrency` | `"auto"` | 대상 통화를 안 썼을 때 바꿀 통화. `auto` 는 Windows 지역 설정의 통화 |
| `launchAtStartup` | true | Windows 시작 시 실행 |
| `theme` | `"system"` | `"dark"`, `"light"` |
| `updateRepository` | `"KusBeoms/Quicklight"` | `update` 가 최신 릴리스를 찾는 GitHub 저장소 |
| `autoUpdate` | true | 시작 뒤와 6시간마다 새 버전을 확인해, 검색창이 닫혀 있을 때 설치 |
| `bundledEverything` | true | Everything이 없을 때 내장 Everything 설치를 제안 |
| `everythingSetup` | `"ask"` | 내장 Everything 설치 제안에 대한 답 (`installed`, `declined`) |
| `backdrop` | `"acrylic"` | 창 뒤 화면을 흐리게 비춤. `"solid"` 는 불투명 배경 |

흐린 배경은 창을 여는 순간의 화면을 흐리게 한 것입니다. 그래서 창이 열려 있는 동안에는 뒤에서 재생되는 동영상이 배경에서 움직이지 않습니다.

환율은 하루 단위 기준 환율이라 실제 은행 환전 시세(수수료 포함)와 다를 수 있습니다. 환율 서버에는 환율표만 요청하고, 입력한 검색어는 보내지 않습니다.

사용 기록은 `%APPDATA%\Quicklight\history.json`, 로그는 `%LOCALAPPDATA%\Quicklight\quicklight.log` 에 있습니다.

## MCP 서버 (AI 연결)

AI가 Quicklight 검색을 쓰게 할 수 있습니다. AI가 부를 때는 창이 뜨지 않고, 런처 화면을 마크다운으로 옮긴 형태로 답합니다. 계산, 환전 답은 큰 제목으로, 나머지 결과는 종류별 구역으로, 파일 목록은 표로 나옵니다.

```bat
claude mcp add quicklight -- "%LOCALAPPDATA%\Programs\Quicklight\Quicklight.exe" --mcp
```

다른 클라이언트의 설정 예:

```json
{ "mcpServers": { "quicklight": { "command": "C:\\Users\\<you>\\AppData\\Local\\Programs\\Quicklight\\Quicklight.exe", "args": ["--mcp"] } } }
```

| 도구 | 설명 |
|---|---|
| `search` | 런처와 같은 통합 검색. `kinds` 로 종류 제한 (`app`, `file`, `folder`, `setting`, `command`, `calculator`, `url`, `path`, `web_search`) |
| `search_files` | Everything 파일 검색. Everything 문법, 정렬(`modified`, `name`, `path`, `size`, `run_count`), 경로 일치, 정규식 |
| `convert_currency` | 환전. `amount`, `from`, `to` (통화 코드나 `달러`, `엔` 같은 이름) |
| `calculate` | 수식 계산 |
| `convert_unit` | 단위 변환 (`5 km in mile`) |
| `date_calc` | 날짜 계산, D-day, 도시 시간 |
| `list_windows` | 열린 창 목록 |
| `recent_files` | 최근 수정한 파일 (`days`, `ext`) |
| `open` | 검색 결과의 대상 열기. 문서, 폴더, 웹 주소, Windows 설정, 앱만 열고 프로그램이나 스크립트는 실행하지 않음 |
| `reveal` | 탐색기에서 파일 위치 열기 |

옵션: `--read-only` (`open`, `reveal` 도구를 빼고 읽기만), `--no-history` (사용 기록을 순위에 쓰지 않음).
