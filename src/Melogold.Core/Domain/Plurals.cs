namespace Melogold.Core.Domain;

/// <summary>Форма множественного числа для ресурсов <c>Ключ_one/few/many/other</c> (<c>Loc.Plural</c>).</summary>
public static class Plurals
{
    /// <summary>Русский: one (1, 21), few (2–4, 22–24), many (5–20, 25…); английский: one (1) и other.</summary>
    public static string Form(long count, bool russian)
    {
        if (!russian) return count == 1 ? "one" : "other";
        var n10 = count % 10;
        var n100 = count % 100;
        return n10 == 1 && n100 != 11 ? "one" : n10 is >= 2 and <= 4 && n100 is < 12 or > 14 ? "few" : "many";
    }
}
