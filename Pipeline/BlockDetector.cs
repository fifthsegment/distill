using AngleSharp;
using AngleSharp.Dom;

namespace Distill.Pipeline;

/// <summary>
/// Detects captcha pages, bot blocks, and rate-limit responses.
/// Returns a clear error instead of passing garbage HTML downstream.
/// </summary>
public static class BlockDetector
{
    private static readonly string[] BlockSignals =
    [
        "unusual traffic",
        "verify you are human",
        "captcha",
        "please verify",
        "access denied",
        "rate limit",
        "too many requests",
        "blocked",
        "security check",
        "challenge-platform",
        "cf-challenge",
        "just a moment",
        "checking your browser",
        "enable javascript",
        "enable cookies",
        "bot detection",
        "are you a robot",
        "slide to verify",
    ];

    /// <summary>
    /// Strong signals that almost always mean a block page, regardless of page size.
    /// </summary>
    private static readonly string[] StrongSignals =
    [
        "unusual traffic",
        "slide to verify",
        "captcha",
        "verify you are human",
        "are you a robot",
        "cf-challenge",
        "challenge-platform",
    ];

    public static BlockResult Check(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return BlockResult.Blocked("Empty response — page returned no content.");

        // Detect JS-only challenge pages: tiny visible content + heavy obfuscated JS.
        // Sites like Temu serve a near-empty <body> that loads an anti-bot challenge script.
        var jsChallenge = DetectJsChallengePage(html);
        if (jsChallenge is not null)
            return jsChallenge.Value;

        var textLower = html.ToLowerInvariant();

        // Strong signals: if two or more appear, it's definitely a block page
        int strongHits = 0;
        string? firstSignal = null;
        foreach (var signal in StrongSignals)
        {
            if (textLower.Contains(signal))
            {
                firstSignal ??= signal;
                strongHits++;
            }
        }

        if (strongHits >= 2)
            return MakeBlockResult(firstSignal!);

        // Weak signals: only flag if the page has very little real text content
        if (strongHits == 1 || BlockSignals.Any(s => textLower.Contains(s)))
        {
            var textContent = ExtractVisibleText(html);
            if (textContent.Length < 200)
                return MakeBlockResult(firstSignal ?? "block page");
        }

        return BlockResult.Ok;
    }

    /// <summary>
    /// Detects pages that are essentially a JS-only anti-bot challenge:
    /// near-zero visible text, one or two script tags with heavy obfuscated code.
    /// Indicators: very short visible text, large script blocks, obfuscation patterns
    /// (hex variable names like _0x, function chaining, eval-like constructs).
    /// </summary>
    private static BlockResult? DetectJsChallengePage(string html)
    {
        var visibleText = ExtractVisibleText(html);
        if (visibleText.Length > 100)
            return null;

        int scriptContentLength = 0;
        int scriptCount = 0;
        int idx = 0;
        while (true)
        {
            int open = html.IndexOf("<script", idx, StringComparison.OrdinalIgnoreCase);
            if (open < 0) break;
            int openEnd = html.IndexOf(">", open);
            if (openEnd < 0) break;
            int close = html.IndexOf("</script", openEnd, StringComparison.OrdinalIgnoreCase);
            if (close < 0) break;
            scriptCount++;
            scriptContentLength += close - openEnd - 1;
            idx = close + 9;
        }

        if (scriptCount == 0)
            return null;

        bool heavilyObfuscated = html.Contains("_0x") || html.Contains("\\x") ||
                                  html.Contains("challenge") || html.Contains("captcha");
        bool scriptDominatedPage = scriptContentLength > html.Length * 0.6;
        bool tinyHtmlBigScript = visibleText.Length < 20 && scriptContentLength > 500;

        if (tinyHtmlBigScript && (heavilyObfuscated || scriptDominatedPage))
        {
            return BlockResult.Blocked(
                "Detected anti-bot JS challenge page (no visible content, only obfuscated scripts). " +
                "The site requires a real browser session. " +
                "Try: using --render with --profile pointing to your real Chrome profile.");
        }

        return null;
    }

    private static BlockResult MakeBlockResult(string signal) =>
        BlockResult.Blocked($"Detected block/captcha page (signal: \"{signal}\"). " +
            "The site is likely rate-limiting or blocking automated access. " +
            "Try: waiting before retrying, using --profile with your real Chrome profile, " +
            "or reducing request frequency with --delay.");

    /// <summary>
    /// Strips zero-width and other invisible Unicode characters that sites use to
    /// fake non-empty content (e.g. Temu fills pages with U+200B).
    /// </summary>
    internal static string StripInvisibleChars(string text)
    {
        return System.Text.RegularExpressions.Regex.Replace(text,
            @"[\u200B\u200C\u200D\u2060\uFEFF\u00AD\u200E\u200F\u202A-\u202E\u2061-\u2064]+", "");
    }

    private static string ExtractVisibleText(string html)
    {
        var sb = new System.Text.StringBuilder();
        bool inTag = false;
        bool inScript = false;

        for (int i = 0; i < html.Length; i++)
        {
            char c = html[i];
            if (c == '<')
            {
                inTag = true;
                var rest = html.AsSpan(i, Math.Min(10, html.Length - i));
                if (rest.StartsWith("<script", StringComparison.OrdinalIgnoreCase) ||
                    rest.StartsWith("<style", StringComparison.OrdinalIgnoreCase))
                    inScript = true;
                else if (rest.StartsWith("</script", StringComparison.OrdinalIgnoreCase) ||
                         rest.StartsWith("</style", StringComparison.OrdinalIgnoreCase))
                    inScript = false;
            }
            else if (c == '>')
            {
                inTag = false;
            }
            else if (!inTag && !inScript)
            {
                sb.Append(c);
            }
        }

        var raw = sb.ToString();
        raw = StripInvisibleChars(raw);
        return System.Text.RegularExpressions.Regex.Replace(raw, @"\s+", " ").Trim();
    }
}

public readonly struct BlockResult
{
    public bool IsBlocked { get; }
    public string? Message { get; }

    private BlockResult(bool blocked, string? message)
    {
        IsBlocked = blocked;
        Message = message;
    }

    public static BlockResult Blocked(string message) => new(true, message);
    public static readonly BlockResult Ok = new(false, null);
}
