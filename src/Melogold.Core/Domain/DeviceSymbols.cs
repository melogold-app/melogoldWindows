namespace Melogold.Core.Domain;

/// <summary>Вид устройства аккаунта по его <c>platform</c> (API §1.6): значок и подпись для экранного диктора.</summary>
public enum DeviceKind
{
    Phone,
    Tablet,
    Computer,
    Watch,
    Headset,
    Other,
}

/// <summary>
/// Значок устройства (tasks/0006 §1, как <c>DeviceSymbol</c> Clementine) — один на всё приложение: список устройств,
/// фильтр Истории, одобрение входа. Сравнение — по точному значению в нижнем регистре; незнакомое — «Устройство».
/// </summary>
public static class DeviceSymbols
{
    public static DeviceKind Kind(string? platform) => platform?.Trim().ToLowerInvariant() switch
    {
        "android" or "ios" => DeviceKind.Phone,
        "ipados" => DeviceKind.Tablet,
        "macos" or "windows" or "linux" => DeviceKind.Computer,
        "watchos" => DeviceKind.Watch,
        "visionos" => DeviceKind.Headset,
        _ => DeviceKind.Other,
    };

    /// <summary>Глиф Segoe Fluent Icons: телефон, планшет, компьютер, часы (секундомер с головкой), гарнитура.</summary>
    public static string Glyph(string? platform) => Kind(platform) switch
    {
        DeviceKind.Phone => "",
        DeviceKind.Tablet => "",
        DeviceKind.Watch => "",
        DeviceKind.Headset => "",
        _ => "",
    };
}

/// <summary>
/// Код входа, который показывает новое устройство (API §1.6 <c>UserCode</c>): алфавит Крокфорда, ввод — верхний регистр,
/// без пробелов, <c>-</c> и <c>_</c>, <c>O→0</c>, <c>I,L→1</c>; ровно 8 символов; вывод <c>XXXX-XXXX</c>.
/// </summary>
public static class UserCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Нормализованный код <c>XXXX-XXXX</c>; null — это не код.</summary>
    public static string? Normalize(string? input)
    {
        if (input is null) return null;
        var chars = input.ToUpperInvariant()
            .Where(ch => ch is not (' ' or '-' or '_') && !char.IsWhiteSpace(ch))
            .Select(ch => ch switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                _ => ch,
            })
            .ToArray();
        if (chars.Length != 8 || chars.Any(ch => !Alphabet.Contains(ch))) return null;
        return $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}";
    }
}
