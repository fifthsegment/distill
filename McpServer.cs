using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Distill.Pipeline;
using Distill.Cdp;

namespace Distill;

public sealed class McpServer : IAsyncDisposable
{
    private readonly Fetcher _fetcher = new();
    private BrowserLauncher? _launcher;
    private CdpClient? _browserClient;

    private static readonly string Version =
        typeof(McpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    private static JsonObject BuildToolSchema()
    {
        var props = new JsonObject();
        var urlProp = new JsonObject();
        urlProp["type"] = JsonValue.Create("string");
        urlProp["description"] = JsonValue.Create("The URL to convert to markdown");
        props["url"] = urlProp;

        var renderProp = new JsonObject();
        renderProp["type"] = JsonValue.Create("boolean");
        renderProp["description"] = JsonValue.Create("Force browser rendering for JS-heavy pages");
        props["render"] = renderProp;

        var visibleProp = new JsonObject();
        visibleProp["type"] = JsonValue.Create("boolean");
        visibleProp["description"] = JsonValue.Create("Launch Chrome visibly to bypass headless detection");
        props["visible"] = visibleProp;

        var profileProp = new JsonObject();
        profileProp["type"] = JsonValue.Create("string");
        profileProp["description"] = JsonValue.Create("Path to Chrome profile directory");
        props["profile"] = profileProp;

        var required = new JsonArray();
        required.Add(JsonValue.Create("url"));

        var schema = new JsonObject();
        schema["type"] = JsonValue.Create("object");
        schema["properties"] = props;
        schema["required"] = required;
        return schema;
    }

    public async Task RunAsync()
    {
        while (true)
        {
            var line = await Console.In.ReadLineAsync();
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonNode? msg;
            try { msg = JsonNode.Parse(line); }
            catch { continue; }

            var method = msg?["method"]?.GetValue<string>();
            var id = msg?["id"];

            switch (method)
            {
                case "initialize":
                    var initResult = new JsonObject();
                    initResult["protocolVersion"] = JsonValue.Create("2024-11-05");
                    var caps = new JsonObject();
                    caps["tools"] = new JsonObject();
                    initResult["capabilities"] = caps;
                    var serverInfo = new JsonObject();
                    serverInfo["name"] = JsonValue.Create("distill");
                    serverInfo["version"] = JsonValue.Create(Version);
                    initResult["serverInfo"] = serverInfo;
                    await Respond(id, initResult);
                    break;

                case "notifications/initialized":
                    break;

                case "tools/list":
                    var tool = new JsonObject();
                    tool["name"] = JsonValue.Create("distill");
                    tool["description"] = JsonValue.Create("Convert a web page URL to clean, LLM-ready markdown. Auto-escalates from static fetch to headless Chrome to visible Chrome as needed.");
                    tool["inputSchema"] = BuildToolSchema();
                    var tools = new JsonArray();
                    tools.Add(tool);
                    var listResult = new JsonObject();
                    listResult["tools"] = tools;
                    await Respond(id, listResult);
                    break;

                case "tools/call":
                    await HandleToolCall(id, msg?["params"]);
                    break;

                default:
                    if (id is not null)
                        await RespondError(id, -32601, $"Method not found: {method}");
                    break;
            }
        }
    }

    private async Task HandleToolCall(JsonNode? id, JsonNode? parameters)
    {
        var toolName = parameters?["name"]?.GetValue<string>();
        if (toolName != "distill")
        {
            await RespondError(id, -32602, $"Unknown tool: {toolName}");
            return;
        }

        var args = parameters?["arguments"];
        var urlStr = args?["url"]?.GetValue<string>();
        if (urlStr is null || !Uri.TryCreate(urlStr, UriKind.Absolute, out var url))
        {
            await RespondError(id, -32602, "Missing or invalid 'url' parameter");
            return;
        }

        var forceRender = args?["render"]?.GetValue<bool>() ?? false;
        var visible = args?["visible"]?.GetValue<bool>() ?? false;
        var profile = args?["profile"]?.GetValue<string>();

        try
        {
            var markdown = await DistillUrl(url, forceRender, visible, profile);

            var textContent = new JsonObject();
            textContent["type"] = JsonValue.Create("text");
            textContent["text"] = JsonValue.Create(markdown);
            var content = new JsonArray();
            content.Add(textContent);
            var result = new JsonObject();
            result["content"] = content;
            await Respond(id, result);
        }
        catch (Exception ex)
        {
            var textContent = new JsonObject();
            textContent["type"] = JsonValue.Create("text");
            textContent["text"] = JsonValue.Create($"Error: {ex.Message}");
            var content = new JsonArray();
            content.Add(textContent);
            var result = new JsonObject();
            result["content"] = content;
            result["isError"] = JsonValue.Create(true);
            await Respond(id, result);
        }
    }

    private async Task<string> DistillUrl(Uri url, bool forceRender, bool visible, string? profile)
    {
        string html = "";
        bool usedBrowser = false;

        if (forceRender || visible)
        {
            html = await RenderWithBrowser(url, visible, profile);
            usedBrowser = true;
        }
        else
        {
            bool needsBrowser = false;
            try
            {
                html = await _fetcher.FetchAsync(url);
                var staticBlock = BlockDetector.Check(html);
                if (staticBlock.IsBlocked)
                    needsBrowser = true;
                else if (await SpaDetector.IsSpaAsync(html))
                    needsBrowser = true;
            }
            catch (HttpRequestException)
            {
                needsBrowser = true;
            }

            if (needsBrowser)
            {
                html = await RenderWithBrowser(url, visible, profile);
                usedBrowser = true;
            }
        }

        var blockCheck = BlockDetector.Check(html);
        if (blockCheck.IsBlocked && usedBrowser && !visible)
        {
            var profileDir = profile ?? DefaultChromeProfileDir();
            if (profileDir is not null)
            {
                html = await RenderWithBrowser(url, true, profileDir);
                blockCheck = BlockDetector.Check(html);
            }
        }
        if (blockCheck.IsBlocked)
            throw new InvalidOperationException(blockCheck.Message);

        if (!usedBrowser)
            html = HtmlCleaner.Clean(html);

        var extracted = ContentExtractor.Extract(html, url);
        var markdown = MarkdownConverter.Convert(extracted);

        var stripped = BlockDetector.StripInvisibleChars(markdown).Trim();
        if (string.IsNullOrWhiteSpace(stripped))
            throw new InvalidOperationException("Extraction produced empty output");

        return stripped;
    }

    private async Task<string> RenderWithBrowser(Uri url, bool visible, string? profile)
    {
        await EnsureBrowser(visible, profile);
        await using var commands = new CdpCommands(_browserClient!, _launcher!.Port);
        var timeout = visible ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(60);
        return await commands.GetRenderedHtmlAsync(url, timeout, preExtractScripts: PageCleaner.AllScripts);
    }

    private async Task EnsureBrowser(bool visible, string? profile)
    {
        if (_launcher is not null) return;

        var browserPath = BrowserLauncher.FindBrowser(null)
            ?? throw new InvalidOperationException("No Chrome or Edge browser found");

        _launcher = new BrowserLauncher();
        var wsUri = await _launcher.LaunchAsync(browserPath, profile, visible);
        _browserClient = new CdpClient();
        await _browserClient.ConnectAsync(wsUri);
    }

    private static string? DefaultChromeProfileDir()
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

    private static async Task Respond(JsonNode? id, JsonObject result)
    {
        var response = new JsonObject();
        response["jsonrpc"] = JsonValue.Create("2.0");
        response["id"] = id?.DeepClone();
        response["result"] = result;
        await Console.Out.WriteLineAsync(response.ToJsonString());
        await Console.Out.FlushAsync();
    }

    private static async Task RespondError(JsonNode? id, int code, string message)
    {
        var error = new JsonObject();
        error["code"] = JsonValue.Create(code);
        error["message"] = JsonValue.Create(message);
        var response = new JsonObject();
        response["jsonrpc"] = JsonValue.Create("2.0");
        response["id"] = id?.DeepClone();
        response["error"] = error;
        await Console.Out.WriteLineAsync(response.ToJsonString());
        await Console.Out.FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_browserClient is not null) await _browserClient.DisposeAsync();
        if (_launcher is not null) await _launcher.DisposeAsync();
        _fetcher.Dispose();
    }
}
