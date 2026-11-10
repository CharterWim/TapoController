// Prover-kant van SPAKE2+ (RFC 9383) op P-256 met SHA-256, HKDF-SHA256 en HMAC-SHA256,
// zoals TPAP inlogt. Overgezet van de Rust-crate `tapo` (tpap_spake2p.rs).

using System.Security.Cryptography;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Math.EC;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;
using ECPoint = Org.BouncyCastle.Math.EC.ECPoint;

namespace Tuinverlichting.Tapo.Tpap;

sealed class Proof
{
    public required byte[] Share { get; init; }          // X, ongecomprimeerd (65 bytes)
    public required byte[] Confirm { get; init; }        // HMAC(KcA, Y)
    public required byte[] SharedKey { get; init; }      // basis voor sessiesleutels
    public required byte[] VerifierConfirmKey { get; init; }

    public bool Verify(byte[] deviceConfirm) =>
        CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(VerifierConfirmKey, Share), deviceConfirm);
}

static class Spake2Plus
{
    static readonly X9ECParameters Curve = ECNamedCurveTable.GetByName("P-256");
    static readonly ECPoint M = Curve.Curve.DecodePoint(Convert.FromHexString(
        "02886e2f97ace46e55ba9dd7242579f2993b64e16ef3dcab95afd497333d8fa12f"));
    static readonly ECPoint N = Curve.Curve.DecodePoint(Convert.FromHexString(
        "03d8bbd6c639c62937b04d997f38c3770719c629d7014d49a24b4f98baa1292b49"));

    public static Proof Prove(string credential, byte[] salt, int iterations,
        byte[] userRandom, byte[] deviceRandom, byte[] deviceShare)
    {
        var (w0, w1) = DeriveW0W1(credential, salt, iterations);
        var x = BigIntegers.CreateRandomInRange(BigInteger.One, Curve.N.Subtract(BigInteger.One), new SecureRandom());
        var context = SHA256.HashData([.. "PAKE V1"u8, .. userRandom, .. deviceRandom]);
        return ProveWith(w0, w1, x, context, [], [], deviceShare);
    }

    public static (BigInteger W0, BigInteger W1) DeriveW0W1(string credential, byte[] salt, int iterations)
    {
        var key = Rfc2898DeriveBytes.Pbkdf2(System.Text.Encoding.UTF8.GetBytes(credential), salt, iterations,
            HashAlgorithmName.SHA256, 80);
        return (new BigInteger(1, key, 0, 40).Mod(Curve.N), new BigInteger(1, key, 40, 40).Mod(Curve.N));
    }

    public static Proof ProveWith(BigInteger w0, BigInteger w1, BigInteger x, byte[] context,
        byte[] proverId, byte[] verifierId, byte[] verifierShare)
    {
        var share = Curve.G.Multiply(x).Add(M.Multiply(w0)).Normalize();

        ECPoint y;
        try
        {
            y = Curve.Curve.DecodePoint(verifierShare).Normalize();
        }
        catch (ArgumentException)
        {
            throw new TapoException("dev_share van het apparaat is geen punt op P-256");
        }
        if (y.IsInfinity || !y.IsValid())
            throw new TapoException("dev_share van het apparaat is geen punt op P-256");

        var unmasked = y.Subtract(N.Multiply(w0));
        var z = unmasked.Multiply(x).Normalize();
        var v = unmasked.Multiply(w1).Normalize();
        if (z.IsInfinity || v.IsInfinity)
            throw new TapoException("dev_share leidt tot een niet-geheime sleutel");

        var shareBytes = share.GetEncoded(false);
        var yBytes = y.GetEncoded(false);

        var transcript = Transcript(
            context, proverId, verifierId,
            M.GetEncoded(false), N.GetEncoded(false),
            shareBytes, yBytes,
            z.GetEncoded(false), v.GetEncoded(false),
            BigIntegers.AsUnsignedByteArray(32, w0));
        var mainKey = SHA256.HashData(transcript);

        var confirmKeys = HKDF.DeriveKey(HashAlgorithmName.SHA256, mainKey, 64, [], "ConfirmationKeys"u8.ToArray());
        var sharedKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, mainKey, 32, [], "SharedKey"u8.ToArray());

        return new Proof
        {
            Share = shareBytes,
            Confirm = HMACSHA256.HashData(confirmKeys[..32], yBytes),
            SharedKey = sharedKey,
            VerifierConfirmKey = confirmKeys[32..],
        };
    }

    // Elk item voorafgegaan door zijn lengte als 8 bytes little-endian.
    static byte[] Transcript(params byte[][] items)
    {
        using var ms = new MemoryStream();
        foreach (var item in items)
        {
            ms.Write(BitConverter.GetBytes((ulong)item.Length)); // Windows/ARM: little-endian
            ms.Write(item);
        }
        return ms.ToArray();
    }
}
