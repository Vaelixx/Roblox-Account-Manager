namespace RobloxAccountManager.Services;

/// <summary>
/// Where a new client window goes: the first free cell of a grid of window-sized cells, filled
/// left to right, top to bottom, within the screen's work area. Free means no other client
/// window's top-left corner is inside the cell. When every cell is taken it starts over at the
/// top-left (windows then overlap, which Roblox doesn't mind; minimizing is what stops it).
/// </summary>
public static class WindowLayout
{
    public readonly record struct Rect(int X, int Y, int W, int H);

    public static (int X, int Y) FirstFreeCell(Rect area, int w, int h, IEnumerable<(int X, int Y)> others)
    {
        w = Math.Max(1, w);
        h = Math.Max(1, h);
        int cols = Math.Max(1, area.W / w);
        int rows = Math.Max(1, area.H / h);
        var taken = others.ToList();

        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                int x = area.X + c * w, y = area.Y + r * h;
                if (!taken.Any(o => o.X >= x && o.X < x + w && o.Y >= y && o.Y < y + h))
                    return (x, y);
            }
        return (area.X, area.Y);
    }
}
