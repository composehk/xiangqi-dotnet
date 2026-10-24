using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Xiangqi.Server;

namespace Xiangqi.Tests;

public class ContinueRaceTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ContinueRaceTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task ReplayOfOwnTxidIs200AndOtherSessionIs409()
    {
        using var client = ContinueHarness.Configured(_factory);
        var session = await ContinueHarness.CreateSigned(client);
        string txid = new string('a', 64);

        var first = await ContinueHarness.Pay(client, session, txid);
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal("active", first.Body.GetProperty("status").GetString());
        Assert.Equal(txid, first.Body.GetProperty("txid").GetString());

        var again = await ContinueHarness.Pay(client, session, txid);
        Assert.Equal(HttpStatusCode.OK, again.Status);
        Assert.Equal("active", again.Body.GetProperty("status").GetString());
        Assert.Equal(txid, again.Body.GetProperty("txid").GetString());

        var other = await ContinueHarness.CreateSigned(client);
        var conflict = await ContinueHarness.Pay(client, other, txid);
        Assert.Equal(HttpStatusCode.Conflict, conflict.Status);

        var loaded = await ContinueHarness.GetSession(client, session);
        Assert.Equal("active", loaded.Body.GetProperty("status").GetString());
        Assert.Equal(txid, loaded.Body.GetProperty("txid").GetString());
    }

    [Fact]
    public async Task PaymentAndAuthRaceLeavesSessionActive()
    {
        using var client = ContinueHarness.Configured(_factory, settings: new[] { ("Payment:CreatePerMinute", "100000"), ("Payment:ActionPerMinute", "100000") });
        for (int round = 0; round < 25; round++)
        {
            var session = await ContinueHarness.CreateSigned(client);
            string txid = round.ToString("x64");
            var tasks = new List<Task<(HttpStatusCode Status, System.Text.Json.JsonElement Body)>>
            {
                ContinueHarness.Pay(client, session, txid),
            };
            for (int i = 0; i < 12; i++)
                tasks.Add(ContinueHarness.Auth(client, session));

            var results = await Task.WhenAll(tasks);
            Assert.True(results[0].Status is HttpStatusCode.OK or HttpStatusCode.Accepted,
                $"round {round} payment {(int)results[0].Status}");
            for (int i = 1; i < results.Length; i++)
                Assert.True(results[i].Status is HttpStatusCode.OK or HttpStatusCode.Conflict,
                    $"round {round} auth {(int)results[i].Status}");

            var loaded = await ContinueHarness.GetSession(client, session);
            Assert.Equal("active", loaded.Body.GetProperty("status").GetString());
            Assert.Equal(txid, loaded.Body.GetProperty("txid").GetString());

            var replay = await ContinueHarness.Pay(client, session, txid);
            Assert.True(replay.Status is HttpStatusCode.OK or HttpStatusCode.Conflict);
            var after = await ContinueHarness.GetSession(client, session);
            Assert.Equal("active", after.Body.GetProperty("status").GetString());
            Assert.Equal(txid, after.Body.GetProperty("txid").GetString());
        }
    }

    [Fact]
    public async Task SameTxidAcrossTwoSessionsHasOneWinner()
    {
        using var client = ContinueHarness.Configured(_factory, settings: new[] { ("Payment:CreatePerMinute", "100000"), ("Payment:ActionPerMinute", "100000") });
        for (int round = 0; round < 25; round++)
        {
            var a = await ContinueHarness.CreateSigned(client);
            var b = await ContinueHarness.CreateSigned(client);
            string txid = (round + 100).ToString("x64");
            var results = await Task.WhenAll(ContinueHarness.Pay(client, a, txid), ContinueHarness.Pay(client, b, txid));
            var codes = results.Select(r => r.Status).OrderBy(c => c).ToArray();
            Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, codes);
        }
    }

    [Fact]
    public void TryAuthorizeAndConditionalConsume()
    {
        string path = Path.Combine(Path.GetTempPath(), $"xq-cas-{Guid.NewGuid():N}.db");
        var store = new ContinueStore(path);
        var active = new ContinueSession
        {
            Id = "s-active", Token = "t", Nonce = "n", StartFen = "fen", Status = "active", Address = "kaspa:x",
        };
        store.Insert(active);
        active.Timestamp = 5;
        active.Address = "kaspa:other";
        active.Signature = "sig";
        active.UpdatedAt = 6;
        Assert.False(store.TryAuthorize(active));
        var loaded = store.Get("s-active");
        Assert.Equal("active", loaded!.Status);
        Assert.Equal("kaspa:x", loaded.Address);

        string blocked = new string('b', 64);
        Assert.False(store.TryConsumeTxid(active, blocked, 100_000_000, 12));
        Assert.Equal(ConsumeResult.SessionNotPayable, store.TryConsumeTxid(active, blocked, 100_000_000, 12, requirePendingPayment: true));
        Assert.False(store.IsTxidUsed(blocked));
        Assert.Null(store.GetTxidSession(blocked));
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM payments WHERE txid=$t", blocked));
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM used_txids WHERE txid=$t", blocked));

        var payable = new ContinueSession
        {
            Id = "s-pay", Token = "t", Nonce = "n", StartFen = "fen", Status = "pending_payment", Address = "kaspa:x",
        };
        store.Insert(payable);
        string ok = new string('c', 64);
        Assert.Equal(ConsumeResult.Ok, store.TryConsumeTxid(payable, ok, 100_000_000, 12, requirePendingPayment: true));
        Assert.Equal("active", store.Get("s-pay")!.Status);
        Assert.Equal("s-pay", store.GetTxidSession(ok));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM payments WHERE txid=$t", ok));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM used_txids WHERE txid=$t", ok));

        var unsigned = new ContinueSession
        {
            Id = "s-auth", Token = "t", Nonce = "n", StartFen = "fen", Status = "pending_auth",
        };
        store.Insert(unsigned);
        string fresh = new string('d', 64);
        Assert.Equal(ConsumeResult.SessionNotPayable, store.TryConsumeTxid(unsigned, fresh, 1, 1, requirePendingPayment: true));
        Assert.False(store.IsTxidUsed(fresh));
        Assert.Null(store.GetTxidSession(fresh));
    }

    private static long Scalar(string path, string sql, string txid)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$t", txid);
        return (long)cmd.ExecuteScalar()!;
    }
}
