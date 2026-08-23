using System.Net;
using System.Net.Http.Headers;
using System.Reflection;

namespace RoSTools.Sidecar.Core;

public enum FetchOutcome
{
    /// <summary>New bytes arrived and were staged to disk.</summary>
    Downloaded,

    /// <summary>Server answered 304 - what is already installed is current.</summary>
    NotModified,

    Failed,
}

public sealed record FetchResult(
    FetchOutcome Outcome,
    string? StagingPath,
    string? ETag,
    string? LastModified,
    long Bytes,
    string? Error)
{
    public static FetchResult Failure(string error) =>
        new(FetchOutcome.Failed, null, null, null, 0, error);
}

/// <summary>
/// Conditional GET against the published <c>GuildData.lua</c>. Nothing here knows
/// what a valid roster looks like - that is <see cref="GuildDataValidator"/>'s job,
/// and it runs before anything is installed.
/// </summary>
public sealed class GuildDataClient : IDisposable
{
    private readonly HttpClient _http;

    public GuildDataClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    public static string UserAgent
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
            return $"RoSTools-Sidecar/{version} (+https://github.com/ChrisMartin86/RoS-Tools)";
        }
    }

    /// <summary>
    /// Downloads to a temp file when the remote copy has changed. The caller owns
    /// the returned staging path and must delete it if it does not install it.
    /// </summary>
    public async Task<FetchResult> FetchAsync(
        string url,
        string? etag,
        string? lastModified,
        bool force,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (!force)
        {
            // TryAddWithoutValidation: GitHub returns weak ETags (W/"..."), and the
            // strongly-typed header collection rejects some of what it sends back.
            if (!string.IsNullOrWhiteSpace(etag))
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }

            if (!string.IsNullOrWhiteSpace(lastModified))
            {
                request.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FetchResult.Failure($"could not reach the data source: {ex.Message}");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new FetchResult(FetchOutcome.NotModified, null, etag, lastModified, 0, null);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return FetchResult.Failure(
                    $"the roster file was not found at {url} (404). Check the URL and that the repo is public.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return FetchResult.Failure(
                    $"the data source answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var staging = Path.Combine(
                Path.GetTempPath(),
                $"GuildData-{Guid.NewGuid():N}.lua");

            try
            {
                await using (var target = File.Create(staging))
                {
                    await response.Content.CopyToAsync(target, ct).ConfigureAwait(false);
                }

                var bytes = new FileInfo(staging).Length;
                return new FetchResult(
                    FetchOutcome.Downloaded,
                    staging,
                    Format(response.Headers.ETag),
                    response.Content.Headers.LastModified?.ToString("R"),
                    bytes,
                    null);
            }
            catch (Exception ex)
            {
                TryDelete(staging);

                if (ex is OperationCanceledException && ct.IsCancellationRequested)
                {
                    throw;
                }

                return FetchResult.Failure($"the download did not complete: {ex.Message}");
            }
        }
    }

    private static string? Format(EntityTagHeaderValue? tag) =>
        tag is null ? null : (tag.IsWeak ? "W/" : string.Empty) + tag.Tag;

    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"could not remove staging file {path}: {ex.Message}");
        }
    }

    public void Dispose() => _http.Dispose();
}
