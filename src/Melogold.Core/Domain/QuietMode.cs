namespace Melogold.Core.Domain;

/// <summary>
/// Тихий запуск для проверок (<c>tools/shot.ps1</c>, переменная <c>MELOGOLD_QUIET=1</c>): окно за правым краем всех
/// экранов, без фокуса и без кнопки на панели задач, без звука, без медиапанели Windows и без уведомлений. Пользователь
/// в это время играет или слушает свой Melogold, и проверка не должна ему мешать (2026-09-26). Только в отладочной сборке.
/// </summary>
public static class QuietMode
{
#if DEBUG
    public static bool IsOn { get; } = Environment.GetEnvironmentVariable("MELOGOLD_QUIET") == "1";
#else
    public static bool IsOn => false;
#endif
}
