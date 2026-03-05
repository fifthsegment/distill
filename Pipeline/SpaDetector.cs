using AngleSharp;
using AngleSharp.Dom;

namespace Distill.Pipeline;

public static class SpaDetector
{
    private static readonly string[] MountPointIds =
        ["app", "root", "__next", "__nuxt", "___gatsby", "svelte", "main-app"];

    public static async Task<bool> IsSpaAsync(string html)
    {
        var config = Configuration.Default;
        var context = BrowsingContext.New(config);
        var doc = await context.OpenAsync(req => req.Content(html));

        int signals = 0;

        if (BodyHasLittleText(doc))
            signals++;

        if (HasMountPointDiv(doc))
            signals++;

        if (HasHeavyScriptBundles(doc))
            signals++;

        if (HasNoScriptWarning(doc))
            signals++;

        return signals >= 2;
    }

    private static bool BodyHasLittleText(IDocument doc)
    {
        var body = doc.Body;
        if (body is null) return true;

        var text = body.TextContent.Trim();
        return text.Length < 100;
    }

    private static bool HasMountPointDiv(IDocument doc)
    {
        foreach (var id in MountPointIds)
        {
            if (doc.GetElementById(id) is not null)
                return true;
        }
        return false;
    }

    private static bool HasHeavyScriptBundles(IDocument doc)
    {
        var scripts = doc.QuerySelectorAll("script[src]");
        int bundleCount = 0;
        foreach (var script in scripts)
        {
            var src = script.GetAttribute("src") ?? "";
            if (src.Contains("chunk") || src.Contains("bundle") || src.Contains("vendor") ||
                src.Contains("main") || src.Contains("app"))
                bundleCount++;
        }
        return bundleCount >= 2;
    }

    private static bool HasNoScriptWarning(IDocument doc)
    {
        var noscript = doc.QuerySelector("noscript");
        if (noscript is null) return false;

        var text = noscript.TextContent.ToLowerInvariant();
        return text.Contains("javascript") || text.Contains("enable") || text.Contains("browser");
    }
}
