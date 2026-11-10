using System.Text.Json.Nodes;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Utilities;
using Tuinverlichting.Tapo.Tpap;

namespace Tuinverlichting.Tapo.Tests;

// Testvectoren uit RFC 9383 en uit de Rust-crate `tapo` (tpap_spake2p.rs / tpap_cipher.rs).
public class CryptoTests
{
    static byte[] H(string hex) => Convert.FromHexString(hex);
    static BigInteger S(string hex) => new(1, H(hex));

    [Fact]
    public void Spake2Plus_matches_RFC9383_vector()
    {
        var proof = Spake2Plus.ProveWith(
            S("bb8e1bbcf3c48f62c08db243652ae55d3e5586053fca77102994f23ad95491b3"),
            S("7e945f34d78785b8a3ef44d0df5a1a97d6b3b460409a345ca7830387a74b1dba"),
            S("d1232c8e8693d02368976c174e2088851b8365d0d79a9eee709c6a05a2fad539"),
            "SPAKE2+-P256-SHA256-HKDF-SHA256-HMAC-SHA256 Test Vectors"u8.ToArray(),
            "client"u8.ToArray(), "server"u8.ToArray(),
            H("04c0f65da0d11927bdf5d560c69e1d7d939a05b0e88291887d679fcadea75810fb" +
              "5cc1ca7494db39e82ff2f50665255d76173e09986ab46742c798a9a68437b048"));

        Assert.Equal(H("04ef3bd051bf78a2234ec0df197f7828060fe9856503579bb1733009042c15c0c1" +
                       "de127727f418b5966afadfdd95a6e4591d171056b333dab97a79c7193e341727"), proof.Share);
        Assert.Equal(H("926cc713504b9b4d76c9162ded04b5493e89109f6d89462cd33adc46fda27527"), proof.Confirm);
        Assert.Equal(H("0c5f8ccd1413423a54f6c1fb26ff01534a87f893779c6e68666d772bfd91f3e7"), proof.SharedKey);
        Assert.True(proof.Verify(H("9747bcc4f8fe9f63defee53ac9b07876d907d55047e6ff2def2e7529089d3e68")));
        Assert.False(proof.Verify(proof.Confirm));
    }

    [Fact]
    public void Spake2Plus_rejects_share_not_on_curve()
    {
        var share = new byte[65];
        share[0] = 0x04;
        Array.Fill(share, (byte)1, 1, 64);
        Assert.Throws<TapoException>(() => Spake2Plus.Prove("x", new byte[16], 1, new byte[32], new byte[32], share));
    }

    [Fact]
    public void Credential_and_w0w1_match_known_values()
    {
        var cred = TpapClient.Credential("hunter2", JsonNode.Parse("""{"type":"password_shadow","params":{"passwd_id":2}}"""));
        Assert.Equal("f3bbbd66a63d4bf1747940578ec3d0103530e21d", cred);

        var (w0, w1) = Spake2Plus.DeriveW0W1(cred, "0123456789abcdef"u8.ToArray(), 3000);
        Assert.Equal(H("f01588c03f935783a7c530a83ab1ade1a3261b13005521b6946dc5c7ad259f2c"), BigIntegers.AsUnsignedByteArray(32, w0));
        Assert.Equal(H("de9bd4a17ba3b23e2a26fbf5ae94010c68f5e0e34388fe6ec227fcb45bd0b42e"), BigIntegers.AsUnsignedByteArray(32, w1));
    }

    [Fact]
    public void Credential_passwd_id_4_is_password_itself_and_unknown_is_rejected()
    {
        Assert.Equal("hunter2", TpapClient.Credential("hunter2", JsonNode.Parse("""{"type":"password_shadow","params":{"passwd_id":4}}""")));
        Assert.Throws<TapoException>(() => TpapClient.Credential("hunter2", JsonNode.Parse("""{"type":"password_shadow","params":{"passwd_id":3}}""")));
        Assert.Throws<TapoException>(() => TpapClient.Credential("hunter2", null));
    }

    [Fact]
    public void Cipher_matches_known_answer_and_round_trips()
    {
        using var cipher = new TpapCipher(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(), 0x01020304);
        const string msg = """{"method":"get_device_info"}""";
        var (body, seq) = cipher.Encrypt(msg);

        Assert.Equal("01020304c0331c1c36a5df4ce0c68e5a27b2667ebf842dc994a3fd24b9e860a60fab76d4b66b9b669d95cca10c012f13",
            Convert.ToHexStringLower(body));
        Assert.Equal(msg, cipher.Decrypt(seq, body));
    }

    [Fact]
    public void Cipher_rejects_response_for_other_sequence()
    {
        using var cipher = new TpapCipher(new byte[32], 7);
        var (first, _) = cipher.Encrypt("{}");
        var (_, second) = cipher.Encrypt("{}");
        Assert.Throws<TapoException>(() => cipher.Decrypt(second, first));
    }

    [Fact]
    public void Session_errors_are_not_credential_problems()
    {
        Assert.True(new TapoException("x", -2203).IsCredentialProblem);
        Assert.False(new TapoException("x", -2203, sessionInvalid: true).IsCredentialProblem);
    }
}
