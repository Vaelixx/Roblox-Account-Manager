namespace RobloxAccountManager.Models;

public class GameServer
{
    public string Id { get; set; } = "";
    public int Playing { get; set; }
    public int MaxPlayers { get; set; }
    public double Fps { get; set; }
    public int Ping { get; set; }
    public bool IsVip { get; set; }

    public string PlayingDisplay => $"{Playing}/{MaxPlayers}";
    public string FpsDisplay => Fps > 0 ? Fps.ToString("F0") : "—";
    public string PingDisplay => Ping > 0 ? $"{Ping} ms" : "—";
    public string Type => IsVip ? "Private" : "Public";
    public string ShortId => Id.Length > 8 ? Id.Substring(0, 8) : Id;

    // Fill fraction 0..1 for the little capacity meter.
    public double Fill => MaxPlayers > 0 ? Math.Clamp((double)Playing / MaxPlayers, 0, 1) : 0;

    /// <summary>Readable fallback if a control ever shows the raw object.</summary>
    public override string ToString() => $"{ShortId} · {PlayingDisplay}";
}
