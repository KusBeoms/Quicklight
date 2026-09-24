using Quicklight.Core.Matching;
using Quicklight.Core.Models;

namespace Quicklight.Core.Providers;

/// <summary>Windows settings pages, admin tools and power commands, findable by Korean or English names.</summary>
public sealed class SystemProvider : IResultProvider
{
    sealed record Item(string Title, string[] Aliases, ResultKind Kind, string Target, string? Args = null, bool Confirm = false, string Subtitle = "");

    static readonly Item[] Items =
    [
        S("디스플레이", "ms-settings:display", "display", "화면", "해상도", "resolution", "모니터", "배율", "scale"),
        S("소리", "ms-settings:sound", "sound", "사운드", "볼륨", "volume", "스피커", "마이크", "audio"),
        S("알림", "ms-settings:notifications", "notifications", "방해 금지", "do not disturb"),
        S("전원 및 배터리", "ms-settings:powersleep", "power", "battery", "전원", "배터리", "절전 설정"),
        S("저장소", "ms-settings:storagesense", "storage", "디스크 공간", "저장 공간"),
        S("Bluetooth 및 장치", "ms-settings:bluetooth", "bluetooth", "블루투스", "장치", "devices"),
        S("네트워크 및 인터넷", "ms-settings:network", "network", "internet", "네트워크", "인터넷", "이더넷", "ethernet"),
        S("Wi-Fi", "ms-settings:network-wifi", "wifi", "와이파이", "무선"),
        S("VPN", "ms-settings:network-vpn", "vpn"),
        S("프록시", "ms-settings:network-proxy", "proxy"),
        S("개인 설정", "ms-settings:personalization", "personalization", "배경 화면", "wallpaper", "background", "테마", "theme"),
        S("색", "ms-settings:colors", "colors", "다크 모드", "dark mode", "라이트 모드", "light mode", "강조 색"),
        S("작업 표시줄", "ms-settings:taskbar", "taskbar"),
        S("설치된 앱", "ms-settings:appsfeatures", "installed apps", "앱 및 기능", "apps features", "프로그램 제거", "uninstall", "프로그램 추가 제거"),
        S("기본 앱", "ms-settings:defaultapps", "default apps", "기본 프로그램"),
        S("시작 앱", "ms-settings:startupapps", "startup apps", "시작 프로그램", "startup"),
        S("계정", "ms-settings:yourinfo", "account", "accounts", "내 정보"),
        S("로그인 옵션", "ms-settings:signinoptions", "sign-in options", "비밀번호", "password", "pin", "windows hello"),
        S("날짜 및 시간", "ms-settings:dateandtime", "date", "time", "시간", "날짜", "시간대", "timezone"),
        S("언어 및 지역", "ms-settings:regionlanguage", "language", "region", "언어", "지역"),
        S("키보드", "ms-settings:keyboard", "keyboard", "입력기"),
        S("마우스", "ms-settings:mousetouchpad", "mouse", "커서", "포인터"),
        S("터치패드", "ms-settings:devices-touchpad", "touchpad"),
        S("프린터 및 스캐너", "ms-settings:printers", "printers", "printer", "프린터", "스캐너", "scanner"),
        S("게임 모드", "ms-settings:gaming-gamemode", "game mode", "게임"),
        S("접근성", "ms-settings:easeofaccess", "accessibility", "ease of access", "돋보기", "magnifier"),
        S("개인 정보 및 보안", "ms-settings:privacy", "privacy", "개인정보", "보안", "security", "권한", "permissions"),
        S("Windows 보안", "windowsdefender:", "windows security", "defender", "바이러스", "antivirus", "방화벽", "firewall"),
        S("Windows 업데이트", "ms-settings:windowsupdate", "windows update", "update", "업데이트"),
        S("시스템 정보", "ms-settings:about", "about", "정보", "pc 정보", "pc 이름", "사양", "system info"),
        S("야간 모드", "ms-settings:nightlight", "night light", "블루라이트", "야간 조명"),
        S("클립보드", "ms-settings:clipboard", "clipboard", "클립보드 기록"),
        S("멀티태스킹", "ms-settings:multitasking", "multitasking", "스냅", "snap", "가상 데스크톱"),
        S("개발자용", "ms-settings:developers", "developers", "개발자 모드", "developer mode"),
        S("복구", "ms-settings:recovery", "recovery", "초기화", "reset this pc"),
        S("설정", "ms-settings:", "settings", "windows 설정"),

        T("제어판", "control.exe", null, "control panel", "control"),
        T("장치 관리자", "devmgmt.msc", null, "device manager", "드라이버", "driver"),
        T("디스크 관리", "diskmgmt.msc", null, "disk management", "파티션", "partition"),
        T("서비스", "services.msc", null, "services"),
        T("이벤트 뷰어", "eventvwr.msc", null, "event viewer", "로그"),
        T("작업 스케줄러", "taskschd.msc", null, "task scheduler"),
        T("환경 변수", "rundll32.exe", "sysdm.cpl,EditEnvironmentVariables", "environment variables", "env", "path 편집", "환경변수"),
        T("시스템 속성", "SystemPropertiesAdvanced.exe", null, "system properties", "고급 시스템 설정"),
        T("네트워크 연결", "ncpa.cpl", null, "network connections", "어댑터", "adapter"),
        T("프로그램 및 기능", "appwiz.cpl", null, "programs and features"),
        T("사운드 제어판", "mmsys.cpl", null, "sound control panel", "재생 장치", "녹음 장치"),

        C("잠금", "lock", false, "lock", "화면 잠금", "lock screen"),
        C("절전", "sleep", false, "sleep", "절전 모드", "suspend"),
        C("최대 절전 모드", "hibernate", false, "hibernate"),
        C("로그아웃", "signout", true, "sign out", "log off", "logout", "로그오프"),
        C("다시 시작", "restart", true, "restart", "reboot", "재시작", "재부팅"),
        C("시스템 종료", "shutdown", true, "shutdown", "shut down", "power off", "컴퓨터 끄기", "끄기"),
        C("휴지통 비우기", "recyclebin", true, "empty recycle bin", "휴지통", "recycle bin"),
    ];

    static Item S(string title, string uri, params string[] aliases) => new(title, aliases, ResultKind.Setting, uri, Subtitle: "Windows 설정");
    static Item T(string title, string exe, string? args, params string[] aliases) => new(title, aliases, ResultKind.Setting, exe, args, Subtitle: "Windows 도구");
    static Item C(string title, string id, bool confirm, params string[] aliases) =>
        new(title, aliases, ResultKind.Command, id, Confirm: confirm, Subtitle: confirm ? "시스템 명령 · Enter 두 번으로 실행" : "시스템 명령");

    public string Name => "system";

    public Task<IReadOnlyList<SearchResult>> QueryAsync(QueryContext query, CancellationToken ct)
    {
        var results = new List<SearchResult>();
        foreach (var it in Items)
        {
            double m = FuzzyMatcher.Score(query.Text, it.Title);
            foreach (var a in it.Aliases) m = Math.Max(m, FuzzyMatcher.Score(query.Text, a) - 2);
            // Destructive commands must be typed nearly in full: "종" alone must never offer shutdown as the top hit.
            double min = it.Confirm ? Math.Max(query.MinMatch, 85) : query.MinMatch;
            if (m < min) continue;
            double @base = it.Kind == ResultKind.Command ? Scores.CommandBase : Scores.SettingBase;
            results.Add(new SearchResult
            {
                Title = it.Title,
                Subtitle = it.Subtitle,
                Kind = it.Kind,
                Target = it.Target,
                Arguments = it.Args,
                Action = it.Kind == ResultKind.Command ? ActionType.System : ActionType.Open,
                RequiresConfirmation = it.Confirm,
                Score = @base + 0.9 * m,
            });
        }
        return Task.FromResult<IReadOnlyList<SearchResult>>(results);
    }
}
