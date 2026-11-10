// Controleert de crypto tegen bekende testvectoren (RFC 9383 en die uit de Rust-crate `tapo`),
// zodat we zeker weten dat een mislukte login niet aan onze eigen berekening ligt.

using Org.BouncyCastle.Math;
using Org.BouncyCastle.Utilities;
using Tuinverlichting.Tapo;
using Tuinverlichting.Tapo.Tpap;

static class SelfTest
{
    public static bool Run()
    {
        int failed = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine($"  {(ok ? "OK  " : "FOUT")} {name}");
            if (!ok) failed++;
        }
        static byte[] H(string hex) => Convert.FromHexString(hex);
        static BigInteger S(string hex) => new BigInteger(1, H(hex));

        Console.WriteLine("SPAKE2+ (RFC 9383-testvector):");
        var proof = Spake2Plus.ProveWith(
            S("bb8e1bbcf3c48f62c08db243652ae55d3e5586053fca77102994f23ad95491b3"),
            S("7e945f34d78785b8a3ef44d0df5a1a97d6b3b460409a345ca7830387a74b1dba"),
            S("d1232c8e8693d02368976c174e2088851b8365d0d79a9eee709c6a05a2fad539"),
            "SPAKE2+-P256-SHA256-HKDF-SHA256-HMAC-SHA256 Test Vectors"u8.ToArray(),
            "client"u8.ToArray(), "server"u8.ToArray(),
            H("04c0f65da0d11927bdf5d560c69e1d7d939a05b0e88291887d679fcadea75810fb" +
              "5cc1ca7494db39e82ff2f50665255d76173e09986ab46742c798a9a68437b048"));
        Check("share X", proof.Share.AsSpan().SequenceEqual(H(
            "04ef3bd051bf78a2234ec0df197f7828060fe9856503579bb1733009042c15c0c1" +
            "de127727f418b5966afadfdd95a6e4591d171056b333dab97a79c7193e341727")));
        Check("confirm", proof.Confirm.AsSpan().SequenceEqual(H("926cc713504b9b4d76c9162ded04b5493e89109f6d89462cd33adc46fda27527")));
        Check("shared key", proof.SharedKey.AsSpan().SequenceEqual(H("0c5f8ccd1413423a54f6c1fb26ff01534a87f893779c6e68666d772bfd91f3e7")));
        Check("verifier confirm", proof.Verify(H("9747bcc4f8fe9f63defee53ac9b07876d907d55047e6ff2def2e7529089d3e68")));
        Check("eigen confirm wordt afgewezen", !proof.Verify(proof.Confirm));

        Console.WriteLine("Wachtwoord -> w0/w1:");
        var cred = TpapClient.Credential("hunter2", System.Text.Json.Nodes.JsonNode.Parse(
            """{"type":"password_shadow","params":{"passwd_id":2}}"""));
        Check("password_shadow 2 = SHA1-hex", cred == "f3bbbd66a63d4bf1747940578ec3d0103530e21d");
        var (w0, w1) = Spake2Plus.DeriveW0W1(cred, "0123456789abcdef"u8.ToArray(), 3000);
        Check("w0", BigIntegers.AsUnsignedByteArray(32, w0).AsSpan().SequenceEqual(H("f01588c03f935783a7c530a83ab1ade1a3261b13005521b6946dc5c7ad259f2c")));
        Check("w1", BigIntegers.AsUnsignedByteArray(32, w1).AsSpan().SequenceEqual(H("de9bd4a17ba3b23e2a26fbf5ae94010c68f5e0e34388fe6ec227fcb45bd0b42e")));

        Console.WriteLine("AES-128-CCM-datakanaal:");
        using var cipher = new TpapCipher(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(), 0x01020304);
        const string msg = """{"method":"get_device_info"}""";
        var (body, seq) = cipher.Encrypt(msg);
        Check("known answer", Convert.ToHexStringLower(body) ==
            "01020304c0331c1c36a5df4ce0c68e5a27b2667ebf842dc994a3fd24b9e860a60fab76d4b66b9b669d95cca10c012f13");
        Check("round trip", cipher.Decrypt(seq, body) == msg);

        Console.WriteLine(failed == 0 ? "\nAlle tests geslaagd." : $"\n{failed} test(s) mislukt.");
        return failed == 0;
    }
}
