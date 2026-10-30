using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Http;

namespace Ro3Emu.Tests;

/// <summary>
/// The web panel (Ro3Emu.Panel, docs/guide/panel.md) on its own port of a running server: logins with the game's
/// accounts, CSRF, the admin area, bans that kick, sign-up and password changes, and the pages. Accounts are invented.
/// </summary>
public sealed class PanelTests : IAsyncLifetime
{
    private const string Admin = "panel-admin";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private SyntheticHost _host = null!;
    private string _panel = "";

    public async Task InitializeAsync() => (_host, _panel) = await StartAsync(o => o.GmAccounts = [Admin]);

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static async Task<(SyntheticHost Host, string Panel)> StartAsync(Action<ServerOptions> configure)
    {
        int port = TestPorts.FreePort();
        var host = await SyntheticHost.StartAsync(o =>
        {
            o.Panel.Enabled = true;
            o.Panel.Port = port;
            configure(o);
        });
        return (host, $"http://127.0.0.1:{port}");
    }

    [Fact]
    public async Task PagesRenderWithTheirSecurityHeaders()
    {
        using var browser = new PanelBrowser(_panel);
        var home = await browser.Http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Equal("text/html", home.Content.Headers.ContentType?.MediaType);
        Assert.Contains("/app.js", await home.Content.ReadAsStringAsync());
        Assert.StartsWith("default-src 'none'; script-src 'self'", home.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", home.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", home.Headers.GetValues("X-Content-Type-Options").Single());
        foreach (string page in new[] { "/online", "/rankings", "/news", "/login", "/register", "/account", "/admin/accounts/x" })
            Assert.Equal(HttpStatusCode.OK, (await browser.Http.GetAsync(page)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.Http.GetAsync("/no-such-page")).StatusCode);
        Assert.Equal("text/css", (await browser.Http.GetAsync("/app.css")).Content.Headers.ContentType?.MediaType);
        Assert.Equal("font/woff2", (await browser.Http.GetAsync("/fonts/young-serif-400-latin.woff2")).Content.Headers.ContentType?.MediaType);

        var status = await browser.GetJsonAsync("/api/status");
        Assert.Equal("RO3Emu", status.GetProperty("name").GetString());
        Assert.Equal(0, status.GetProperty("players").GetInt32());
        Assert.Equal(0, status.GetProperty("characters").GetInt32());
        Assert.Equal(JsonValueKind.Number, status.GetProperty("accounts").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.Http.GetAsync("/api/nothing")).StatusCode);
    }

    [Fact]
    public async Task ThePanelPortServesOnlyThePanel()
    {
        using var client = new HttpClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(_panel + "/ro3emu/status")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(_host.Http + "/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(_host.Http + "/ro3emu/status")).StatusCode);
    }

    [Fact]
    public async Task AGameAccountLogsInAndAWrongPasswordIsRefused()
    {
        Assert.Equal(0, await SdkLoginAsync(_host, "panel-player", "pw-game"));
        using var browser = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.LoginAsync("panel-player", "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.LoginAsync("panel-nobody", "pw-game")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.Http.GetAsync("/api/account")).StatusCode);

        // Any letter case finds the account; the session carries its stored name.
        var login = await browser.LoginAsync("Panel-Player", "pw-game");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var session = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("panel-player", session.GetProperty("account").GetString());
        Assert.False(session.GetProperty("admin").GetBoolean());
        string cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), c => c.StartsWith(PanelCookie, StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("panel-player", (await browser.GetJsonAsync("/api/account")).GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsync("/api/logout", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.Http.GetAsync("/api/account")).StatusCode);
    }

    private const string PanelCookie = "ro3p_session=";

    [Fact]
    public async Task APostWithoutTheSessionsCsrfTokenIsRefused()
    {
        Assert.Equal(0, await SdkLoginAsync(_host, "panel-csrf", "pw-game"));
        using var browser = new PanelBrowser(_panel);
        await browser.SessionAsync();
        string visitorToken = browser.Csrf;
        browser.Csrf = "";
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.LoginAsync("panel-csrf", "pw-game")).StatusCode);
        browser.Csrf = "not-the-token";
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.LoginAsync("panel-csrf", "pw-game")).StatusCode);
        browser.Csrf = visitorToken;
        Assert.Equal(HttpStatusCode.OK, (await browser.LoginAsync("panel-csrf", "pw-game")).StatusCode);

        // The visitor's token does not carry over to the session, and another site's page cannot post.
        browser.Csrf = visitorToken;
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostAsync("/api/account/password", new { current = "pw-game", next = "changed-1" })).StatusCode);
        await browser.SessionAsync();
        using var foreign = new HttpRequestMessage(HttpMethod.Post, "/api/logout") { Content = JsonContent.Create(new { }) };
        foreign.Headers.Add("X-CSRF-Token", browser.Csrf);
        foreign.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.Http.SendAsync(foreign)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.Http.GetAsync("/api/account")).StatusCode);
    }

    [Fact]
    public async Task TheAdminAreaIsOnlyForListedGmAccounts()
    {
        Assert.Equal(0, await SdkLoginAsync(_host, "panel-plain", "pw-game"));
        Assert.Equal(0, await SdkLoginAsync(_host, Admin, "pw-admin"));
        using var anonymous = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.Http.GetAsync("/api/admin/dashboard")).StatusCode);
        using var player = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.OK, (await player.LoginAsync("panel-plain", "pw-game")).StatusCode);
        foreach (string path in new[] { "/api/admin/dashboard", "/api/admin/accounts", "/api/admin/accounts/panel-plain", "/api/admin/audit" })
            Assert.Equal(HttpStatusCode.Forbidden, (await player.Http.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await player.PostAsync("/api/admin/accounts/" + Admin + "/ban", new { reason = "no", hours = 1 })).StatusCode);

        using var admin = new PanelBrowser(_panel);
        Assert.True((await (await admin.LoginAsync(Admin, "pw-admin")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("admin").GetBoolean());
        var dashboard = await admin.GetJsonAsync("/api/admin/dashboard");
        Assert.True(dashboard.GetProperty("accounts").GetInt32() >= 2);
        Assert.Equal("Accepting", dashboard.GetProperty("phase").GetString());
        var found = await admin.GetJsonAsync("/api/admin/accounts?q=PLAIN");
        Assert.Equal("panel-plain", Assert.Single(found.GetProperty("accounts").EnumerateArray()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task WithAnEmptyGmListNobodyIsAdminAndLoginsAreThrottled()
    {
        var (host, panel) = await StartAsync(o =>
        {
            o.GmOpenWhenEmpty = true; // GM for everyone in game; the panel still has no admin
            o.LoginLimitIncludesLoopback = true;
            o.LoginAttemptsPerMinute = 3;
        });
        await using var _ = host;
        Assert.Equal(0, await SdkLoginAsync(host, "panel-anyone", "pw-game"));
        using var browser = new PanelBrowser(panel);
        var login = await browser.LoginAsync("panel-anyone", "pw-game");
        Assert.False((await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("admin").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.Http.GetAsync("/api/admin/dashboard")).StatusCode);

        // The server's credential budget (CredentialLimiter), shared with the game's login: the third check of the
        // minute is the last, then 429 before any hashing.
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.LoginAsync("panel-anyone", "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await browser.LoginAsync("panel-anyone", "pw-game")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await browser.RegisterAsync("panel-another", "secret-1")).StatusCode);
    }

    [Fact]
    public async Task ABanKicksTheOnlinePlayerBlocksEveryLoginAndIsAudited()
    {
        Assert.Equal(0, await SdkLoginAsync(_host, Admin, "pw-admin"));
        await using var bot = await _host.InWorldAsync("Banned");
        using var playerPanel = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.OK, (await playerPanel.LoginAsync("banned", "pw")).StatusCode);
        using var admin = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.OK, (await admin.LoginAsync(Admin, "pw-admin")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/admin/accounts/banned/ban", new { reason = "", hours = 24 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/admin/accounts/" + Admin + "/ban", new { reason = "self", hours = 24 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/admin/accounts/panel-ghost/ban", new { reason = "x", hours = 24 })).StatusCode);

        var ban = await admin.PostAsync("/api/admin/accounts/banned/ban", new { reason = "botting", hours = 24 });
        Assert.Equal(HttpStatusCode.OK, ban.StatusCode);
        Assert.Equal(1, (await ban.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("kicked").GetInt32());
        await bot.Inbox.WaitAsync(e => e.Kind == BotEventKind.Disconnect, Wait);
        Assert.Equal(401, await SdkLoginAsync(_host, "banned", "pw"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await playerPanel.Http.GetAsync("/api/account")).StatusCode);
        await playerPanel.SessionAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await playerPanel.LoginAsync("banned", "pw")).StatusCode);
        Assert.True(await _host.Service<AccountStore>().IsBannedAsync("banned"));

        var detail = await admin.GetJsonAsync("/api/admin/accounts/banned");
        Assert.Equal("botting", detail.GetProperty("account").GetProperty("ban").GetProperty("reason").GetString());
        Assert.Equal(Admin, detail.GetProperty("account").GetProperty("ban").GetProperty("by").GetString());
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/admin/accounts/banned/unban", new { reason = "appeal" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/admin/accounts/banned/unban", new { reason = "" })).StatusCode);
        Assert.Equal(0, await SdkLoginAsync(_host, "banned", "pw"));

        var audit = (await admin.GetJsonAsync("/api/admin/audit")).GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(["unban", "ban"], audit.Select(e => e.GetProperty("action").GetString()));
        Assert.All(audit, e => Assert.Equal(Admin, e.GetProperty("admin").GetString()));
        Assert.All(audit, e => Assert.Equal("banned", e.GetProperty("target").GetString()));
        Assert.EndsWith("botting", audit[1].GetProperty("detail").GetString());
    }

    [Fact]
    public async Task SignUpCreatesTheAccountTheGameLogsInWith()
    {
        using var browser = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.RegisterAsync("   ", "secret-1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.RegisterAsync("panel-new", "short")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.RegisterAsync("panel\u0001new", "secret-1")).StatusCode);
        var created = await browser.RegisterAsync("  panel-new  ", "secret-1");
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal("panel-new", (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("account").GetString());
        Assert.Equal("panel-new", (await browser.GetJsonAsync("/api/account")).GetProperty("name").GetString());

        // The same record as a first login in the game: the game signs in with it, and no second account appears.
        Assert.Equal(0, await SdkLoginAsync(_host, "panel-new", "secret-1"));
        Assert.Equal(401, await SdkLoginAsync(_host, "panel-new", "other-pw"));
        using var other = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.Conflict, (await other.RegisterAsync("PANEL-NEW", "secret-2")).StatusCode);
        Assert.Equal(0, await SdkLoginAsync(_host, "panel-game-first", "pw-game"));
        Assert.Equal(HttpStatusCode.Conflict, (await other.RegisterAsync("panel-game-first", "secret-2")).StatusCode);
        Assert.Equal("panel-new", await _host.Service<AccountStore>().VerifiedAccountAsync("Panel-New", "secret-1"));
    }

    [Fact]
    public async Task SignUpCanBeTurnedOff()
    {
        var (host, panel) = await StartAsync(o => o.Panel.Registration = false);
        await using var _ = host;
        using var browser = new PanelBrowser(panel);
        Assert.False((await browser.SessionAsync()).GetProperty("registration").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.RegisterAsync("panel-closed", "secret-1")).StatusCode);
        Assert.Null(await host.Service<AccountStore>().VerifiedAccountAsync("panel-closed", "secret-1"));
    }

    [Fact]
    public async Task APasswordChangeNeedsTheCurrentPasswordAndSignsOutOtherBrowsers()
    {
        Assert.Equal(0, await SdkLoginAsync(_host, "panel-pw", "old-pass"));
        using var first = new PanelBrowser(_panel);
        using var second = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.OK, (await first.LoginAsync("panel-pw", "old-pass")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.LoginAsync("panel-pw", "old-pass")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await first.PostAsync("/api/account/password", new { current = "wrong", next = "new-pass" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await first.PostAsync("/api/account/password", new { current = "old-pass", next = "new" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.PostAsync("/api/account/password", new { current = "old-pass", next = "new-pass" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.Http.GetAsync("/api/account")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.Http.GetAsync("/api/account")).StatusCode);
        Assert.Equal(401, await SdkLoginAsync(_host, "panel-pw", "old-pass"));
        Assert.Equal(0, await SdkLoginAsync(_host, "panel-pw", "new-pass"));
    }

    [Fact]
    public async Task OnlineRankingsAndTheAccountPageShowTheCharacters()
    {
        Assert.Equal(0, await SdkLoginAsync(_host, Admin, "pw-admin"));
        await using var bot = await _host.InWorldAsync("Walker");
        using var anonymous = new PanelBrowser(_panel);
        var online = Assert.Single((await anonymous.GetJsonAsync("/api/online")).GetProperty("players").EnumerateArray());
        Assert.Equal("Walker", online.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, online.GetProperty("account").ValueKind);
        // The status counts from the loop's census, taken once a second.
        var deadline = DateTime.UtcNow + Wait;
        while ((await anonymous.GetJsonAsync("/api/status")).GetProperty("players").GetInt32() == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.Equal(1, (await anonymous.GetJsonAsync("/api/status")).GetProperty("players").GetInt32());
        using var admin = new PanelBrowser(_panel);
        await admin.LoginAsync(Admin, "pw-admin");
        Assert.Equal("walker", Assert.Single((await admin.GetJsonAsync("/api/online")).GetProperty("players").EnumerateArray())
            .GetProperty("account").GetString());

        var boards = (await anonymous.GetJsonAsync("/api/rankings")).GetProperty("boards").EnumerateArray().Select(b => b.GetProperty("id").GetString()).ToList();
        Assert.Contains("level", boards);
        Assert.Contains("power", boards);
        var level = await anonymous.GetJsonAsync("/api/rankings/level");
        Assert.Contains(level.GetProperty("rows").EnumerateArray(), r => r.GetProperty("name").GetString() == "Walker");
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.Http.GetAsync("/api/rankings/nothing")).StatusCode);

        using var player = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.OK, (await player.LoginAsync("walker", "pw")).StatusCode);
        var character = Assert.Single((await player.GetJsonAsync("/api/account")).GetProperty("characters").EnumerateArray());
        Assert.Equal("Walker", character.GetProperty("name").GetString());
        Assert.True(character.GetProperty("online").GetBoolean());
        Assert.True(character.GetProperty("level").GetInt32() >= 1);
    }

    private static async Task<int> SdkLoginAsync(SyntheticHost host, string identity, string credential)
    {
        using var client = new HttpClient();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["identity"] = identity, ["credential"] = credential });
        var body = await (await client.PostAsync(host.Http + SdkEndpoints.LoginPath, form)).Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("code").GetInt32();
    }

    /// <summary>A browser on the panel: its cookies, and the CSRF token of its latest /api/session.</summary>
    private sealed class PanelBrowser : IDisposable
    {
        private readonly HttpClientHandler _handler = new() { CookieContainer = new CookieContainer(), UseCookies = true };

        public PanelBrowser(string panel) => Http = new HttpClient(_handler) { BaseAddress = new Uri(panel) };

        public HttpClient Http { get; }
        /// <summary>The token the next POST sends; a test may overwrite it.</summary>
        public string Csrf { get; set; } = "";
        private bool _visited;

        public async Task<JsonElement> GetJsonAsync(string path)
        {
            var response = await Http.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        public async Task<JsonElement> SessionAsync()
        {
            var session = await GetJsonAsync("/api/session");
            Csrf = session.GetProperty("csrf").GetString() ?? "";
            _visited = true;
            return session;
        }

        public async Task<HttpResponseMessage> PostAsync(string path, object body)
        {
            if (!_visited) await SessionAsync();
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            request.Headers.Add("X-CSRF-Token", Csrf);
            var response = await Http.SendAsync(request);
            // A login or sign-up answers with the new session and its token; the body stays readable for the test.
            string text = await response.Content.ReadAsStringAsync();
            response.Content = new StringContent(text, System.Text.Encoding.UTF8, "application/json");
            if (response.IsSuccessStatusCode && path is "/api/login" or "/api/register")
                Csrf = JsonDocument.Parse(text).RootElement.GetProperty("csrf").GetString() ?? "";
            return response;
        }

        public Task<HttpResponseMessage> LoginAsync(string account, string password) => PostAsync("/api/login", new { account, password });

        public Task<HttpResponseMessage> RegisterAsync(string account, string password) => PostAsync("/api/register", new { account, password });

        public void Dispose()
        {
            Http.Dispose();
            _handler.Dispose();
        }
    }
}
