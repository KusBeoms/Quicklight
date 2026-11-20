using Quicklight.Core.Ai;
using Quicklight.Core.Providers;

namespace Quicklight.Tests;

public class AskAiTests
{
    [Theory]
    [InlineData("내가 뭐 하고있게?")]
    [InlineData("뭐함")]
    [InlineData("오늘 날씨 어때?")]
    [InlineData("이 에러 왜 나는지")]
    [InlineData("회의록 요약해줘")]
    [InlineData("파이썬 설치 어떻게 해")]
    [InlineData("what is a mutex")]
    [InlineData("파이썬 리스트 정렬 방법")]
    [InlineData("사과 영어로")]
    [InlineData("TCP UDP 차이")]
    [InlineData("노트북 하나 사고 싶어요")]
    [InlineData("오늘 저녁 메뉴 추천")]
    [InlineData("이 문장 좀 자연스럽게 바꿔줘")]
    [InlineData("내일 서울 비 오나")]
    [InlineData("is it going to rain")]
    [InlineData("explain quantum computing")]
    [InlineData("회의 끝나고 보낼 메일 초안 써")]
    [InlineData("지금 몇시")]
    public void Detects_questions(string q) => Assert.True(QuestionDetector.IsQuestion(q));

    [Theory]
    [InlineData("카카오톡")]
    [InlineData("vs code")]
    [InlineData("지니")]
    [InlineData("chrome")]
    [InlineData("블루투스 설정")]
    [InlineData("yt 고양이")]
    [InlineData("5km in mile")]
    [InlineData("do not disturb")]
    [InlineData("will smith")]
    [InlineData("시스템 종료")]
    [InlineData("환경 변수")]
    [InlineData("1+2?")]
    [InlineData(@"C:\Users\?")]
    [InlineData("kill chrome")]
    [InlineData("오늘 +100일")]
    [InlineData("100달러")]
    [InlineData("개인 정보 및 보안")]
    public void Leaves_searches_alone(string q) => Assert.False(QuestionDetector.IsQuestion(q));

    [Fact]
    public async Task Offers_the_question_only_when_the_ai_is_available()
    {
        Assert.Empty(await new AskAiProvider(() => false).QueryAsync(new QueryContext("뭐함?"), default));

        var r = Assert.Single(await new AskAiProvider(() => true).QueryAsync(new QueryContext("뭐함?"), default));
        Assert.Equal(Quicklight.Core.Models.ActionType.AskAi, r.Action);
        Assert.Equal("뭐함?", r.Target);
    }

    [Fact]
    public void Needs_64_gb_of_ram()
    {
        Assert.Equal(64L * 1024 * 1024 * 1024, AiModel.MinRamBytes);
        Assert.Equal(AiModel.RamBytes >= AiModel.MinRamBytes, AiModel.HasEnoughRam);
    }
}
