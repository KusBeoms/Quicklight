namespace Quicklight.Core.Ai;

/// <summary>Tells a question or a request for an assistant apart from a launcher search.</summary>
public static class QuestionDetector
{
    // A word starting with one of these asks something: "뭐함", "왜", "어떻게", "몇시".
    static readonly string[] KoreanQuestionWords =
        ["뭐", "뭘", "무엇", "무슨", "왜", "어떻게", "어떤", "어때", "어디", "언제", "누구", "누가", "몇", "얼마", "어느"];

    // How a Korean question or request ends. Only trusted on two or more words, so app names stay searches.
    static readonly string[] KoreanEndings =
    [
        "까", "까요", "니", "냐", "나요", "가요", "죠", "지요", "래", "래요", "는지", "인지", "을지",
        "줘", "줘요", "주세요", "줄래", "해봐", "봐줘", "알려", "해요", "할래", "할까", "될까", "일까", "인가", "인가요",
        "하나", "했어", "했지", "있어", "없어", "맞아", "아니야", "거야", "건가", "뭐야", "뭐지", "뭐함",
    ];

    // A last word that names what is wanted from an answer: "파이썬 정렬 방법", "사과 영어로", "두 차이".
    static readonly string[] KoreanTopicEnds =
    [
        "방법", "법", "뜻", "의미", "차이", "차이점", "이유", "원리", "추천", "요약", "번역", "정리", "설명", "예시", "비교",
        "장단점", "순서", "하는법", "영어로", "한국어로", "일본어로", "중국어로", "한글로",
    ];

    static readonly string[] WhWords = ["what", "why", "how", "who", "whom", "whose", "when", "where", "which"];

    // Yes/no openers are also the first word of names ("Do Not Disturb", "Will Smith"), so they need a longer sentence.
    static readonly string[] AuxWords =
        ["is", "are", "was", "were", "can", "could", "should", "would", "will", "do", "does", "did", "have", "has", "am"];

    static readonly string[] EnglishRequests = ["tell me", "explain", "summarize", "translate", "write", "give me", "help me"];

    public static bool IsQuestion(string text)
    {
        var t = text.Trim();
        if (t.Length < 2) return false;
        if (t.EndsWith('?') || t.EndsWith('？')) return !LooksLikeLauncherInput(t);
        if (LooksLikeLauncherInput(t)) return false;

        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var first = words[0].ToLowerInvariant();
        var last = words[^1].TrimEnd('.', '!', '~', '…');

        if (words.Any(w => KoreanQuestionWords.Any(q => w.StartsWith(q, StringComparison.Ordinal)))) return true;
        if (words.Length < 2) return false;

        if (WhWords.Contains(first)) return true;
        if (AuxWords.Contains(first) && words.Length >= 4) return true;
        if (EnglishRequests.Any(r => t.StartsWith(r + " ", StringComparison.OrdinalIgnoreCase))) return true;

        if (KoreanEndings.Any(e => last.EndsWith(e, StringComparison.Ordinal))) return true;
        if (KoreanTopicEnds.Any(e => last.EndsWith(e, StringComparison.Ordinal))) return true;
        // Polite Korean sentence ending: "노트북 하나 사고 싶어요".
        if (IsHangul(last) && last.EndsWith('요')) return true;

        // A long Korean sentence is not a name to search for; "개인 정보 및 보안" style titles list with "및".
        return words.Length >= 4 && words.Count(IsHangul) >= 3 && !words.Contains("및");
    }

    /// <summary>Paths, URLs, math and file names: things the launcher itself answers.</summary>
    static bool LooksLikeLauncherInput(string t) =>
        t.Contains('\\') || t.Contains("://") || t.StartsWith('/') || t.StartsWith('=') ||
        t.All(c => char.IsDigit(c) || " +-*/^%().,?=".Contains(c));

    static bool IsHangul(string w) => w.Any(c => c is >= '가' and <= '힣');
}
