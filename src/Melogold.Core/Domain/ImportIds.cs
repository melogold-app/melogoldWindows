using System.Security.Cryptography;
using System.Text;

namespace Melogold.Core.Domain;

/// <summary>
/// id прослушиваний из копии (docs/spec/backup-format.md §3, Android <c>ImportIds</c>): UUIDv5 строки
/// <c>"videoId|timestampMs|playTimeMs"</c> в пространстве <see cref="Namespace"/>. Одна копия, импортированная на двух
/// устройствах, даёт те же id, и сервер не удваивает историю. Векторы: <c>docs/spec/import-ids.vectors.json</c>.
/// </summary>
public static class ImportIds
{
    /// <summary><c>NS_MELOGOLD_IMPORT</c>.</summary>
    public static readonly Guid Namespace = Guid.Parse("4a3b8c8a-1d9c-48f2-938b-3077d54ab4fb");

    /// <summary>id прослушивания; <paramref name="playTimeMs"/> — уже зажатое в 1…86 400 000.</summary>
    public static string EventId(string videoId, long timestampMs, long playTimeMs) =>
        Uuid5(Namespace, $"{videoId}|{timestampMs}|{playTimeMs}").ToString();

    /// <summary>RFC 9562, версия 5: SHA-1 от пространства (байты в сетевом порядке) и имени.</summary>
    public static Guid Uuid5(Guid space, string name)
    {
        var bytes = space.ToByteArray(bigEndian: true);
        var digest = SHA1.HashData([.. bytes, .. Encoding.UTF8.GetBytes(name)]);
        digest[6] = (byte)((digest[6] & 0x0f) | 0x50);
        digest[8] = (byte)((digest[8] & 0x3f) | 0x80);
        return new Guid(digest.AsSpan(0, 16), bigEndian: true);
    }
}
