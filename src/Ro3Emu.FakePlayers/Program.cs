using Ro3Emu.FakePlayers;

// Fake players for a running server (docs/guide/fake-players.md). Normally the server starts this process itself for the
// GM command "!fakeplayer <n>" and talks to it over stdin/stdout:
//   in:  "target <n>", "status", "quit"
//   out: "@ready <account> <roleId> <job> <baseLevel> <jobLevel>", "@status ...", "@log ..."
// It can also run by hand for a stress test against a server whose fake accounts already exist:
//   Ro3Emu.FakePlayers --server http://127.0.0.1:5380 --data Client\RO3Emu\data --count 500 [--prefix fp_]
// The password comes from the RO3EMU_FAKE_PASSWORD environment variable (or --password).

var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (int i = 0; i + 1 < args.Length; i += 2) values[args[i]] = args[i + 1];
string server = values.GetValueOrDefault("--server", "http://127.0.0.1:5380");
string data = values.GetValueOrDefault("--data", Path.Combine("Client", "RO3Emu", "data"));
string prefix = values.GetValueOrDefault("--prefix", "fp_");
string? password = values.GetValueOrDefault("--password") ?? Environment.GetEnvironmentVariable("RO3EMU_FAKE_PASSWORD");
if (string.IsNullOrEmpty(password))
{
    Console.Error.WriteLine("No password: set RO3EMU_FAKE_PASSWORD (the server's FakePlayerService does) or pass --password.");
    return 1;
}

var output = new object();
void Write(string line) { lock (output) { Console.Out.WriteLine(line); Console.Out.Flush(); } }

var options = new FakeHostOptions(server, data, prefix, password)
{
    LoginConcurrency = int.TryParse(values.GetValueOrDefault("--logins"), out int logins) ? logins : 12,
    ChatSpeed = double.TryParse(values.GetValueOrDefault("--chat-speed"), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out double chatSpeed) && chatSpeed > 0 ? chatSpeed : 1,
    ViewCount = int.TryParse(values.GetValueOrDefault("--view"), out int view) && view > 0 ? view : 20,
    GateHost = values.GetValueOrDefault("--gate-host", "127.0.0.1") is { Length: > 0 } gate && gate != "-" ? gate : null,
};

FakePlayerHost host;
try { host = await FakePlayerHost.CreateAsync(options, Write); }
catch (Exception e)
{
    Console.Error.WriteLine($"Cannot reach the server at {server}: {e.Message}");
    return 2;
}
if (int.TryParse(values.GetValueOrDefault("--count"), out int count)) host.SetTarget(count);
Write($"@log fake player host up: server {server}, data {data}, prefix {prefix}");

using var quit = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Cancel(); };
var reporter = Task.Run(async () =>
{
    while (!quit.IsCancellationRequested)
    {
        try { await Task.Delay(10_000, quit.Token); } catch (OperationCanceledException) { break; }
        Write(host.Status());
    }
});

// stdin closes when the server that started us exits: then leave too.
var input = Task.Run(async () =>
{
    while (!quit.IsCancellationRequested)
    {
        string? line = await Console.In.ReadLineAsync();
        if (line == null) { if (!values.ContainsKey("--count")) quit.Cancel(); return; }
        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) continue;
        switch (parts[0].ToLowerInvariant())
        {
            case "target" when parts.Length > 1 && int.TryParse(parts[1], out int n):
                host.SetTarget(n);
                Write($"@log target {n}");
                break;
            case "status":
                Write(host.Status());
                break;
            case "skills" when parts.Length > 1:
                host.SetSkills(parts[1], parts.Length > 2 ? parts[2] : "");
                break;
            case "quit":
                quit.Cancel();
                return;
        }
    }
});

try { await Task.Delay(Timeout.Infinite, quit.Token); } catch (OperationCanceledException) { }
Write("@log stopping all fake players");
await host.DisposeAsync();
Write(host.Status());
return 0;
