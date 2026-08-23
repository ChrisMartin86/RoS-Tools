namespace RoSTools.Sidecar.Core;

public sealed class SidecarSettings
{
    /// <summary>
    /// The <c>guild-data</c> branch is written daily by the "Guild data" workflow
    /// and holds nothing but the payload, so this URL is stable.
    /// <para>
    /// The sidecar never calls the Blizzard API directly. Doing so would put the
    /// client secret on every guildmate's machine and multiply a ~180-call export
    /// by the number of installs. CI is the single API consumer.
    /// </para>
    /// </summary>
    public const string DefaultDataUrl =
        "https://raw.githubusercontent.com/ChrisMartin86/RoS-Tools/guild-data/GuildData.lua";

    public const int MinimumPollHours = 1;
    public const int MaximumPollHours = 48;
    public const int DefaultPollHours = 6;

    /// <summary>Explicit addon folder. Null means auto-detect on every check.</summary>
    public string? AddOnPath { get; set; }

    public string DataUrl { get; set; } = DefaultDataUrl;

    public int PollIntervalHours { get; set; } = DefaultPollHours;

    public bool StartWithWindows { get; set; }

    // --- conditional-request cache -------------------------------------------

    public string? ETag { get; set; }

    public string? LastModified { get; set; }

    // --- last-run reporting, shown in the tray tooltip and settings window ----

    public DateTimeOffset? LastCheckUtc { get; set; }

    public DateTimeOffset? LastUpdateUtc { get; set; }

    public int LastEntryCount { get; set; }

    public string? LastGeneratedAt { get; set; }

    public string? LastError { get; set; }

    public bool FirstRunCompleted { get; set; }

    public int EffectivePollHours =>
        Math.Clamp(PollIntervalHours, MinimumPollHours, MaximumPollHours);

    public SidecarSettings Clone() => (SidecarSettings)MemberwiseClone();
}
