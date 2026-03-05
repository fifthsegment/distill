using AngleSharp;
using AngleSharp.Dom;
using SmartReader;

namespace Distill.Pipeline;

public static class ContentExtractor
{
    public static string Extract(string html, Uri url)
    {
        var fallback = ExtractBodyFallback(html);
        var article = Reader.ParseArticle(url.ToString(), html);

        if (article.IsReadable && !string.IsNullOrWhiteSpace(article.Content))
        {
            // SmartReader is great for articles but often picks a small nav/header section
            // on search/listing pages. If its output is less than 10% of the body fallback,
            // it likely missed the main content.
            if (article.Content.Length > fallback.Length * 0.1)
                return article.Content;
        }

        return fallback;
    }

    private static string ExtractBodyFallback(string html)
    {
        var config = Configuration.Default;
        var context = BrowsingContext.New(config);
        var doc = context.OpenAsync(req => req.Content(html)).Result;

        var body = doc.Body;
        if (body is null) return html;

        foreach (var tag in body.QuerySelectorAll("script, style, noscript, link[rel=stylesheet], svg"))
            tag.Remove();

        return body.InnerHtml;
    }
}
