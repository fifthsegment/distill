using System.Net;

namespace Distill.Pipeline;

public sealed class Fetcher : IDisposable
{
    private const string UserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private readonly HttpClient _http;

    public Fetcher()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression =
                DecompressionMethods.GZip | DecompressionMethods.Brotli | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
        };

        _http = new HttpClient(handler);
        _http.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        _http.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        _http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        _http.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<string> FetchAsync(Uri url, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    public void Dispose() => _http.Dispose();
}
