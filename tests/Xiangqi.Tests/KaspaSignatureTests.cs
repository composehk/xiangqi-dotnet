using Xiangqi.Server.Kaspa;

namespace Xiangqi.Tests;

public class KaspaSignatureTests
{
    // Vectors from rusty-kaspa wallet/core/src/message.rs (test_sign_and_verify_test_case_0/1).
    private const string PubKey0 = "F9308A019258C31049344F85F89D5229B531C845836F99B08601F113BCE036F9";
    private const string Sig0 =
        "40B9BB2BE0AE02607279EDA64015A8D86E37632791703 40B8243F7CE5344D77AFF1191598BAF2FD26149CAC3B4B12C2C433261C00834DB6098CB172AA48EF522";
    private const string PubKey1 = "DFF1D77F2A671C5F36183726DB2341BE58FEAE1DA2DECED843240F7B502BA659";
    private const string Priv1 = "B7E151628AED2A6ABF7158809CF4F3C762E7160F38B4DA56A784D9045190CFEF";
    private const string Sig1 =
        "EB9E8A3C547EB91B6A7592644F328F0648BDD21ABA3CD44787D429D4D790AA8B962745691F3B472ED8D65F3B770ECB4F777BD17B1D309100919B53E0E206B4C6";

    private static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    [Fact]
    public void RustyKaspaVector0_Verifies()
    {
        string address = KaspaMessageSigner.AddressFromXOnly(Hex(PubKey0), "mainnet");
        var check = KaspaMessageSigner.Verify(address, "Hello Kaspa!", Sig0.Replace(" ", ""), "mainnet");
        Assert.True(check.Valid, check.Error);
    }

    [Fact]
    public void RustyKaspaVector1_SignsIdenticallyAndVerifies()
    {
        byte[] aux = new byte[32];
        aux[31] = 1;
        byte[] sig = KaspaMessageSigner.Sign("Hello Kaspa!", Hex(Priv1), aux);
        Assert.Equal(Sig1, Convert.ToHexString(sig));
        Assert.Equal(PubKey1, Convert.ToHexString(KaspaMessageSigner.XOnlyPublicKey(Hex(Priv1))));
        string address = KaspaMessageSigner.AddressFromXOnly(Hex(PubKey1), "mainnet");
        Assert.True(KaspaMessageSigner.Verify(address, "Hello Kaspa!", Convert.ToBase64String(sig), "mainnet").Valid);
    }

    [Fact]
    public void GeneratedSignature_RoundTripsAndRejectsTampering()
    {
        byte[] priv = Hex(Priv1);
        string address = KaspaMessageSigner.AddressFromXOnly(KaspaMessageSigner.XOnlyPublicKey(priv), "testnet-10");
        Assert.StartsWith("kaspatest:q", address);
        string msg = "XiangqiArena:Continue:abc:1700000000";
        string sig = Convert.ToHexString(KaspaMessageSigner.Sign(msg, priv)).ToLowerInvariant();
        Assert.True(KaspaMessageSigner.Verify(address, msg, sig, "testnet-10").Valid);
        Assert.False(KaspaMessageSigner.Verify(address, msg + "x", sig, "testnet-10").Valid);
        Assert.False(KaspaMessageSigner.Verify(address, msg, sig, "mainnet").Valid); // wrong network prefix
        string other = KaspaMessageSigner.AddressFromXOnly(Hex(PubKey0), "testnet-10");
        Assert.False(KaspaMessageSigner.Verify(other, msg, sig, "testnet-10").Valid);
        Assert.False(KaspaMessageSigner.Verify(address, msg, "zz", "testnet-10").Valid);
    }
}
