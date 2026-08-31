using System.Text.Json;
using System.Text.Json.Serialization;
using RoSTools.Sidecar.Core.Blizzard;

namespace RoSTools.Sidecar.Core.Web;

/// <summary>
/// The JSON API behind <see cref="ConsoleServer"/>. Transport-free on purpose:
/// it takes a path, a method and a body string, so the whole surface is testable
/// without opening a socket.
/// <para>
/// One rule runs through all of it: <b>the client secret never comes back out.</b>
/// Every response reports only whether one is stored. A console the user can open
/// is also a console anything else on the machine could reach if it had the token,
/// and echoing the secret would turn a token leak into a credential leak.
/// </para>
/// </summary>
public sealed class ConsoleApi(
    SettingsStore store,
    PullService pulls,
    ISecretProtector protector,
    Func<Task<UpdateResult>>? checkNow = null)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Guards <see cref="_pullCancellation"/>. Interlocked.Exchange plus a
    /// Dispose raced CancelPull's Cancel() and turned a well-timed cancel into a
    /// 500 from an ObjectDisposedException.</summary>
    private readonly Lock _pullGate = new();

    private CancellationTokenSource? _pullCancellation;

    public async Task<(int Status, string Body)> HandleAsync(
        string path,
        string method,
        string body,
        CancellationToken ct)
    {
        try
        {
            return (path, method.ToUpperInvariant()) switch
            {
                ("/api/state", "GET") => Ok(BuildState()),
                ("/api/credentials", "POST") => SaveCredentials(body),
                ("/api/credentials", "DELETE") => ClearCredentials(),
                ("/api/pull", "GET") => Ok(BuildPull()),
                ("/api/pull", "POST") => StartPull(body, ct),
                ("/api/pull", "DELETE") => CancelPull(),
                ("/api/install", "POST") => InstallPull(body),
                ("/api/check", "POST") => await CheckNowAsync().ConfigureAwait(false),
                _ => (404, Serialize(new { ok = false, error = "No such endpoint." })),
            };
        }
        catch (JsonException)
        {
            return (400, Serialize(new { ok = false, error = "That request was not valid JSON." }));
        }
        catch (Exception ex)
        {
            Log.Error($"console api {method} {path} failed", ex);
            return (500, Serialize(new { ok = false, error = ex.Message }));
        }
    }

    // ------------------------------------------------------------------
    // State
    // ------------------------------------------------------------------
    private object BuildState()
    {
        var settings = store.Snapshot();
        var destination = PullService.InstalledDataFile(settings);

        object? installed = null;
        if (destination is not null)
        {
            var check = GuildDataValidator.Validate(destination);
            installed = new
            {
                path = destination,
                ok = check.Ok,
                reason = check.Reason,
                entries = check.Entries,
                generatedAt = check.GeneratedAt,
                generatedEpoch = check.GeneratedEpoch,
                ageDays = check.AgeInDays is { } days ? Math.Round(days, 1) : (double?)null,
                exportBytes = check.ExportBytes,
                warning = check.Warning,
                characters = check.Ok
                    ? (GuildDataValidator.EntriesOf(destination) ?? [])
                        .Select(e => new { key = e.Key, ilvl = e.Value })
                        .OrderByDescending(e => e.ilvl)
                        .ToList()
                    : null,
            };
        }

        return new
        {
            ok = true,
            addOn = new
            {
                path = settings.AddOnPath,
                resolved = destination is null ? null : Path.GetDirectoryName(destination),
                found = destination is not null,
            },
            guild = settings.Guild is { } g
                ? new { region = g.Region, realm = g.Realm, guild = g.Guild }
                : null,
            credentials = new
            {
                present = !string.IsNullOrWhiteSpace(settings.BlizzardClientId) &&
                          !string.IsNullOrWhiteSpace(settings.BlizzardClientSecretProtected),
                clientId = Mask(settings.BlizzardClientId),
                region = settings.BlizzardRegion ?? settings.GuildRegion ?? "us",
                canStore = protector.CanStoreSecrets,
                // The environment variables the Python exporter uses. Honoured here
                // too, so a machine that already has them set needs no secret on disk
                // at all - and the page has to be able to say which source is in play.
                fromEnvironment = HasEnvironmentCredentials(),
            },
            regions = BlizzardCredentials.Regions,
            poll = new
            {
                dataUrl = settings.DataUrl,
                intervalHours = settings.EffectivePollHours,
                lastCheckUtc = settings.LastCheckUtc,
                lastUpdateUtc = settings.LastUpdateUtc,
                lastError = settings.LastError,
            },
            installed,
            shrinkFloorPercent = (int)(PullService.ShrinkFloor * 100),
        };
    }

    // ------------------------------------------------------------------
    // Credentials
    // ------------------------------------------------------------------
    private (int, string) SaveCredentials(string body)
    {
        var input = JsonSerializer.Deserialize<CredentialsInput>(body, Json);

        if (input is null || string.IsNullOrWhiteSpace(input.ClientId) ||
            string.IsNullOrWhiteSpace(input.ClientSecret))
        {
            return (400, Serialize(new { ok = false, error = "Both a client ID and a secret are required." }));
        }

        var clientId = input.ClientId.Trim();
        var secret = input.ClientSecret.Trim();
        var region = (input.Region ?? "us").Trim().ToLowerInvariant();

        if (!BlizzardCredentials.IsKnownRegion(region))
        {
            return (400, Serialize(new
            {
                ok = false,
                error = $"'{region}' is not a supported region ({string.Join(", ", BlizzardCredentials.Regions)}).",
            }));
        }

        if (!BlizzardCredentials.LooksLikeClientId(clientId))
        {
            return (400, Serialize(new
            {
                ok = false,
                error = "That does not look like a client ID. Copy just the ID from " +
                        "develop.battle.net, without any surrounding label.",
            }));
        }

        if (!protector.CanStoreSecrets)
        {
            return (400, Serialize(new
            {
                ok = false,
                error = "This build cannot encrypt the secret at rest, so it will not store one. " +
                        "Set BLIZZARD_CLIENT_ID and BLIZZARD_CLIENT_SECRET in the environment instead.",
            }));
        }

        string protectedSecret;
        try
        {
            protectedSecret = protector.Protect(secret);
        }
        catch (Exception ex)
        {
            return (500, Serialize(new { ok = false, error = $"Could not encrypt the secret: {ex.Message}" }));
        }

        store.Update(s =>
        {
            s.BlizzardClientId = clientId;
            s.BlizzardClientSecretProtected = protectedSecret;
            s.BlizzardRegion = region;
        });

        Log.Info("Blizzard credentials saved (secret encrypted with DPAPI, current user).");

        return Ok(new { ok = true, message = "Credentials saved." });
    }

    private (int, string) ClearCredentials()
    {
        store.Update(s =>
        {
            s.BlizzardClientId = null;
            s.BlizzardClientSecretProtected = null;
        });

        Log.Info("Blizzard credentials cleared.");
        return Ok(new { ok = true, message = "Credentials cleared." });
    }

    /// <summary>
    /// Environment first, matching <c>Tools/fetch_guild_info.py</c>, so a machine
    /// already set up to run the exporter needs nothing stored.
    /// </summary>
    private BlizzardCredentials? ResolveCredentials(string region)
    {
        var envId = Environment.GetEnvironmentVariable("BLIZZARD_CLIENT_ID");
        var envSecret = Environment.GetEnvironmentVariable("BLIZZARD_CLIENT_SECRET");

        if (!string.IsNullOrWhiteSpace(envId) && !string.IsNullOrWhiteSpace(envSecret))
        {
            return new BlizzardCredentials(envId.Trim(), envSecret.Trim(), region);
        }

        var settings = store.Snapshot();
        if (string.IsNullOrWhiteSpace(settings.BlizzardClientId) ||
            string.IsNullOrWhiteSpace(settings.BlizzardClientSecretProtected))
        {
            return null;
        }

        var secret = protector.Unprotect(settings.BlizzardClientSecretProtected);
        return secret is null ? null : new BlizzardCredentials(settings.BlizzardClientId, secret, region);
    }

    private static bool HasEnvironmentCredentials() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BLIZZARD_CLIENT_ID")) &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BLIZZARD_CLIENT_SECRET"));

    // ------------------------------------------------------------------
    // Pull
    // ------------------------------------------------------------------
    private (int, string) StartPull(string body, CancellationToken ct)
    {
        if (pulls.IsRunning)
        {
            return (409, Serialize(new { ok = false, error = "A pull is already running." }));
        }

        var input = JsonSerializer.Deserialize<PullInput>(body, Json) ?? new PullInput();
        var settings = store.Snapshot();

        var region = (input.Region ?? settings.BlizzardRegion ?? settings.GuildRegion ?? "us")
            .Trim().ToLowerInvariant();
        var realm = (input.Realm ?? settings.GuildRealm ?? string.Empty).Trim();
        var guild = (input.Guild ?? settings.GuildName ?? string.Empty).Trim();

        if (realm.Length == 0 || guild.Length == 0)
        {
            return (400, Serialize(new { ok = false, error = "A realm and a guild name are required." }));
        }

        var credentials = ResolveCredentials(region);
        if (credentials is null)
        {
            return (400, Serialize(new
            {
                ok = false,
                error = "No usable Blizzard credentials. Save a client ID and secret first.",
            }));
        }

        // Clamped: the value comes from the page, and a negative or absurd minimum
        // silently produces an empty or unfiltered roster.
        var minLevel = Math.Clamp(input.MinLevel ?? 1, 1, 80);

        CancellationTokenSource linked;
        lock (_pullGate)
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _pullCancellation = linked;
        }

        // Flip the running flag on THIS thread. The page polls as soon as this
        // response lands, and a flag set only once the queued task runs loses that
        // race - see PullService.Starting.
        pulls.MarkStarting();

        // Started, not awaited: a full roster is a minute or more of API calls, and
        // the page polls /api/pull for progress. PullAsync catches everything,
        // cancellation and HttpClient timeouts included, so this cannot fault.
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await pulls.PullAsync(
                        credentials, new PullRequest(region, realm, guild, minLevel), linked.Token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    lock (_pullGate)
                    {
                        if (ReferenceEquals(_pullCancellation, linked))
                        {
                            _pullCancellation = null;
                        }
                    }

                    linked.Dispose();
                }
            },
            CancellationToken.None);

        return (202, Serialize(new { ok = true, message = "Pull started." }));
    }

    private (int, string) CancelPull()
    {
        lock (_pullGate)
        {
            try
            {
                _pullCancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The pull finished between the null check and the call. Nothing to
                // cancel, and nothing worth reporting.
            }
        }

        return Ok(new { ok = true, message = "Cancelling." });
    }

    private object BuildPull()
    {
        var progress = pulls.Progress;
        var result = pulls.Last;

        return new
        {
            ok = true,
            running = pulls.IsRunning,
            progress = new
            {
                phase = progress.Phase.ToString(),
                done = progress.Done,
                total = progress.Total,
                message = progress.Message,
            },
            result = result is null ? null : new
            {
                ok = result.Ok,
                error = result.Error,
                atUtc = result.AtUtc,
                identity = result.Identity is { } id
                    ? new { region = id.Region, realm = id.Realm, guild = id.Guild }
                    : null,
                generatedEpoch = result.GeneratedEpoch,
                rosterSize = result.RosterSize,
                noProfile = result.NoProfile,
                droppedKeys = result.DroppedKeys,
                entries = result.Entries
                    .OrderByDescending(e => e.Ilvl)
                    .Select(e => new { key = e.Key, ilvl = e.Ilvl })
                    .ToList(),
                exportBytes = result.Validation?.ExportBytes,
                warning = result.Validation?.Warning,
                delta = new
                {
                    added = result.Delta.Added,
                    removed = result.Delta.Removed,
                    changed = result.Delta.Changed
                        .Select(c => new { key = c.Key, from = c.From, to = c.To })
                        .ToList(),
                },
            },
        };
    }

    private (int, string) InstallPull(string body)
    {
        var input = JsonSerializer.Deserialize<InstallInput>(body, Json) ?? new InstallInput();
        var outcome = pulls.Install(input.Override ?? false);

        return (outcome.Ok ? 200 : 400, Serialize(new
        {
            ok = outcome.Ok,
            message = outcome.Message,
            entries = outcome.Entries,
            needsOverride = outcome.NeedsOverride,
        }));
    }

    private async Task<(int, string)> CheckNowAsync()
    {
        if (checkNow is null)
        {
            return (501, Serialize(new { ok = false, error = "Checking is not wired up in this context." }));
        }

        var result = await checkNow().ConfigureAwait(false);

        return Ok(new
        {
            ok = !result.IsFailure,
            message = result.Message,
            entries = result.Entries,
            generatedAt = result.GeneratedAt,
        });
    }

    // ------------------------------------------------------------------
    private static (int, string) Ok(object payload) => (200, Serialize(payload));

    private static string Serialize(object payload) => JsonSerializer.Serialize(payload, Json);

    /// <summary>
    /// The client ID is not a secret, but it is an identifier the user may not want
    /// on a screen share; showing the tail is enough to confirm which app is in use.
    /// </summary>
    private static string? Mask(string? clientId) =>
        string.IsNullOrEmpty(clientId) ? null :
        clientId.Length <= 6 ? new string('*', clientId.Length) :
        new string('*', clientId.Length - 4) + clientId[^4..];

    private sealed record CredentialsInput
    {
        public string? ClientId { get; init; }
        public string? ClientSecret { get; init; }
        public string? Region { get; init; }
    }

    private sealed record PullInput
    {
        public string? Region { get; init; }
        public string? Realm { get; init; }
        public string? Guild { get; init; }
        public int? MinLevel { get; init; }
    }

    private sealed record InstallInput
    {
        public bool? Override { get; init; }
    }
}
