using Xiangqi.Server;

namespace Xiangqi.Tests;

public class ContinueStoreTests
{
    [Fact]
    public void SessionsSurviveReopenAndTxidIsSingleUse()
    {
        string path = Path.Combine(Path.GetTempPath(), $"xq-store-{Guid.NewGuid():N}.db");
        var store = new ContinueStore(path);
        var s = new ContinueSession { Id = "s1", Token = "t", Nonce = "n", StartFen = "fen", Address = "kaspa:x", Moves = "h2e2", Status = "pending_payment" };
        store.Insert(s);
        Assert.True(store.TryConsumeTxid(s, new string('a', 64), 100_000_000, 12));

        var reopened = new ContinueStore(path); // simulates an app restart
        var loaded = reopened.Get("s1");
        Assert.NotNull(loaded);
        Assert.Equal("active", loaded!.Status);
        Assert.Equal("h2e2", loaded.Moves);
        Assert.True(reopened.IsTxidUsed(new string('a', 64)));
        var other = new ContinueSession { Id = "s2", Token = "t", Nonce = "n", StartFen = "fen", Status = "pending_payment" };
        reopened.Insert(other);
        Assert.False(reopened.TryConsumeTxid(other, new string('a', 64), 100_000_000, 12));
    }
}
