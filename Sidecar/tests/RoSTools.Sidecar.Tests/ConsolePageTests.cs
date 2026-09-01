using System.Text.RegularExpressions;
using RoSTools.Sidecar.Core.Web;
using Xunit;

namespace RoSTools.Sidecar.Tests;

/// <summary>
/// Guards on the console's own script.
/// <para>
/// These read the served page as text, which is not how anyone would rather test
/// JavaScript. The sidecar ships as a single-file exe with no web assets and no JS
/// engine anywhere in the solution, and the test project's dependencies are fixed -
/// so the choice is between pinning the shape of the script and leaving two
/// safety-relevant defects with no coverage at all. Each test below is written
/// against the specific defect, not against the file's general appearance: it fails
/// on the code as it was and passes on the code as it is.
/// </para>
/// </summary>
public class ConsolePageTests
{
    private static readonly string Page = ConsolePage.For(null);

    /// <summary>
    /// Line 460 guarded <c>state</c>; line 461 dereferenced it. When
    /// <c>/api/state</c> answers <c>{"ok":false}</c> - an
    /// <c>UnauthorizedAccessException</c> from the addon-folder probe is enough -
    /// <c>state</c> is null, and <c>renderResult</c> threw a TypeError on that second
    /// line: after the result card was unhidden and filled, and precisely on the one
    /// line that draws the shrink warning.
    /// <para>
    /// The absence assertion alone was worth nothing. Rename or drop
    /// <c>shrinkFloorPercent</c> and there are no lines to be offenders, so it goes
    /// green over a page that no longer reads the floor at all - which is not a guard,
    /// it is silence. The positive half below is what makes the negative half mean
    /// something.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_read_of_the_shrink_floor_guards_state_first()
    {
        var reads = Lines()
            .Where(line => line.Contains("shrinkFloorPercent", StringComparison.Ordinal))
            .Where(line => line.Contains("state.", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            reads.Count > 0,
            "the page no longer reads state.shrinkFloorPercent anywhere, so this test was " +
            "guarding nothing. The floor the page draws comes from the server (see " +
            "ConsoleApiGuardTests.The_state_payload_carries_the_shrink_floor_the_page_reads); " +
            "if the field moved, move this with it.");

        var offenders = reads
            .Where(line => !line.Contains("state &&", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "these lines read state.shrinkFloorPercent without guarding state:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));

        // And the fallback that keeps the warning drawable when /api/state failed.
        Assert.Contains(
            reads,
            line => line.Contains(@"typeof state.shrinkFloorPercent === ""number""", StringComparison.Ordinal));
    }

    /// <summary>
    /// Installing announces a roster to the whole guild, so a render that throws must
    /// disable Install rather than silently skip a safety warning. That is only
    /// possible if every call to <c>renderResult</c> goes through the wrapper that
    /// catches - one call site, not two loose ones.
    /// </summary>
    [Fact]
    public void RenderResult_is_only_ever_called_through_the_guarded_wrapper()
    {
        var calls = Regex.Matches(Page, @"(?<!function )\brenderResult\(\)")
            .Select(m => m.Index)
            .ToList();

        Assert.Single(calls);

        var wrapper = Between(Page, "function showResult()", "function renderResult()");
        Assert.Contains("renderResult();", wrapper, StringComparison.Ordinal);
        Assert.Contains("catch", wrapper, StringComparison.Ordinal);
        Assert.Contains(@"$(""installBtn"").disabled = true;", wrapper, StringComparison.Ordinal);
    }

    /// <summary>
    /// A 401 or a 500 mid-pull returned early without <c>clearInterval</c>, so a
    /// 700 ms poll ran for the life of the page with the Pull button disabled and
    /// nothing on screen explaining why.
    /// </summary>
    [Fact]
    public void A_failed_poll_stops_the_poller_and_says_something()
    {
        var body = Between(Page, "function pollPull()", "var pr = p.progress;");

        Assert.Contains("if (!p.ok)", body, StringComparison.Ordinal);
        Assert.Contains("stopPolling();", body, StringComparison.Ordinal);
        Assert.Contains(@"say($(""pullMsg""), ""bad""", body, StringComparison.Ordinal);
    }

    /// <summary>The one place that re-enables the controls, so no early return can
    /// leave them stuck.</summary>
    [Fact]
    public void Stopping_the_poller_always_restores_the_pull_controls()
    {
        var body = Between(Page, "function stopPolling()", "function pollPull()");

        Assert.Contains("clearInterval(poller); poller = null;", body, StringComparison.Ordinal);
        Assert.Contains(@"$(""pullBtn"").disabled = false;", body, StringComparison.Ordinal);
        Assert.Contains(@"$(""cancelBtn"").hidden = true;", body, StringComparison.Ordinal);
        Assert.Contains(@"$(""progressBar"").hidden = true;", body, StringComparison.Ordinal);
    }

    /// <summary>A failed state load must not leave a previous state object behind for
    /// the guards above to succeed against.</summary>
    [Fact]
    public void A_failed_state_load_clears_state()
    {
        var body = Between(Page, "function loadState()", "function renderState()");

        Assert.Contains("state = null;", body, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    private static IEnumerable<string> Lines() =>
        Page.Split('\n', StringSplitOptions.None);

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"the page no longer contains '{start}'");

        var to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to > from, $"the page no longer contains '{end}' after '{start}'");

        return text[from..to];
    }
}
