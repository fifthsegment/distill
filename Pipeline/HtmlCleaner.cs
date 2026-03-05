using AngleSharp;
using AngleSharp.Dom;

namespace Distill.Pipeline;

/// <summary>
/// Server-side DOM cleanup — the AngleSharp equivalent of PageCleaner's JS injection.
/// Used on the static (HttpClient) path where we don't have a browser to run JS in.
/// </summary>
public static class HtmlCleaner
{
    private static readonly string[] ChromeSelectors =
    [
        "nav", "header", "footer",
        "[role='navigation']", "[role='banner']", "[role='contentinfo']",
        "[role='dialog']", "[role='alertdialog']",
        "script", "style", "noscript", "link[rel='stylesheet']", "svg",
        "iframe",
    ];

    private static readonly string[] ChromePatterns =
    [
        "cookie", "consent", "gdpr", "privacy-banner",
        "notification", "subscribe-popup", "newsletter",
        "overlay", "modal", "popup",
        "sticky-header", "site-header", "site-footer",
        "breadcrumb",
        "social-share", "share-buttons",
    ];

    public static string Clean(string html)
    {
        var config = Configuration.Default;
        var context = BrowsingContext.New(config);
        var doc = context.OpenAsync(req => req.Content(html)).Result;

        var body = doc.Body;
        if (body is null) return html;

        foreach (var selector in ChromeSelectors)
        {
            foreach (var el in body.QuerySelectorAll(selector))
                el.Remove();
        }

        RemoveByClassOrId(body, ChromePatterns);
        AnnotateImages(body);

        return body.InnerHtml;
    }

    private static void RemoveByClassOrId(IElement root, string[] patterns)
    {
        var elements = root.QuerySelectorAll("*");
        foreach (var el in elements)
        {
            if (el.Parent is null) continue;

            var id = (el.Id ?? "").ToLowerInvariant();
            var cls = (el.ClassName ?? "").ToLowerInvariant();
            var combined = id + " " + cls;

            foreach (var pattern in patterns)
            {
                if (combined.Contains(pattern))
                {
                    el.Remove();
                    break;
                }
            }
        }
    }

    private static void AnnotateImages(IElement root)
    {
        foreach (var img in root.QuerySelectorAll("img"))
        {
            var alt = img.GetAttribute("alt") ?? "";
            if (!string.IsNullOrWhiteSpace(alt)) continue;

            var label = img.GetAttribute("aria-label")
                ?? img.GetAttribute("title")
                ?? img.ParentElement?.GetAttribute("aria-label")
                ?? img.ParentElement?.GetAttribute("title");

            if (!string.IsNullOrEmpty(label))
                img.SetAttribute("alt", label);
        }
    }
}
