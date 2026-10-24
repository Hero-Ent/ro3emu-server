#:sdk Microsoft.NET.Sdk.Web
// Serves the patch repository (patch-server\repo) on every interface, so a phone on the LAN can test the Android
// updater against the PC (android\serve-repo.ps1). Channels are never cached; everything else is immutable.
// usage: dotnet run android\tools\RepoServer.cs -- <repo dir> [port]
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.StaticFiles;

// A chunk missing here (the local repository only holds what was published on this PC) is fetched once from
// [upstream] (production, read-only) and kept, so a phone test downloads real game data.
string repo = Path.GetFullPath(args.Length > 0 ? args[0] : "patch-server/repo");
int port = args.Length > 1 ? int.Parse(args[1]) : 8790;
string? upstream = args.Length > 2 && args[2] != "-" ? args[2].TrimEnd('/') + "/" : null;
var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();
long served = 0, requests = 0;
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
app.Use(async (context, next) =>
{
    string path = context.Request.Path.Value ?? "";
    if (upstream != null && System.Text.RegularExpressions.Regex.IsMatch(path, "^/chunks/[0-9a-f]{2}/[0-9a-f]{64}\\.zst$"))
    {
        string local = Path.Combine(repo, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(local))
        {
            using var response = await http.GetAsync(upstream + path.TrimStart('/'), context.RequestAborted);
            if (response.IsSuccessStatusCode)
            {
                byte[] data = await response.Content.ReadAsByteArrayAsync(context.RequestAborted);
                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                string temp = local + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllBytesAsync(temp, data);
                // Two requests for the same chunk race here; Windows refuses replacing a file being served.
                try { File.Move(temp, local, true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { File.Delete(temp); }
            }
        }
    }
    await next();
});
app.Use(async (context, next) =>
{
    await next();
    Interlocked.Increment(ref requests);
    if (context.Response.ContentLength is long n) Interlocked.Add(ref served, n);
    string path = context.Request.Path.Value ?? "";
    if (!path.StartsWith("/chunks/") || context.Response.StatusCode != 200)
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} {context.Response.StatusCode} {path}");
});
var types = new FileExtensionContentTypeProvider();
types.Mappings[".zst"] = "application/zstd";
types.Mappings[".apk"] = "application/vnd.android.package-archive";
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(repo),
    ContentTypeProvider = types,
    ServeUnknownFileTypes = true,
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl =
        ctx.Context.Request.Path.StartsWithSegments("/channels") ? "no-cache, no-store" : "public, max-age=31536000, immutable",
});
_ = Task.Run(async () =>
{
    long last = 0;
    while (true)
    {
        await Task.Delay(10_000);
        long now = Interlocked.Read(ref served);
        if (now != last) Console.WriteLine($"{DateTime.Now:HH:mm:ss} {Interlocked.Read(ref requests)} requests, {now >> 20} MB served ({(now - last) / 10.0 / 1048576:F1} MB/s)");
        last = now;
    }
});
Console.WriteLine($"serving {repo} on port {port}");
app.Run();
