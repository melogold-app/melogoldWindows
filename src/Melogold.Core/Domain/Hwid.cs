using System.Security.Cryptography;
using System.Text;

namespace Melogold.Core.Domain;

/// <summary>
/// Идентификатор устройства для сервера (API §1.6 <c>Hwid</c>, векторы <c>spec/hwid.vectors.json</c>):
/// <c>hex(sha256("melogold-hwid-v1|" + platformId + "|" + serverId))</c>. У каждого сервера свой hwid.
/// На Windows <c>platformId = MachineGuid + "|" + installSalt</c>.
/// </summary>
public static class Hwid
{
    private const string Prefix = "melogold-hwid-v1|";

    public static string Compute(string platformId, string serverId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Prefix + platformId + "|" + serverId));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary><c>platformId</c> Windows: <c>MachineGuid|installSalt</c>.</summary>
    public static string WindowsPlatformId(string machineGuid, string installSalt) => machineGuid + "|" + installSalt;
}
