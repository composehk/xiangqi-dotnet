using System.Net.Http.Json;
using Xiangqi.Core;

namespace Xiangqi.Tests;

public class FenValidationTests
{
    // Every case has exactly ONE fault (kings never share a file unless that is the fault under test).
    private const string Tail = " w - - 0 1";
    public static IEnumerable<object[]> IllegalFens()
    {
        yield return new object[] { "3k5/9/9/9/9/9/9/9/9/9" + Tail };                    // no red general
        yield return new object[] { "9/9/9/9/9/9/9/9/9/4K4" + Tail };                    // no black general
        yield return new object[] { "3k5/9/9/9/9/9/9/9/4K4/4K4" + Tail };                // two red generals
        yield return new object[] { "3k5/9/9/9/9/4K4/9/9/9/9" + Tail };                  // red general in the middle
        yield return new object[] { "3k5/9/9/9/9/9/9/9/9/2K6" + Tail };                  // red general outside the palace (file c)
        yield return new object[] { "9/9/9/9/9/9/3k5/9/9/4K4" + Tail };                  // black general outside the palace (rank 6)
        yield return new object[] { "3k5/9/9/9/9/9/4A4/9/9/4K4" + Tail };                // advisor off the palace (e3)
        yield return new object[] { "3k5/9/9/9/9/9/9/9/9/2A1K4" + Tail };                // advisor on c0 (not a palace point)
        yield return new object[] { "3k5/9/9/9/4B4/9/9/9/9/4K4" + Tail };                // elephant across the river (e5)
        yield return new object[] { "3k5/9/9/9/9/9/9/9/9/3BK4" + Tail };                 // elephant on d0
        yield return new object[] { "3k5/9/9/9/P8/P1P1P1P1P/9/9/9/4K4" + Tail };         // six red soldiers
        yield return new object[] { "3k5/9/9/9/9/9/9/9/9/P3K4" + Tail };                 // red soldier behind its start rank
        yield return new object[] { "3k5/9/9/9/9/9/1P7/9/9/4K4" + Tail };                // red soldier on b3 (odd file, own half)
        yield return new object[] { "3k5/9/9/1p7/9/9/9/9/9/4K4" + Tail };                // black soldier on b6 (odd file, own half)
        yield return new object[] { "3k5/p8/9/9/9/9/9/9/9/4K4" + Tail };                 // black soldier on rank 8 (behind its start rank)
        yield return new object[] { "3k5/9/1p7/9/9/9/9/9/9/4K4" + Tail };                // black soldier on b7 (behind its start rank)
        yield return new object[] { "4k4/9/9/9/9/9/9/9/9/4K4" + Tail };                  // generals face each other
        yield return new object[] { "3k5/9/9/9/9/9/9/9/9/3RK4" + Tail };                 // black (not to move) is in check
        yield return new object[] { "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 99999999999 1" };
        yield return new object[] { "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - abc 1" };
        yield return new object[] { "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - -1 1" };
        yield return new object[] { "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 99999999999" };
        yield return new object[] { "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 0" };
    }

    public static IEnumerable<object[]> LegalControlFens()
    {
        yield return new object[] { "3k5/9/9/9/9/9/9/9/9/4K4" + Tail };
        yield return new object[] { "3k5/9/9/9/9/9/9/9/4A4/4K4" + Tail };                // advisor e1
        yield return new object[] { "3k5/9/9/9/9/9/9/9/9/2B1K4" + Tail };                // elephant c0
        yield return new object[] { "3k5/9/9/9/P8/9/9/9/9/4K4" + Tail };                 // red soldier a5 (crossed the river)
        yield return new object[] { "3k5/9/9/9/1P7/9/9/9/9/4K4" + Tail };                // red soldier b5: sideways after crossing
        yield return new object[] { "3k5/9/9/9/9/1p7/9/9/9/4K4" + Tail };                // black soldier b4: sideways after crossing
        yield return new object[] { "3k5/9/9/p1p1p1p1p/9/9/P1P1P1P1P/9/9/4K4" + Tail }; // 5 + 5 soldiers on their start ranks
    }

    [Theory]
    [MemberData(nameof(LegalControlFens))]
    public void LegalControlFenParses(string fen)
    {
        Assert.Equal(fen, Board.FromFen(fen).ToFen());
    }

    [Fact]
    public async Task ApiRejectsIllegalFenAndOverflowClockWith400()
    {
        using var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>();
        using var client = ContinueHarness.Configured(factory);
        foreach (var fen in new[] { "3k5/9/9/9/9/9/9/9/9/9 w - - 0 1", "4k4/9/9/9/9/9/9/9/9/4K4 w - - 0 1",
                     "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 99999999999 1" })
        {
            foreach (var url in new[] { "/api/continue/parse", "/api/continue/sessions" })
            {
                using var res = await client.PostAsJsonAsync(url, new { fen, moves = "" });
                Assert.Equal(System.Net.HttpStatusCode.BadRequest, res.StatusCode);
            }
        }
    }

    [Theory]
    [MemberData(nameof(IllegalFens))]
    public void IllegalFenThrowsFormatException(string fen)
    {
        Assert.Throws<FormatException>(() => Board.FromFen(fen));
    }

    [Theory]
    [MemberData(nameof(IllegalFens))]
    public void GameAndBoardOnlyThrowFormatException(string fen)
    {
        Assert.Throws<FormatException>(() => Board.FromFen(fen));
        Assert.Throws<FormatException>(() => new Game(fen));
    }

    [Fact]
    public void StartFenParses()
    {
        Board board = Board.FromFen(Board.StartFen);
        Assert.Equal(Board.StartFen, board.ToFen());
        Assert.Equal(Color.Red, board.SideToMove);
    }

    [Fact]
    public void LegalMidgameFensParse()
    {
        const string horsesOut =
            "r1bakab1r/9/1cn3nc1/p1p1p1p1p/9/9/P1P1P1P1P/1CN3NC1/9/R1BAKAB1R w - - 0 1";
        const string cannonDeveloped =
            "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C2C4/9/RNBAKABNR b - - 1 1";

        Assert.Equal(horsesOut, Board.FromFen(horsesOut).ToFen());
        Assert.Equal(cannonDeveloped, Board.FromFen(cannonDeveloped).ToFen());
        Assert.Equal(Color.Black, new Game(cannonDeveloped).SideToMove);
    }
}
