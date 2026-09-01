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

    /// <summary>
    /// Serializes <c>/api/check</c>. 0 is idle, 1 is a check in flight.
    /// <para>
    /// The pull path has had a guard since it existed, because a second pull spends a
    /// second ~180-call quota. This one had none: rapid clicks on Check now fired
    /// concurrent <see cref="UpdateService"/> checks at one destination, which race on
    /// the same file and the same ETag cache entry.
    /// </para>
    /// </summary>
    private int _checking;

    /// <summary>
    /// How long a manual check may take before the console stops waiting on it. The
    /// poll path has <see cref="HttpClient"/>'s own timeout under it, but nothing
    /// bounded the request holding the console's guard.
    /// </summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromMinutes(2);

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
                ("/api/install", "POST") => await InstallPullAsync(body).ConfigureAwait(false),
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
            // The message stays in the log and only in the log. It was going into the
            // response body verbatim, which put full filesystem paths on a page any
            // local process could read with the token - and made this an unbounded
            // exception-message channel on the same endpoint set that handles the
            // client secret.
            Log.Error($"console api {method} {path} failed", ex);

            return (500, Serialize(new
            {
                ok = false,
                error = "Something went wrong handling that request. " +
                        "The details are in the sidecar log.",
            }));
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
                // A load failure is sticky and lives on the store, not on the
                // settings instance -- UpdateService.Record() clears
                // Current.LastError on every successful check, which used to
                // wipe the "your client secret needs re-entering" warning
                // within a minute of it appearing. Surface both, failure first.
                lastError = store.LoadFailure ?? settings.LastError,
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
            // Same rule as the catch-all: the detail is a log line, not a response
            // body. This is the endpoint that handles the secret itself.
            Log.Error("could not encrypt the Blizzard client secret", ex);

            return (500, Serialize(new
            {
                ok = false,
                error = "Could not encrypt the secret on this machine, so nothing was stored. " +
                        "The details are in the sidecar log; the environment variables are the " +
                        "way round it.",
            }));
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
        // Admission is one atomic step inside PullService, taken BEFORE any of the
        // work below. It used to be an IsRunning read here and a MarkStarting() call
        // ninety lines further down, with a JSON deserialize, a settings snapshot and
        // a DPAPI Unprotect syscall in between - a window wide enough that a
        // double-clicked button reliably put two pulls through it.
        var admission = pulls.TryStart();
        if (!admission.Granted)
        {
            var status = admission.Refusal == PullRefusal.TooSoon ? 429 : 409;
            return (status, Serialize(new { ok = false, error = admission.Message }));
        }

        var ticket = admission.Ticket!;

        PullPlan? plan;
        (int Status, string Body) refusal;
        string started;

        try
        {
            (plan, refusal) = PreparePull(body);

            // Serialized HERE, while this method still owns the ticket. Past the
            // handover below the ticket belongs to the running pull, and the catch
            // that frees it would be freeing the slot out from under a live pull:
            // IsRunning goes false, /api/install becomes reachable against a Last that
            // pull is about to replace, and a second POST is admitted. Serialize over
            // a constant anonymous type will not throw in practice - which is exactly
            // why it must not be the one statement standing on the wrong side of the
            // line.
            started = Serialize(new { ok = true, message = "Pull started." });
        }
        catch
        {
            // Anything that throws between admission and the queued task would leave
            // the slot claimed forever, and the console permanently reporting a pull
            // that does not exist.
            pulls.Abandon(ticket);
            throw;
        }

        if (plan is null)
        {
            pulls.Abandon(ticket);
            return refusal;
        }

        // The handover. Started, not awaited: a full roster is a minute or more of API
        // calls, and the page polls /api/pull for progress. PullAsync catches
        // everything, cancellation and HttpClient timeouts included, so this cannot
        // fault. It owns the ticket from here: it creates the cancellation source,
        // registers it against the ticket, and releases the slot in its own finally.
        _ = Task.Run(
            () => pulls.PullAsync(plan.Credentials, plan.Request, ticket, ct),
            CancellationToken.None);

        // Deliberately outside every catch above, and with nothing left that can throw.
        AfterPullHandover?.Invoke();

        return (202, started);
    }

    /// <summary>
    /// Everything a pull needs, worked out before the slot is handed over. Returns a
    /// null plan and the response to send when the request cannot be turned into one.
    /// </summary>
    private (PullPlan? Plan, (int, string) Refusal) PreparePull(string body)
    {
        var input = JsonSerializer.Deserialize<PullInput>(body, Json) ?? new PullInput();
        var settings = store.Snapshot();

        var region = (input.Region ?? settings.BlizzardRegion ?? settings.GuildRegion ?? "us")
            .Trim().ToLowerInvariant();
        var realm = (input.Realm ?? settings.GuildRealm ?? string.Empty).Trim();
        var guild = (input.Guild ?? settings.GuildName ?? string.Empty).Trim();

        if (realm.Length == 0 || guild.Length == 0)
        {
            return (null, (400, Serialize(new
            {
                ok = false,
                error = "A realm and a guild name are required.",
            })));
        }

        var credentials = ResolveCredentials(region);
        if (credentials is null)
        {
            return (null, (400, Serialize(new
            {
                ok = false,
                error = "No usable Blizzard credentials. Save a client ID and secret first.",
            })));
        }

        // Clamped: the value comes from the page, and a negative or absurd minimum
        // silently produces an empty or unfiltered roster.
        var minLevel = Math.Clamp(input.MinLevel ?? 1, 1, 80);

        return (new PullPlan(credentials, new PullRequest(region, realm, guild, minLevel)),
            (0, string.Empty));
    }

    private sealed record PullPlan(BlizzardCredentials Credentials, PullRequest Request);

    /// <summary>
    /// Test seam: runs immediately after a pull has been handed to its background
    /// task, and outside the catch that frees the ticket.
    /// <para>
    /// The handover is the line this method must not touch the ticket past, and the
    /// only throw site that ever sat on the wrong side of it - serializing a constant
    /// anonymous type - cannot be made to throw from a test. Without a seam the
    /// ordering has no coverage at all, and "a catch that frees the slot under a live
    /// pull" is exactly the kind of defect that comes back. Same reasoning as
    /// <c>ConsoleServer.FailAndClose</c>'s delegates, which exist so an ordering that
    /// only HTTP.sys can produce is still testable. Null in production.
    /// </para>
    /// </summary>
    internal Action? AfterPullHandover { get; set; }

    private (int, string) CancelPull()
    {
        // Answering "Cancelling." over a pull nothing is going to cancel is worse than
        // saying so: the ~180-call pull runs to completion and spends the whole quota
        // while the console reports it as cancelled.
        return pulls.CancelActive()
            ? Ok(new { ok = true, message = "Cancelling." })
            : (409, Serialize(new { ok = false, error = "There is no pull to cancel." }));
    }

    private object BuildPull()
    {
        // One coherent read. Taken separately, running could come back false beside
        // the PREVIOUS pull's successful result, which the page renders as a fresh
        // roster with a live Install button.
        var status = pulls.Status();
        var progress = status.Progress;
        var result = status.Last;

        return new
        {
            ok = true,
            running = status.Running,
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
                unreachable = result.Unreachable,
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

    private async Task<(int, string)> InstallPullAsync(string body)
    {
        var input = JsonSerializer.Deserialize<InstallInput>(body, Json) ?? new InstallInput();
        var outcome = await pulls.InstallAsync(input.Override ?? false).ConfigureAwait(false);

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

        // Same shape of guard as the pull path: one at a time, refused rather than
        // queued, so a held mouse button cannot fan out concurrent checks at one
        // destination file and one ETag cache entry.
        if (Interlocked.CompareExchange(ref _checking, 1, 0) != 0)
        {
            return (409, Serialize(new { ok = false, error = "A check is already running." }));
        }

        Task<UpdateResult> running;
        try
        {
            running = checkNow();
        }
        catch
        {
            Interlocked.Exchange(ref _checking, 0);
            throw;
        }

        try
        {
            var result = await running.WaitAsync(CheckTimeout).ConfigureAwait(false);

            Interlocked.Exchange(ref _checking, 0);

            return Ok(new
            {
                ok = !result.IsFailure,
                message = result.Message,
                entries = result.Entries,
                generatedAt = result.GeneratedAt,
            });
        }
        catch (TimeoutException)
        {
            Log.Warn($"a manual check did not finish within {CheckTimeout.TotalMinutes:0} minutes");

            // WaitAsync stops US waiting; it does not stop the check. Holding the
            // guard until the real task ends is the point of the guard - releasing it
            // here would let the next click start the second concurrent check this
            // exists to prevent. The continuation also observes a late fault.
            _ = running.ContinueWith(
                _ => Interlocked.Exchange(ref _checking, 0),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return (504, Serialize(new
            {
                ok = false,
                error = "The check did not finish in time. It is still running; try again shortly.",
            }));
        }
        catch
        {
            Interlocked.Exchange(ref _checking, 0);
            throw;
        }
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
