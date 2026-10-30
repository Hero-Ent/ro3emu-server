namespace Ro3Emu.Panel;

/// <summary>The web control panel (Ro3Emu:Panel, docs/guide/panel.md).</summary>
public sealed class PanelOptions
{
    /// <summary>Serve the panel. Off: its port is not opened at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The panel's own HTTP port, next to the server's HttpPort.</summary>
    public int Port { get; set; } = 5381;

    /// <summary>Interface the panel listens on. Empty: the one the server's other ports use (loopback for local
    /// play). A reverse proxy on the same machine sets 127.0.0.1 so only the proxy reaches the panel.</summary>
    public string? BindAddress { get; set; }

    /// <summary>
    /// Addresses of reverse proxies whose X-Forwarded-For and X-Forwarded-Proto are believed: the client address for
    /// the login budget and https for the cookie's Secure flag. Empty: both headers are ignored, whoever sends them.
    /// </summary>
    public string[] TrustedProxies { get; set; } = [];

    /// <summary>Hours a panel login stays valid without use; each request extends it. Sessions live in memory and end
    /// when the server restarts.</summary>
    public int SessionHours { get; set; } = 12;

    /// <summary>Accounts can be created on the panel's sign-up page. Off: only the game's first login creates them.</summary>
    public bool Registration { get; set; } = true;

    /// <summary>
    /// The folder of the pictures the pages show (item, monster, class and map images), decoded from the operator's own
    /// client by the data export. Empty: icons/ in the game data folder. Without it the pages draw letters instead.
    /// </summary>
    public string? ImageDir { get; set; }
}
