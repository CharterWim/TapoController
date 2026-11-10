// AES-128-CCM-datakanaal van TPAP (tag 16 bytes, nonce 12 bytes).
// Body = seq (4 bytes big-endian) + ciphertext + tag. Nonce = eerste 8 bytes van de basis-IV + seq.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Tuinverlichting.Tapo.Tpap;

sealed class TpapCipher : IDisposable
{
    const int TagLength = 16;
    readonly AesCcm _aes;
    readonly byte[] _baseNonce;
    int _seq;

    public TpapCipher(byte[] sharedKey, int startSeq)
    {
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedKey, 16,
            "tp-kdf-salt-aes128-key"u8.ToArray(), "tp-kdf-info-aes128-key"u8.ToArray());
        _baseNonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedKey, 12,
            "tp-kdf-salt-aes128-iv"u8.ToArray(), "tp-kdf-info-aes128-iv"u8.ToArray());
        _aes = new AesCcm(key);
        _seq = startSeq;
    }

    public (byte[] Body, int Seq) Encrypt(string json)
    {
        int seq = Interlocked.Increment(ref _seq) - 1;
        var plain = Encoding.UTF8.GetBytes(json);
        var body = new byte[4 + plain.Length + TagLength];
        BinaryPrimitives.WriteInt32BigEndian(body, seq);
        _aes.Encrypt(Nonce(seq), plain, body.AsSpan(4, plain.Length), body.AsSpan(4 + plain.Length, TagLength));
        return (body, seq);
    }

    public string Decrypt(int seq, byte[] body)
    {
        if (body.Length < 4 + TagLength)
            throw new TapoException($"Antwoord van {body.Length} bytes is te kort");
        int bodySeq = BinaryPrimitives.ReadInt32BigEndian(body);
        if (bodySeq != seq)
            throw new TapoException($"Antwoord heeft seq {bodySeq}, request had {seq}");

        int len = body.Length - 4 - TagLength;
        var plain = new byte[len];
        _aes.Decrypt(Nonce(seq), body.AsSpan(4, len), body.AsSpan(4 + len, TagLength), plain);
        return Encoding.UTF8.GetString(plain);
    }

    byte[] Nonce(int seq)
    {
        var nonce = new byte[12];
        _baseNonce.AsSpan(0, 8).CopyTo(nonce);
        BinaryPrimitives.WriteInt32BigEndian(nonce.AsSpan(8), seq);
        return nonce;
    }

    public void Dispose() => _aes.Dispose();
}
