using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Melogold.Core.Domain;

/// <summary>
/// Доказательство работы при регистрации (API §4.3, векторы <c>spec/pow.vectors.json</c>): первый
/// <c>nonce</c> ("0", "1", …), для которого <c>sha256(UTF-8(challenge + ":" + nonce))</c> начинается
/// хотя бы с <c>bits</c> нулевых битов.
/// </summary>
public static class ProofOfWork
{
    public static string Solve(string challenge, int bits, CancellationToken cancellationToken = default)
    {
        var prefix = Encoding.UTF8.GetBytes(challenge + ":");
        Span<byte> buffer = stackalloc byte[prefix.Length + 20];
        prefix.CopyTo(buffer);
        Span<byte> hash = stackalloc byte[32];
        for (long nonce = 0; ; nonce++)
        {
            if ((nonce & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            nonce.TryFormat(buffer[prefix.Length..], out var written, provider: System.Globalization.CultureInfo.InvariantCulture);
            SHA256.HashData(buffer[..(prefix.Length + written)], hash);
            if (LeadingZeroBits(hash) >= bits) return nonce.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public static int LeadingZeroBits(ReadOnlySpan<byte> hash)
    {
        var count = 0;
        foreach (var b in hash)
        {
            if (b == 0)
            {
                count += 8;
                continue;
            }
            return count + BitOperations.LeadingZeroCount((uint)b) - 24;
        }
        return count;
    }

    public static string Digest(string challenge, string nonce) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(challenge + ":" + nonce)));
}
