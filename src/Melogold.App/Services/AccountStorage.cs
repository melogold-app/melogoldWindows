using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Melogold.Server;
using Microsoft.Win32;

namespace Melogold.App.Services;

/// <summary>
/// Сессия на сервере в файле <c>account.dat</c>, зашифрованном DPAPI для текущего пользователя Windows: токены не
/// читаются ни другим пользователем, ни с копии диска. Файл не переносится резервной копией библиотеки.
/// </summary>
public sealed class DpapiSessionStore(string path) : ISessionStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("melogold-session-v1");

    public StoredSession? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = Unprotect(File.ReadAllBytes(path));
            return JsonSerializer.Deserialize<StoredSession>(json, MelogoldApi.Json);
        }
        catch (Exception e) when (e is IOException or JsonException or CryptographicException or UnauthorizedAccessException)
        {
            Log.Warn("Account file unreadable", e);
            return null;
        }
    }

    public void Save(StoredSession? session)
    {
        try
        {
            if (session is null)
            {
                File.Delete(path);
                return;
            }
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, Protect(JsonSerializer.SerializeToUtf8Bytes(session, MelogoldApi.Json)));
            File.Move(temp, path, true);
        }
        catch (Exception e) when (e is IOException or CryptographicException or UnauthorizedAccessException)
        {
            Log.Error("Account file not saved", e);
        }
    }

    // ---------- DPAPI (CryptProtectData, область пользователя) ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private const int UiForbidden = 0x1;

    private static byte[] Protect(byte[] data) => Transform(data, true);

    private static byte[] Unprotect(byte[] data) => Transform(data, false);

    private static byte[] Transform(byte[] data, bool protect)
    {
        var input = GCHandle.Alloc(data, GCHandleType.Pinned);
        var entropy = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var inputBlob = new DataBlob { Size = data.Length, Data = input.AddrOfPinnedObject() };
            var entropyBlob = new DataBlob { Size = Entropy.Length, Data = entropy.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref inputBlob, "Melogold", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            input.Free();
            entropy.Free();
        }
    }
}

/// <summary>
/// Что Windows сообщает о себе серверу (API §4.1 <c>DeviceInput</c>): имя компьютера, «Windows 11 (26200)», модель из
/// BIOS. <c>platformId</c> для hwid — <c>MachineGuid|installSalt</c> (API §1.6): соль случайная и живёт в папке данных,
/// поэтому переустановка программы с сохранёнными данными — то же устройство.
/// </summary>
public sealed class WindowsDeviceIdentity : IDeviceIdentity
{
    public WindowsDeviceIdentity()
    {
        PlatformId = MachineGuid() + "|" + InstallSalt();
        var build = Environment.OSVersion.Version.Build;
        OsVersion = $"{(build >= 22000 ? "Windows 11" : "Windows 10")} ({build})";
        Model = BiosModel();
    }

    public string PlatformId { get; }

    public string DeviceName => Environment.MachineName;

    public string? OsVersion { get; }

    public string? Model { get; }

    public string ClientVersion => AppInfo.Version;

    private static string MachineGuid()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (key?.GetValue("MachineGuid") is string guid && guid.Length > 0) return guid;
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            Log.Warn("MachineGuid unavailable", e);
        }
        return "";
    }

    private static string InstallSalt()
    {
        var path = AppPaths.InstallSalt;
        try
        {
            if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: 32 } saved) return saved;
            var salt = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            File.WriteAllText(path, salt);
            return salt;
        }
        catch (IOException e)
        {
            Log.Warn("Install salt not saved", e);
            return "";
        }
    }

    private static string? BiosModel()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            var maker = (key?.GetValue("SystemManufacturer") as string)?.Trim();
            var product = (key?.GetValue("SystemProductName") as string)?.Trim();
            var model = string.Join(" ", new[] { maker, product }.Where(s => !string.IsNullOrEmpty(s) && !s.Contains("To be filled", StringComparison.OrdinalIgnoreCase)));
            return model.Length > 0 ? model : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
