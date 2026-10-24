using System.Reflection;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Xiangqi.Server;
using Xiangqi.Server.Kaspa;

namespace Xiangqi.Tests;

public class ContinueCleanupTests
{
    private sealed class AcceptingVerifier : IPaymentVerifier
    {
        public Task<PaymentCheck> VerifyAsync(string txid, string payerAddress, CancellationToken ct = default) =>
            Task.FromResult(new PaymentCheck(true, false, null, 100_000_000, 10, new[] { payerAddress }));
    }

    [Fact]
    public async Task CleanupCaches_DropsExpiredLocks_AndLeavesTheLiveSessionUsable()
    {
        var clock = new MutableClock();
        string path = Path.Combine(Path.GetTempPath(), $"xq-clean-{Guid.NewGuid():N}.db");
        var store = new ContinueStore(path);
        var options = new PaymentOptions
        {
            PayToAddress = PaymentVerifierTests.PayTo,
            Network = "mainnet",
            SessionTtlMinutes = 60,
            ExpiredPaymentGraceMinutes = 10,
            PaymentMinIntervalSeconds = 2,
            PriceSompi = 100_000_000,
            MinConfirmations = 1,
        };
        var svc = new ContinueService(store, new AcceptingVerifier(), options, NullLogger<ContinueService>.Instance, clock);

        ContinueSessionDto stale = svc.Create(new CreateContinueRequest(null, null, null, "easy", "red"));
        TouchLock(svc, stale, clock);

        ContinueSessionDto live = svc.Create(new CreateContinueRequest(null, null, null, "easy", "red"));
        long ts = clock.UnixSeconds;
        string address = ContinueHarness.AddressOf(ContinueHarness.Priv);
        string sig = Convert.ToHexString(KaspaMessageSigner.Sign(ContinueService.SignMessage(live.Id, ts), ContinueHarness.Priv));
        Assert.Equal("pending_payment", svc.Authorize(live.Id, new ContinueAuthRequest(live.Token, address, sig, ts)).Status);
        var (paid, _) = await svc.SubmitPaymentAsync(live.Id, new ContinuePaymentRequest(live.Token, new string('a', 64)), CancellationToken.None);
        Assert.Equal(200, paid);

        int locksBefore = DictCount(svc, "_locks");
        Assert.True(locksBefore >= 2);
        // Payment lookups are stamped with Environment.TickCount64. Age one so the 10× interval rule applies
        // without waiting on the wall clock (the session clock does not drive this map).
        Lookups(svc)[live.Id] = Environment.TickCount64 - (10L * options.PaymentMinIntervalSeconds * 1000L) - 1;
        int lookupsBefore = DictCount(svc, "_lastLookup");
        Assert.True(lookupsBefore >= 1);

        clock.Advance(TimeSpan.FromMinutes(60 + 10).Add(TimeSpan.FromSeconds(1)));

        int removed = svc.CleanupCaches();
        Assert.True(removed > 0);
        Assert.Equal(0, svc.CleanupCaches());
        Assert.True(DictCount(svc, "_locks") < locksBefore);
        Assert.True(DictCount(svc, "_lastLookup") < lookupsBefore);
        Assert.False(LockIds(svc).Contains(stale.Id));
        Assert.Contains(live.Id, LockIds(svc));

        ContinueSessionDto fetched = svc.Get(live.Id);
        Assert.Equal("active", fetched.Status);
        Assert.Equal(live.Id, fetched.Id);
    }

    /// <summary>Invalid signature still allocates the per-session gate, then releases it.</summary>
    private static void TouchLock(ContinueService svc, ContinueSessionDto session, MutableClock clock)
    {
        try
        {
            svc.Authorize(session.Id, new ContinueAuthRequest(session.Token, "kaspa:not-an-address", new string('0', 128), clock.UnixSeconds));
        }
        catch (Exception)
        {
            // Expected: the lock entry remains after the gate is released.
        }
    }

    private static int DictCount(ContinueService svc, string fieldName)
    {
        object dict = typeof(ContinueService).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(svc)!;
        return (int)dict.GetType().GetProperty("Count")!.GetValue(dict)!;
    }

    private static HashSet<string> LockIds(ContinueService svc)
    {
        var dict = (ConcurrentDictionary<string, SemaphoreSlim>)typeof(ContinueService)
            .GetField("_locks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(svc)!;
        return dict.Keys.ToHashSet();
    }

    private static ConcurrentDictionary<string, long> Lookups(ContinueService svc) =>
        (ConcurrentDictionary<string, long>)typeof(ContinueService)
            .GetField("_lastLookup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(svc)!;
}
