# Quicklight

Windows용 Spotlight 스타일 런처입니다. **Alt+Space** 를 누르면 화면 가운데에 검색창이 뜹니다. PowerToys Run을 대신하도록 만들었습니다.

| PowerToys Run과 다른 점 | 구현 |
|---|---|
| 접두 기호 없이 최적의 결과 | 모든 공급자(앱, 파일, 설정, 명령, 계산기, URL, 경로, 웹 검색)를 한꺼번에 조회하고 하나의 점수 체계로 정렬. 고른 결과를 기억해서 같은 검색어나 그 앞부분을 다시 치면 위로 올림 |
| Everything 색인으로 파일 검색 | SDK DLL 없이 Everything IPC(WM_COPYDATA)를 직접 구현. 빠른 이름 앞부분 검색 결과를 먼저 보여 주고 전체 검색 결과로 이어서 채움 |
| Spotlight처럼 간결한 UI | 빈 상태에서는 알약 모양 검색창, 결과가 나오면 둥근 패널. 흐린 배경, "최상위 결과"와 종류별 구역, 열고 닫을 때 흐림에서 초점이 잡히는 애니메이션, 시스템 밝은/어두운 테마 |
| 모든 앱 보기 | `앱`, `app`, `apps`, `application`, `애플리케이션`, `응용프로그램`, `프로그램` 을 입력하면 설치된 앱 전체를 이름순 그리드로 표시 (A–Z, ㄱ–ㅎ, # 구역. 제거 프로그램과 도움말 링크는 뺌) |
| 우클릭 메뉴 | 열기, 관리자 권한으로 실행, 파일 탐색기에서 열기, 경로 복사 |
| 환전 | `100달러`, `$100`, `5만원 엔`, `100 usd to eur`, `100달러를 유로로`, `100달러는 몇 원?`. 대상을 안 쓰면 Windows 지역 설정의 통화(한국이면 원화)로, 이미 그 통화면 달러로 바꾸고 다른 주요 통화도 함께 보여 줌 |
| 번역 | `hello 번역`, `번역 good morning`, `사과 영어로`, `영어로 오늘 날씨 좋다`, `good morning in korean`, `serendipity 뜻`. 이 PC에서 도는 LibreTranslate로 번역하므로 글이 밖으로 나가지 않음. 대상을 안 쓰면 한국어는 영어로, 그 밖의 언어는 한국어로 |
| 답변 카드 | 계산, 환전, 번역 결과가 맨 위에 오면 큰 글씨 카드로 표시. Enter는 결과 복사 |
| MCP 서버 | `Quicklight.exe --mcp` 로 AI가 같은 검색 엔진을 사용. 창을 띄우지 않고 런처 화면을 흉내 낸 마크다운으로 답함 |
| 자동 업데이트 | `update` (또는 `업데이트`) 를 치고 Enter: GitHub 최신 릴리스를 받아 서명을 확인한 뒤 교체하고 다시 시작하며, 이전 파일은 새 버전이 지움 |
| 설치할 것 없음 | exe 하나에 .NET 런타임과 Everything이 들어 있음. 번역 엔진은 처음 번역할 때 스스로 설치 |

한국어 사용자를 위한 기능:

- 초성 검색: `ㅋㅋㅇㅌ` → 카카오톡
- 한/영 전환을 잊고 친 입력 복구: `rPtksrl` → 계산기, `qmffnxntm` → Bluetooth 설정, `ㅍㄴ챙ㄷ` → VS Code (Caps Lock이 켜져 있어도 됨)
- 설정과 명령을 한국어로: `해상도`, `블루투스`, `환경 변수`, `잠금`, `시스템 종료`

## 설치

결과물은 `Quicklight.exe` 파일 하나입니다 (약 66MB). 다른 PC에서도 이 파일만 있으면 실행됩니다.

- **.NET 런타임**: exe 안에 들어 있습니다.
- **Everything (파일 검색)**: exe 안에 들어 있습니다 (voidtools, MIT 라이선스). Everything이 이미 설치된 PC에서는 그것을 씁니다. 없으면 처음 실행할 때 설치할지 묻고, 설치하면 관리자 권한 확인(UAC)이 한 번 나옵니다. NTFS 색인을 읽는 Everything 서비스가 관리자 권한을 필요로 하기 때문입니다. 설치 위치는 `C:\Program Files\Quicklight\Everything` 입니다. 서비스 파일을 사용자 폴더에 두면 다른 프로그램이 바꿔치기해 관리자 권한을 얻을 수 있어서 이 위치에 둡니다. 거절하면 파일 검색 없이 실행되고, 트레이 메뉴에서 나중에 설치할 수 있습니다.
- **번역 엔진 (LibreTranslate)**: 언어 모델까지 약 1GB라 exe에 넣지 않았습니다. 처음 번역할 때 Python(python.org 공식 NuGet 패키지, SHA-256 검증)과 LibreTranslate를 `%LOCALAPPDATA%\Quicklight\translate` 에 자동으로 설치하고 언어 모델을 받습니다. 그동안 결과에 진행 상황이 표시됩니다.

```bat
publish.bat 0.2.0                                      :: 테스트 후 버전 0.2.0 으로 dist\Quicklight.exe 를 만들고, 서명해서 dist\Quicklight_v0.2.0.zip 으로 묶음
powershell -ExecutionPolicy Bypass -File install.ps1   :: %LOCALAPPDATA%\Programs\Quicklight 에 설치하고 실행
```

- 실행 중에는 트레이에 아이콘이 표시됩니다. Windows 11은 새 아이콘을 숨김 영역에 넣는데, Quicklight는 처음 한 번 작업 표시줄로 꺼내 둡니다. 이후 사용자가 숨기면 그 선택을 따릅니다.
- 처음 실행하면 Windows 시작 시 자동 실행이 켜집니다. 트레이 아이콘 메뉴에서 끌 수 있습니다.
- PowerToys Run이 켜져 있으면 Alt+Space를 두고 경쟁합니다. PowerToys 설정에서 PowerToys Run을 끄십시오.
- Alt+Space를 다른 프로그램이 이미 등록했다면 저수준 키보드 후크로 대신 가로챕니다.

## 업데이트

런처에 `update` 또는 `업데이트` 를 치면 GitHub `KusBeoms/Quicklight` 의 최신 릴리스를 확인해 행에 보여 줍니다. 예: "새 버전 v0.2.0 있음 · Enter로 내려받아 설치하고 다시 시작". 확인할 저장소는 설정의 `updateRepository` 로 바꿀 수 있습니다. 새 버전이 있을 때 Enter를 누르면 다음 순서로 진행됩니다.

1. 릴리스의 `Quicklight_v0.2.0.zip` 같은 zip을 받습니다. 진행률이 표시되고, Esc로 취소할 수 있습니다.
2. zip 안의 `Quicklight.exe` 가 **릴리스 키로 서명되었는지** `Quicklight.exe.sig` 로 확인합니다. 서명이 없거나 맞지 않으면 설치하지 않습니다. GitHub 계정이 탈취되어도 서명 키 없이는 업데이트를 퍼뜨릴 수 없습니다.
3. 서명된 exe 안의 버전이 지금 버전보다 새로운지 확인합니다. 태그만 바꿔 예전 빌드를 올리는 식의 버전 되돌리기도 막습니다.
4. 실행 중인 exe와 바꾸고 새 버전을 실행합니다. 새 버전은 이전 파일을 지웁니다. 새 버전이 실행되지 않으면 원래 파일로 되돌립니다.

### 릴리스 올리는 법

1. `publish.bat 0.2.0` 으로 패키징합니다. 버전 번호를 exe에 기록하고, `Quicklight.exe.sig` 를 만들어 둘을 `dist\Quicklight_v0.2.0.zip` 으로 묶습니다.
2. GitHub에서 태그 `v0.2.0` 으로 릴리스를 만들고 `dist\Quicklight_v0.2.0.zip` 을 첨부합니다. 파일 이름은 `Quicklight_v버전.zip` 형식이어야 합니다 (`Quicklight.zip` 도 받습니다). 초안(draft)과 사전 릴리스(pre-release)는 업데이트 대상에서 빠집니다.

### 서명 키

- 비밀 키는 `%USERPROFILE%\.quicklight\update-signing-key.pem` 에 있습니다. 이 PC의 현재 사용자만 읽을 수 있고, 저장소에는 들어가지 않습니다. 다른 곳의 키를 쓰려면 환경 변수 `QUICKLIGHT_SIGNING_KEY` 에 경로를 지정합니다.
- **이 파일을 안전한 곳에 백업하십시오.** 잃어버리면 이미 설치된 Quicklight들이 이후 업데이트를 받지 못합니다. 새 키를 만들면 새 공개 키가 들어간 버전을 사용자가 직접 한 번 설치해야 합니다.
- 공개 키는 `src\Quicklight.Core\Update\UpdateSignature.cs` 에 있습니다. 키를 새로 만들려면 `tools\new-signing-key.ps1` 을 실행하고, 출력된 공개 키를 이 파일에 넣습니다.

## 사용법

| 키 | 동작 |
|---|---|
| Alt+Space | 열기 / 닫기 (창은 마우스가 있는 모니터의 가운데에 뜸) |
| ↑ ↓, PgUp PgDn | 결과 이동 (앱 그리드에서는 ← → 도) |
| Enter | 열기, 실행 (계산 결과는 클립보드로 복사) |
| Ctrl+Enter | 탐색기에서 파일 위치 열기 |
| Ctrl+Shift+C | 경로 복사 |
| 우클릭 | 열기, 관리자 권한으로 실행, 파일 탐색기에서 열기, 경로 복사 |
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
| `maxResults` | 12 | 표시할 최대 결과 수 |
| `fileSearch` | true | Everything 파일 검색 |
| `maxFileResults` | 6 | 결과 중 파일과 폴더의 최대 수 |
| `demotedPaths` | AppData, node_modules 등 | 이 경로를 포함한 파일은 순위를 낮춤 |
| `excludedPaths` | 없음 | 이 경로를 포함한 파일은 숨김 |
| `currencyConversion` | true | 환전 결과 표시 |
| `defaultCurrency` | `"auto"` | 대상 통화를 안 썼을 때 바꿀 통화. `auto` 는 Windows 지역 설정의 통화 |
| `translation` | true | 번역 결과 표시 |
| `libreTranslateUrl` | `"http://127.0.0.1:5055"` | LibreTranslate 서버 주소. 이 PC 주소(localhost)만 허용 |
| `libreTranslateDir` | `""` | LibreTranslate 폴더. 비우면 exe 위쪽 폴더에서 `.library\LibreTranslate` 를 찾음 |
| `translationLanguages` | `["ko","en","ja","zh"]` | 서버가 불러올 언어 모델 |
| `secondaryLanguage` | `"en"` | 이미 시스템 언어인 글을 번역할 대상 |
| `launchAtStartup` | true | Windows 시작 시 실행 |
| `theme` | `"system"` | `"dark"`, `"light"` |
| `updateRepository` | `"KusBeoms/Quicklight"` | `update` 가 최신 릴리스를 찾는 GitHub 저장소 |
| `bundledEverything` | true | Everything이 없을 때 내장 Everything 설치를 제안 |
| `everythingSetup` | `"ask"` | 내장 Everything 설치 제안에 대한 답 (`installed`, `declined`) |
| `autoInstallTranslation` | true | 번역 엔진이 없으면 처음 번역할 때 자동 설치 |
| `backdrop` | `"acrylic"` | 창 뒤 화면을 흐리게 비춤. `"solid"` 는 불투명 배경 |

흐린 배경은 창을 여는 순간의 화면을 흐리게 한 것입니다. 그래서 창이 열려 있는 동안에는 뒤에서 재생되는 동영상이 배경에서 움직이지 않습니다.

환율은 [open.er-api.com](https://www.exchangerate-api.com) 의 하루 단위 기준 환율(약 166개 통화)을 쓰고, 실패하면 유럽중앙은행 기준의 [Frankfurter](https://frankfurter.dev) 로 넘어갑니다. 받아 온 환율표는 `%LOCALAPPDATA%\Quicklight\rates.json` 에 저장하고 6시간마다 새로 받습니다. 서버에는 환율표만 요청하고, 입력한 검색어는 보내지 않습니다. 실제 은행 환전 시세(수수료 포함)와는 다를 수 있습니다.

## 번역 (LibreTranslate)

번역은 [LibreTranslate](https://github.com/LibreTranslate/LibreTranslate) 를 이 PC에서 실행해 처리합니다. 인터넷이 필요한 건 처음 설치하고 언어 모델을 받을 때뿐이고, 그 뒤로는 오프라인에서도 번역됩니다.

- 따로 설치할 필요가 없습니다. 처음 번역할 때 `%LOCALAPPDATA%\Quicklight\translate` 에 자동으로 설치됩니다. 설치 로그는 같은 폴더의 `install.log` 에 있습니다.
- 개발용으로 이 저장소의 `.library\LibreTranslate` 에 가상환경을 만들어 두면 그것을 먼저 씁니다 (`py -3.12 -m venv .venv` 후 `.venv\Scripts\python -m pip install -e .`).
- Quicklight가 번역 요청을 처음 받을 때 서버를 숨김 상태로 켜고, Quicklight가 종료되면 함께 끕니다. 서버가 이미 떠 있으면 그 서버를 씁니다.
- 첫 실행에는 언어 모델(한국어, 영어, 일본어, 중국어)을 내려받느라 몇 분 걸릴 수 있습니다. 그동안 결과에 "번역 엔진을 준비하는 중"이 표시됩니다. 서버 로그는 `%LOCALAPPDATA%\Quicklight\libretranslate.log` 에 있습니다.
- 오픈소스 번역 모델이라 문장은 쓸 만하지만, 단어 하나만 넣으면 엉뚱한 결과가 나올 때가 있습니다.

사용 기록은 `%APPDATA%\Quicklight\history.json`, 로그는 `%LOCALAPPDATA%\Quicklight\quicklight.log` 에 있습니다.

## MCP 서버

AI가 부를 때는 창이 뜨지 않습니다. 각 도구의 텍스트 응답은 런처 화면을 마크다운으로 옮긴 형태입니다. 계산, 환전, 번역 답은 큰 제목(`#`)으로, 나머지 결과는 종류별 구역으로, 파일 목록은 표로 나옵니다. 같은 데이터는 프로그램이 읽을 수 있게 `structuredContent` JSON으로도 함께 보냅니다.

`Quicklight.exe --mcp` 는 stdio MCP 서버입니다 (프로토콜 2025-06-18, 2025-03-26, 2024-11-05). 런처와 같은 exe이며, 런처가 켜져 있는지와 관계없이 따로 동작합니다.

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
| `translate` | 로컬 번역. `text`, `to` (언어 코드나 `영어`, `japanese` 같은 이름, 생략하면 자동) |
| `calculate` | 수식 계산 |
| `open` | 검색 결과의 대상 열기. 문서, 폴더, http(s) URL, `ms-settings:`, 색인된 앱만 허용. 실행 파일, 스크립트, 바로가기, 네트워크 경로는 거부 (AI가 속아서 프로그램을 실행하지 않도록) |
| `reveal` | 탐색기에서 파일 위치 열기 |

옵션: `--read-only` (`open`, `reveal` 도구를 빼고 읽기만), `--no-history` (사용 기록을 순위에 쓰지 않음).

## 구조

```
src/Quicklight.Core    검색 엔진 (UI 없음)
  Matching/            퍼지 매칭, 초성, 한/영 자판 변환
  Everything/          Everything IPC 클라이언트
  Shell/               앱 목록(shell:AppsFolder), 모든 앱 그리드 정리, 셸 아이콘, 실행
  Providers/           앱, 파일, 계산기, URL, 경로, 설정/명령, 웹 검색
  SearchEngine.cs      병렬 조회, 점수 통합, 학습, 중복 제거
src/Quicklight         WPF 런처 (단축키, 창, 애니메이션, 트레이). Program.cs 가 --mcp 면 MCP 서버로 실행
src/Quicklight.Mcp     MCP 서버 라이브러리
tests/Quicklight.Tests xUnit 테스트
```

```bat
dotnet build Quicklight.sln
dotnet test tests\Quicklight.Tests
src\Quicklight\bin\Debug\net8.0-windows\Quicklight.exe --show     :: 바로 창 띄우기
src\Quicklight\bin\Debug\net8.0-windows\Quicklight.exe --pinned   :: 포커스를 잃어도 닫히지 않음 (디버깅용)
```
