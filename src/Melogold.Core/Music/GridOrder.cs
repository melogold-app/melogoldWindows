namespace Melogold.Core.Music;

/// <summary>
/// Популярные треки исполнителя сеткой в несколько колонок (tasks/0024): места по порядку идут вниз по колонке, как в
/// Apple Music, а сетка Windows раскладывает по строкам. <see cref="ColumnMajor"/> даёт, какой по счёту трек стоит на
/// каждом месте при раскладке по строкам. Короче — правые колонки, на одну строку: пустые места только в конце
/// последней строки, и раскладка по строкам их не сдвигает.
/// </summary>
public static class GridOrder
{
    public static int[] ColumnMajor(int count, int columns)
    {
        if (count <= 0) return [];
        columns = Math.Clamp(columns, 1, count);
        var rows = (count + columns - 1) / columns;
        // Полных колонок (по rows треков); остальные — на один короче
        var full = count - (rows - 1) * columns;
        int Start(int column) => column < full ? column * rows : full * rows + (column - full) * (rows - 1);
        int Length(int column) => column < full ? rows : rows - 1;
        var order = new List<int>(count);
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                if (row < Length(column)) order.Add(Start(column) + row);
            }
        }
        return [.. order];
    }
}
