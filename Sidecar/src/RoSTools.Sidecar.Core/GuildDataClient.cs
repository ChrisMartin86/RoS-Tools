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

            // Stop before writing, when the server is willing to say how much there
            // is. A mistyped DataUrl pointing at an ISO or a video used to be copied
            // into %TEMP% in full and then read into memory (several times over, once
            // decoded) before the validator's size rule ever got a look at it.
            var ceiling = GuildDataValidator.MaxRosterBytes;
            if (response.Content.Headers.ContentLength is { } declared && declared > ceiling)
            {
                return FetchResult.Failure(TooLargeMessage(declared, ceiling));
            }

            var staging = Path.Combine(
                Path.GetTempPath(),
                $"GuildData-{Guid.NewGuid():N}.lua");

            try
            {
                // Content-Length is a hint, not a promise: it can be absent (chunked)
                // or simply wrong. Count what actually arrives and abandon the
                // transfer the moment it goes over, rather than filling the disk.
                var bytes = await CopyWithCeilingAsync(response, staging, ceiling, ct)
                    .ConfigureAwait(false);

                return new FetchResult(
                    FetchOutcome.Downloaded,
                    staging,
                    Format(response.Headers.ETag),
                    response.Content.Headers.LastModified?.ToString("R"),
                    bytes,
                    null);
            }
            catch (TooLargeException tooLarge)
            {
                TryDelete(staging);
                return FetchResult.Failure(tooLarge.Message);
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

    /// <summary>
    /// Streams the body to <paramref name="staging"/>, giving up as soon as more
    /// than <paramref name="ceiling"/> bytes have arrived. Returns the byte count.
    /// </summary>
    private static async Task<long> CopyWithCeilingAsync(
        HttpResponseMessage response,
        string staging,
        long ceiling,
        CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = File.Create(staging);

        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0)
            {
                return total;
            }

            total += read;
            if (total > ceiling)
            {
                throw new TooLargeException(TooLargeMessage(null, ceiling));
            }

            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    private static string TooLargeMessage(long? declared, long ceiling)
    {
        var size = declared is { } bytes ? $"{bytes} bytes" : $"more than {ceiling} bytes";

        return $"the data source offered {size}, past the {ceiling}-byte ceiling for a " +
               "roster file. Nothing was downloaded; check the URL.";
    }

    /// <summary>Private on purpose - it never leaves <see cref="FetchAsync"/>.</summary>
    private sealed class TooLargeException(string message) : Exception(message);

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
