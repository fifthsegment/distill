using System.Reflection;
using Distill.Cdp;
using Distill.Pipeline;

var options = ParseArgs(args);

if (options.Urls.Count == 0)
{
    PrintUsage();
    return 1;
}

using var fetcher = new Fetcher();

// Browser is launched lazily and only once — reused across all URLs that need it.
BrowserLauncher? launcher = null;
CdpClient? browserClient = null;
string? browserPath = null;

try
{
    for (int i = 0; i < options.Urls.Count; i++)
    {
        var url = options.Urls[i];

        if (i > 0 && options.Delay > TimeSpan.Zero)
        {
            Console.Error.WriteLine($"Waiting {options.Delay.TotalMilliseconds}ms before next request...");
            await Task.Delay(options.Delay);
        }

        try
        {
            var markdown = await ProcessUrl(url, options, fetcher);

            if (options.OutputFile is not null)
                await File.WriteAllTextAsync(options.OutputFile, markdown);
            else
                Console.WriteLine(markdown);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error processing {url}: {ex.Message}");
        }
    }
}
finally
{
    if (browserClient is not null) await browserClient.DisposeAsync();
    if (launcher is not null) await launcher.DisposeAsync();
}

return 0;

async Task<string> ProcessUrl(Uri url, CliOptions opts, Fetcher fetcher)
{
    string html = "";
    bool usedBrowser = false;

    if (opts.CdpEndpoint is not null)
    {
        html = await RenderViaCdp(url, opts.CdpEndpoint, opts.Timeout);
        usedBrowser = true;
    }
    else if (opts.ForceRender)
    {
        html = await RenderWithBrowser(url, opts);
        usedBrowser = true;
    }
    else if (opts.ForceStatic)
    {
        html = await fetcher.FetchAsync(url);
    }
    else
    {
        // Static-first: always try HttpClient first. Chrome is a last resort.
        bool needsBrowser = false;
        try
        {
            html = await fetcher.FetchAsync(url);

            var staticBlock = BlockDetector.Check(html);
            if (staticBlock.IsBlocked)
            {
                Console.Error.WriteLine("Static fetch blocked — escalating to browser...");
                needsBrowser = true;
            }
            else if (await SpaDetector.IsSpaAsync(html))
            {
                Console.Error.WriteLine("SPA detected — launching browser for rendering...");
                needsBrowser = true;
            }
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"Static fetch failed ({ex.StatusCode}) — escalating to browser...");
            needsBrowser = true;
        }

        if (needsBrowser)
        {
            html = await RenderWithBrowser(url, opts);
            usedBrowser = true;
        }
    }

    // Check for captcha/block (may have appeared during browser render)
    var blockCheck = BlockDetector.Check(html);
    if (blockCheck.IsBlocked)
    {
        // Auto-escalate: if we were headless and got blocked, retry visible with profile
        if (usedBrowser && !opts.Visible && !opts.TriedEscalation)
        {
            var profileDir = opts.ProfileDir ?? DefaultChromeProfileDir();
            if (profileDir is not null)
            {
                Console.Error.WriteLine("Headless blocked — retrying with visible browser + profile...");
                opts.TriedEscalation = true;
                opts.Visible = true;
                opts.ProfileDir = profileDir;
                // Relaunch browser in visible mode
                if (browserClient is not null) await browserClient.DisposeAsync();
                if (launcher is not null) await launcher.DisposeAsync();
                launcher = null;
                browserClient = null;
                html = await RenderWithBrowser(url, opts);
                blockCheck = BlockDetector.Check(html);
            }
        }

        if (blockCheck.IsBlocked)
            throw new InvalidOperationException(blockCheck.Message);
    }

    // Clean the HTML — browser path uses JS injection (PageCleaner), static path uses AngleSharp
    if (!usedBrowser)
        html = HtmlCleaner.Clean(html);

    var extracted = ContentExtractor.Extract(html, url);
    var markdown = MarkdownConverter.Convert(extracted);

    var stripped = BlockDetector.StripInvisibleChars(markdown).Trim();
    if (string.IsNullOrWhiteSpace(stripped))
        throw new InvalidOperationException(
            "Extraction produced empty output — the page may use anti-scraping tricks " +
            "(e.g. zero-width characters) or require a real browser session. " +
            "Try: --render --visible --profile <your-chrome-profile>.");

    return stripped;
}

static string? DefaultChromeProfileDir()
{
    if (OperatingSystem.IsMacOS())
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "Google", "Chrome");
    if (OperatingSystem.IsLinux())
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "google-chrome");
    if (OperatingSystem.IsWindows())
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Google", "Chrome", "User Data");
    return null;
}

async Task<string> RenderWithBrowser(Uri url, CliOptions opts)
{
    await EnsureBrowserLaunched(opts);
    await using var commands = new CdpCommands(browserClient!, launcher!.Port);
    return await commands.GetRenderedHtmlAsync(url, opts.EffectiveTimeout,
        preExtractScripts: PageCleaner.AllScripts);
}

async Task<string> RenderViaCdp(Uri url, Uri cdpEndpoint, TimeSpan timeout)
{
    await using var client = new CdpClient();
    await client.ConnectAsync(cdpEndpoint);
    int port = cdpEndpoint.Port;
    await using var commands = new CdpCommands(client, port);
    return await commands.GetRenderedHtmlAsync(url, timeout,
        preExtractScripts: PageCleaner.AllScripts);
}

async Task EnsureBrowserLaunched(CliOptions opts)
{
    if (launcher is not null) return;

    browserPath = BrowserLauncher.FindBrowser(opts.BrowserPath)
        ?? throw new InvalidOperationException(
            "No Chrome or Edge browser found. Install one, set CHROME_PATH/EDGE_PATH, or use --browser <path>.");

    Console.Error.WriteLine($"Using browser: {browserPath}");

    launcher = new BrowserLauncher();
    var browserWsUri = await launcher.LaunchAsync(browserPath, opts.ProfileDir, opts.Visible);
    browserClient = new CdpClient();
    await browserClient.ConnectAsync(browserWsUri);
}

static CliOptions ParseArgs(string[] args)
{
    var opts = new CliOptions();
    var urls = new List<Uri>();

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--render":
                opts.ForceRender = true;
                break;
            case "--static":
                opts.ForceStatic = true;
                break;
            case "-o" or "--output":
                opts.OutputFile = args[++i];
                break;
            case "--browser":
                opts.BrowserPath = args[++i];
                break;
            case "--profile":
                opts.ProfileDir = args[++i];
                break;
            case "--cdp":
                opts.CdpEndpoint = new Uri(args[++i]);
                break;
            case "--timeout":
                opts.Timeout = TimeSpan.FromMilliseconds(int.Parse(args[++i]));
                opts.TimeoutExplicitlySet = true;
                break;
            case "--delay":
                opts.Delay = TimeSpan.FromMilliseconds(int.Parse(args[++i]));
                break;
            case "--visible":
                opts.Visible = true;
                break;
            case "--stdin":
                opts.ReadStdin = true;
                break;
            case "--version":
                Console.WriteLine(typeof(CliOptions).Assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");
                Environment.Exit(0);
                break;
            case "-h" or "--help":
                PrintUsage();
                Environment.Exit(0);
                break;
            default:
                if (Uri.TryCreate(args[i], UriKind.Absolute, out var uri) &&
                    (uri.Scheme == "http" || uri.Scheme == "https"))
                    urls.Add(uri);
                else
                    Console.Error.WriteLine($"Skipping invalid URL: {args[i]}");
                break;
        }
    }

    if (opts.ReadStdin)
    {
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            line = line.Trim();
            if (Uri.TryCreate(line, UriKind.Absolute, out var uri) &&
                (uri.Scheme == "http" || uri.Scheme == "https"))
                urls.Add(uri);
        }
    }

    opts.Urls = urls;
    return opts;
}

static void PrintUsage()
{
    Console.Error.WriteLine("""
        Distill — Web Page to Markdown for AI Agents

        Usage: distill <url> [url2 ...] [options]

        Options:
          --render          Force browser rendering (skip SPA detection)
          --static          Force static fetch (skip browser entirely)
          --visible         Launch Chrome visibly (non-headless), best for anti-bot sites
          --profile <dir>   Use existing Chrome profile (helps avoid bot detection)
          -o, --output      Output to file instead of stdout
          --browser <path>  Path to Chrome/Edge binary
          --cdp <ws-url>    Connect to existing CDP endpoint
          --timeout <ms>    Override timeout (auto-scales: static 15s, render 60s, visible 90s)
          --delay <ms>      Delay between requests when processing multiple URLs
          --stdin           Read URLs from stdin (one per line)
          -h, --help        Show this help

        Architecture:
          Auto-escalation: pages are fetched statically first. If blocked or SPA
          detected, escalates to headless Chrome. If headless gets captcha'd,
          auto-retries with visible Chrome + your default profile. Network idle
          detection replaces fixed delays — works with any SPA framework.

        Examples:
          distill https://example.com
          distill https://example.com --render -o output.md
          distill https://aliexpress.com/w/wholesale-ssd.html --render --visible
          cat urls.txt | distill --stdin --delay 1000
        """);
}

class CliOptions
{
    public List<Uri> Urls { get; set; } = [];
    public bool ForceRender { get; set; }
    public bool ForceStatic { get; set; }
    public string? OutputFile { get; set; }
    public string? BrowserPath { get; set; }
    public string? ProfileDir { get; set; }
    public Uri? CdpEndpoint { get; set; }
    public TimeSpan Timeout { get; set; } = TimeSpan.Zero;
    public bool TimeoutExplicitlySet { get; set; }
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;
    public bool ReadStdin { get; set; }
    public bool Visible { get; set; }
    public bool TriedEscalation { get; set; }

    /// <summary>
    /// Returns the effective timeout, auto-scaling based on mode if the user didn't set one.
    /// Static: 15s, Headless render: 60s, Visible: 90s.
    /// </summary>
    public TimeSpan EffectiveTimeout =>
        TimeoutExplicitlySet ? Timeout :
        Visible ? TimeSpan.FromSeconds(90) :
        ForceRender ? TimeSpan.FromSeconds(60) :
        TimeSpan.FromSeconds(30);
}
