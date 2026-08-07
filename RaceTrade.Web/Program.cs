using System.Net;
using System.Security.Claims;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using RaceTrade.Engine.Logging;
using RaceTrade.Web.Components;
using RaceTrade.Web.Security;
using RaceTrade.Web.Services;
using RaceTrade;

// ---- data directory -------------------------------------------------------------------
// The engine addresses everything by relative path ("sites", "cbftp", "pre_bots", "db",
// "userdata", "logs"). Rather than patching dozens of call sites, the process working
// directory IS the data directory: point it somewhere and the whole tree follows.
//
// Configure with Data:Root in appsettings.json (or --data <path>). Relative values are
// resolved against the binary's folder; absolute paths work too, so the data can live
// outside the install directory - handy when replacing the binary on an update.
var appDir = ResolveAppDirectory();
var appArgs = NormalizeArgs(args);
var setPassword = appArgs.Contains("--set-password", StringComparer.OrdinalIgnoreCase);
var migrateLegacySecrets = appArgs.Contains("--migrate-legacy-secrets", StringComparer.OrdinalIgnoreCase);
var startupSecurity = ResolveStartupSecurity(appArgs, appDir);
var startupUrl = BuildUrl(startupSecurity);

if (!setPassword && !migrateLegacySecrets && !IsPortAvailable(startupSecurity.BindAddress, startupSecurity.Port))
{
    Console.WriteLine($"RaceTrade web UI: {startupUrl}");
    PrintPortInUse(startupUrl);
    Environment.ExitCode = 1;
    return;
}

var dataRoot = ResolveDataRoot(appArgs, appDir);
Directory.CreateDirectory(dataRoot);
Directory.SetCurrentDirectory(dataRoot);

foreach (var sub in new[] { "sites", "cbftp", "pre_bots", "sections", "settings", "db", "userdata", "logs" })
    Directory.CreateDirectory(sub);

Console.WriteLine($"Data directory: {dataRoot}");

// Report what was actually found, and catch the most common mistake: config files
// dropped loose into the data root instead of into their subfolder.
{
    var looseJson = Directory.GetFiles(dataRoot, "*.json")
        .Select(Path.GetFileName)
        .Where(f => !string.Equals(f, "appsettings.json", StringComparison.OrdinalIgnoreCase))
        .ToList();

    foreach (var sub in new[] { "sites", "cbftp", "pre_bots" })
    {
        var n = Directory.GetFiles(Path.Combine(dataRoot, sub), "*.json").Length;
        Console.WriteLine($"  {sub,-10} {n} file(s)");
    }

    if (looseJson.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"  NOTE: {looseJson.Count} .json file(s) sit directly in the data folder and are ignored:");
        Console.WriteLine($"        {string.Join(", ", looseJson.Take(6))}{(looseJson.Count > 6 ? ", ..." : "")}");
        Console.WriteLine( "        Site configs belong in data\\sites\\, cbftp config in data\\cbftp\\,");
        Console.WriteLine( "        prebots in data\\pre_bots\\.");
    }
}

// --- one-off legacy migration: RaceTrade --migrate-legacy-secrets ----------------------
if (migrateLegacySecrets)
{
    RunLegacySecretMigration(dataRoot);
    return;
}

// --- one-off password setup: RaceTrade --set-password ---------------------------------
if (setPassword)
{
    SetPasswordInteractive();
    return;
}

// ContentRootPath must be pinned to the binary's folder. It defaults to the current
// directory, which we just repointed at the data folder - leaving it would make ASP.NET
// look for wwwroot inside the data folder and serve no CSS ("WebRootPath was not found").
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = appArgs,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = ResolveWebRoot(appDir)
});

// An appsettings.json placed NEXT TO THE EXE wins over the one baked into the bundle.
// Without this, editing the file the user can actually see has no effect on a
// single-file build, because the host only reads the copy in the extraction folder.
builder.Configuration.AddJsonFile(
    Path.Combine(appDir, "appsettings.json"), optional: true, reloadOnChange: true);

var security = new WebSecurityOptions();
builder.Configuration.GetSection(WebSecurityOptions.SectionName).Bind(security);
// Refuses to start an unauthenticated listener on a non-loopback address.
security.Validate();

builder.WebHost.ConfigureKestrel(k =>
{
    var address = security.BindAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        ? IPAddress.Loopback
        : IPAddress.Parse(security.BindAddress);

    k.Listen(address, security.Port);
});

builder.Services.AddSingleton(security);

// Singletons: engine components are process-wide, not per browser circuit.
builder.Services.AddSingleton<UiLogSink>();
builder.Services.AddSingleton<ILogSink>(sp => sp.GetRequiredService<UiLogSink>());
builder.Services.AddSingleton<WebIrcOutput>();
builder.Services.AddSingleton<EngineHost>();
// Separate from EngineHost on purpose: chat connects and disconnects independently of
// the trader, exactly like the WinForms build's chat-only IRC clients.
builder.Services.AddSingleton<ChatHost>();
builder.Services.AddSingleton<RacerState>();
builder.Services.AddSingleton<SiteStore>();
builder.Services.AddSingleton<CbftpStore>();
builder.Services.AddSingleton<PreBotStore>();
builder.Services.AddSingleton<FxpClientService>();
builder.Services.AddSingleton<PreSpreadService>();
builder.Services.AddSingleton<CbftpSiteService>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        // LoginPath is the Razor page users get redirected TO; the POST it submits to
        // lives under /auth/ so it cannot collide with that page's route.
        o.LoginPath = "/login";
        o.LogoutPath = "/auth/logout";
        o.ExpireTimeSpan = TimeSpan.FromDays(7);
        o.SlidingExpiration = true;
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        // Not Always: a loopback/LAN install is typically plain HTTP behind a tunnel,
        // and an Always policy there would silently drop the auth cookie.
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });
// Page protection comes from [Authorize] in _Imports.razor (so it covers every
// component) combined with AuthorizeRouteView, NOT from an endpoint fallback policy.
// A fallback policy also applies to Blazor's own /_blazor SignalR endpoint, which the
// login page needs before anyone is signed in - that produced a login page that could
// not establish a circuit.
var requireLogin = security.HasPassword || !security.IsLoopbackOnly;

builder.Services.AddAuthorization();

builder.Services.AddCascadingAuthenticationState();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();
var appCss = ReadEmbeddedText("RaceTrade.Web.wwwroot.app.css");
var favicon = ReadEmbeddedBytes("RaceTrade.Web.wwwroot.favicon.ico");
var icon192 = ReadEmbeddedBytes("RaceTrade.Web.wwwroot.icon-192.png");
var icon512 = ReadEmbeddedBytes("RaceTrade.Web.wwwroot.icon-512.png");

// Route every engine log line into the UI sink.
LogManager.Configure(app.Services.GetRequiredService<ILogSink>());

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

// Order matters: authentication establishes who you are, authorization decides if you
// may proceed, and antiforgery runs last so it can see the resolved endpoint.
app.UseStaticFiles();
app.UseAuthentication();

// No password configured AND loopback-only: treat every request as the local operator.
// The OS boundary is the access control here, and demanding a login before one has been
// set up would lock the owner out of their own first run. Everything downstream then
// sees a normal authenticated user, so there is only ONE authorization code path
// instead of conditional policies scattered around.
if (!requireLogin)
{
    app.Use(async (ctx, next) =>
    {
        if (ctx.User?.Identity?.IsAuthenticated != true)
        {
            var local = new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, "local") }, "Local");
            ctx.User = new ClaimsPrincipal(local);
        }
        await next();
    });
}

app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/app.css", () => Results.Content(appCss, "text/css"))
    .AllowAnonymous();
app.MapGet("/favicon.ico", () => Results.File(favicon, "image/x-icon"))
    .AllowAnonymous();
app.MapGet("/icon-192.png", () => Results.File(icon192, "image/png"))
    .AllowAnonymous();
app.MapGet("/icon-512.png", () => Results.File(icon512, "image/png"))
    .AllowAnonymous();

// --- login / logout -------------------------------------------------------------------
// Minimal API endpoints rather than Blazor handlers: sign-in must write a cookie, which
// needs a real HTTP response, not a SignalR circuit.
var loginAttempts = new LoginThrottle(security);

// NOTE: these POST handlers must NOT share a path with a Razor page. Login.razor is
// @page "/login", so a MapPost("/login") produces two endpoints for the same route and
// ASP.NET throws AmbiguousMatchException on submit. Hence the /auth/* paths.
app.MapPost("/auth/login", async (HttpContext ctx, WebSecurityOptions opts) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var password = form["password"].ToString();
    var client = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    if (!loginAttempts.IsAllowed(client))
        return Results.Redirect("/login?error=locked");

    if (!opts.VerifyPassword(password))
    {
        loginAttempts.RegisterFailure(client);
        LogManager.Warning($"Failed web login from {client}");
        return Results.Redirect("/login?error=1");
    }

    loginAttempts.Reset(client);

    var identity = new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.Name, "admin") },
        CookieAuthenticationDefaults.AuthenticationScheme);

    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity));

    LogManager.Info($"Web login from {client}");
    return Results.Redirect("/");
})
// Critical: the fallback policy demands an authenticated user for every endpoint, and
// this is the endpoint you use to BECOME authenticated. Without AllowAnonymous the POST
// is rejected and you are bounced straight back to /login - a login page that can never
// log you in.
.AllowAnonymous()
// The form is a plain HTML POST, not a Blazor interactive submit, so the antiforgery
// middleware has no token to validate here.
.DisableAntiforgery();

app.MapPost("/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).AllowAnonymous().DisableAntiforgery();

app.MapRazorComponents<RaceTrade.Web.Components.App>()
    .AddInteractiveServerRenderMode();

var url = BuildUrl(security);
Console.WriteLine($"RaceTrade web UI: {url}");
if (security.IsLoopbackOnly)
    Console.WriteLine("Listening on loopback only. Set Web:BindAddress to expose it (a password is then required).");

// --- desktop-app behaviour -------------------------------------------------------------
// Registered on ApplicationStarted, not before app.Run(): at this point Kestrel has
// actually bound the port, so we never open a browser at a URL that failed to come up and
// never hide the console right before printing a bind error to it.
var openBrowser = !appArgs.Contains("--no-browser", StringComparer.OrdinalIgnoreCase);
var keepConsole = appArgs.Contains("--console", StringComparer.OrdinalIgnoreCase);

app.Lifetime.ApplicationStarted.Register(() =>
{
    if (openBrowser) AppLauncher.OpenBrowser(url);
    if (!keepConsole) AppLauncher.DetachConsole();
});

try
{
    app.Run();
}
catch (IOException ex) when (ContainsSocketException(ex))
{
    PrintPortInUse(url);
    Environment.ExitCode = 1;
}


// --------------------------------------------------------------------------------------
/// <summary>
/// The folder the user sees the executable in.
///
/// NOT AppContext.BaseDirectory: in a PublishSingleFile build with
/// IncludeAllContentForSelfExtract the bundle is unpacked into a temp folder and
/// BaseDirectory points THERE. Resolving "data" against it would create the data folder
/// somewhere under %TEMP%\.net\ and silently lose every config on the next cleanup.
///
/// Environment.ProcessPath is the real exe - except under `dotnet run` / F5, where the
/// process is dotnet itself, so that case falls back to BaseDirectory.
/// </summary>
static string ResolveAppDirectory()
{
    var exe = Environment.ProcessPath;

    if (!string.IsNullOrEmpty(exe))
    {
        var name = Path.GetFileNameWithoutExtension(exe);
        if (!string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.GetDirectoryName(exe);
            if (!string.IsNullOrEmpty(dir)) return dir;
        }
    }

    return AppContext.BaseDirectory;
}

static string ResolveWebRoot(string appDir)
{
    var visible = Path.Combine(appDir, "wwwroot");
    if (Directory.Exists(visible)) return visible;

    var extracted = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    if (Directory.Exists(extracted)) return extracted;

    var empty = Path.Combine(Path.GetTempPath(), "RaceTrade", "wwwroot");
    Directory.CreateDirectory(empty);
    return empty;
}

static string[] NormalizeArgs(string[] args)
{
    var normalized = new List<string>();

    for (var i = 0; i < args.Length; i++)
    {
        var raw = args[i];

        if (TryReadCommandLineValue(args, ref i, raw, "port", out var port))
        {
            normalized.Add($"--Web:Port={port}");
            continue;
        }

        if (TryReadCommandLineValue(args, ref i, raw, "bind", out var bindAddress))
        {
            normalized.Add($"--Web:BindAddress={bindAddress}");
            continue;
        }

        normalized.Add(raw);
    }

    return normalized.ToArray();
}

static bool TryReadCommandLineValue(
    string[] args,
    ref int index,
    string raw,
    string name,
    out string value)
{
    value = "";

    var option = raw.TrimStart('-', '/');
    var separator = option.IndexOf('=');
    var key = separator >= 0 ? option[..separator] : option;

    if (!key.Equals(name, StringComparison.OrdinalIgnoreCase))
        return false;

    if (separator >= 0)
    {
        value = option[(separator + 1)..];
        return !string.IsNullOrWhiteSpace(value);
    }

    if (index + 1 >= args.Length || args[index + 1].StartsWith('-') || args[index + 1].StartsWith('/'))
        return false;

    value = args[++index];
    return !string.IsNullOrWhiteSpace(value);
}

static WebSecurityOptions ResolveStartupSecurity(string[] args, string appDir)
{
    var security = new WebSecurityOptions();
    var settings = Path.Combine(appDir, "appsettings.json");

    if (File.Exists(settings))
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settings));
            if (doc.RootElement.TryGetProperty("Web", out var web))
            {
                if (web.TryGetProperty("BindAddress", out var bindAddress))
                {
                    var v = bindAddress.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) security.BindAddress = v;
                }

                if (web.TryGetProperty("Port", out var port) && port.TryGetInt32(out var p))
                    security.Port = p;
            }
        }
        catch
        {
            // The real configuration loader reports malformed settings later. For this
            // early port probe, fall back to defaults instead of blocking startup here.
        }
    }

    for (var i = 0; i < args.Length; i++)
    {
        var raw = args[i];
        var normalized = raw.TrimStart('-', '/');
        var key = normalized;
        string? value = null;

        var equals = normalized.IndexOf('=');
        if (equals >= 0)
        {
            key = normalized[..equals];
            value = normalized[(equals + 1)..];
        }
        else if (i + 1 < args.Length && !args[i + 1].StartsWith('-') && !args[i + 1].StartsWith('/'))
        {
            value = args[i + 1];
        }

        if (key.Equals("Web:BindAddress", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Web__BindAddress", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(value)) security.BindAddress = value;
        }
        else if ((key.Equals("Web:Port", StringComparison.OrdinalIgnoreCase) ||
                  key.Equals("Web__Port", StringComparison.OrdinalIgnoreCase)) &&
                 int.TryParse(value, out var port))
        {
            security.Port = port;
        }
    }

    return security;
}

static string BuildUrl(WebSecurityOptions security) =>
    $"http://{(security.BindAddress == "0.0.0.0" ? "localhost" : security.BindAddress)}:{security.Port}";

/// <summary>
/// Works out where the data lives, in order: --data on the command line, then Data:Root
/// from appsettings.json next to the binary, then a "data" folder beside the binary.
/// Read before the host is built, because the working directory has to be set before any
/// engine code resolves a relative path.
/// </summary>
static string ResolveDataRoot(string[] args, string appDir)
{
    var idx = Array.FindIndex(args, a => a.Equals("--data", StringComparison.OrdinalIgnoreCase));
    if (idx >= 0 && idx + 1 < args.Length)
        return Path.GetFullPath(args[idx + 1]);

    var settings = Path.Combine(appDir, "appsettings.json");
    if (File.Exists(settings))
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settings));
            if (doc.RootElement.TryGetProperty("Data", out var data) &&
                data.TryGetProperty("Root", out var root))
            {
                var v = root.GetString();
                if (!string.IsNullOrWhiteSpace(v))
                    return Path.GetFullPath(Path.Combine(appDir, v));
            }
        }
        catch
        {
            // Malformed settings shouldn't stop startup; fall through to the default.
        }
    }

    return Path.Combine(appDir, "data");
}

static string ReadEmbeddedText(string name)
{
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"Embedded asset '{name}' was not found.");
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
}

static byte[] ReadEmbeddedBytes(string name)
{
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"Embedded asset '{name}' was not found.");
    using var memory = new MemoryStream();
    stream.CopyTo(memory);
    return memory.ToArray();
}

static bool IsPortAvailable(string bindAddress, int port)
{
    try
    {
        var address = bindAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? IPAddress.Loopback
            : IPAddress.Parse(bindAddress);

        using var listener = new TcpListener(address, port);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
        listener.Start();
        return true;
    }
    catch (SocketException)
    {
        return false;
    }
    catch (FormatException)
    {
        return true;
    }
}

static bool ContainsSocketException(Exception? ex)
{
    while (ex is not null)
    {
        if (ex is SocketException || ex.GetType().Name.Contains("AddressInUse", StringComparison.OrdinalIgnoreCase))
            return true;

        ex = ex.InnerException;
    }

    return false;
}

static void PrintPortInUse(string url)
{
    Console.WriteLine();
    Console.WriteLine($"Could not listen on {url} - the port is already in use.");
    Console.WriteLine("Another RaceTrade is probably still running. Close it, or:");
    Console.WriteLine("    taskkill /IM RaceTrade.exe /F        (Windows)");
    Console.WriteLine("    pkill RaceTrade                      (Linux)");
    Console.WriteLine("Or start another copy on a different port:");
    Console.WriteLine("    RaceTrade.exe --port 8421");
    Console.WriteLine("You can also set Web:Port in appsettings.json next to the executable.");
}

static void RunLegacySecretMigration(string dataRoot)
{
    try
    {
        var result = LegacySecretMigrator.MigrateDataRoot(dataRoot);

        Console.WriteLine();
        Console.WriteLine("Legacy secret migration complete.");
        Console.WriteLine($"  Data folder      : {dataRoot}");
        Console.WriteLine($"  JSON files scanned: {result.FilesScanned}");
        Console.WriteLine($"  Files changed     : {result.FilesChanged}");
        Console.WriteLine($"  Secrets migrated  : {result.SecretsMigrated}");

        if (result.Errors.Count == 0)
            return;

        Console.WriteLine();
        Console.WriteLine("Some files could not be migrated:");
        foreach (var error in result.Errors.Take(20))
            Console.WriteLine($"  - {error}");

        if (result.Errors.Count > 20)
            Console.WriteLine($"  ... and {result.Errors.Count - 20} more.");

        Environment.ExitCode = 1;
    }
    catch (PlatformNotSupportedException ex)
    {
        Console.WriteLine();
        Console.WriteLine(ex.Message);
        Environment.ExitCode = 1;
    }
    catch (Exception ex)
    {
        Console.WriteLine();
        Console.WriteLine($"Legacy secret migration failed: {ex.Message}");
        Environment.ExitCode = 1;
    }
}

static void SetPasswordInteractive()
{
    Console.Write("New admin password: ");
    var pw = ReadHidden();
    Console.Write("Repeat: ");
    var pw2 = ReadHidden();

    if (pw != pw2 || string.IsNullOrWhiteSpace(pw))
    {
        Console.WriteLine("Passwords do not match (or were empty). Nothing changed.");
        return;
    }

    var (hash, salt) = WebSecurityOptions.HashPassword(pw);
    Console.WriteLine();
    Console.WriteLine("Add this to appsettings.json:");
    Console.WriteLine("{");
    Console.WriteLine("  \"Web\": {");
    Console.WriteLine($"    \"PasswordHash\": \"{hash}\",");
    Console.WriteLine($"    \"PasswordSalt\": \"{salt}\"");
    Console.WriteLine("  }");
    Console.WriteLine("}");
}

static string ReadHidden()
{
    var buf = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (buf.Length > 0) buf.Length--;
            continue;
        }
        buf.Append(key.KeyChar);
    }
    return buf.ToString();
}

/// <summary>
/// Per-client login throttle. Without this, a password-protected instance exposed on a
/// network can be brute-forced as fast as the CPU allows.
/// </summary>
internal sealed class LoginThrottle(WebSecurityOptions options)
{
    private readonly Dictionary<string, (int failures, DateTimeOffset until)> _state = new();
    private readonly object _lock = new();

    public bool IsAllowed(string client)
    {
        lock (_lock)
        {
            if (!_state.TryGetValue(client, out var s)) return true;
            if (s.failures < options.MaxLoginAttempts) return true;
            return DateTimeOffset.UtcNow >= s.until;
        }
    }

    public void RegisterFailure(string client)
    {
        lock (_lock)
        {
            _state.TryGetValue(client, out var s);
            var failures = s.failures + 1;
            _state[client] = (failures, DateTimeOffset.UtcNow.AddMinutes(options.LoginLockoutMinutes));
        }
    }

    public void Reset(string client)
    {
        lock (_lock) { _state.Remove(client); }
    }
}
