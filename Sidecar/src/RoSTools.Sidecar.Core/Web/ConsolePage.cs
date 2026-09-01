namespace RoSTools.Sidecar.Core.Web;

/// <summary>
/// The console's single page, served inline.
/// <para>
/// Deliberately one self-contained string with no external stylesheet, font or
/// script. The sidecar ships as a single-file exe with no web assets beside it, and
/// the server's CSP blocks outbound requests from the page anyway - so anything
/// this page cannot do on its own, it must not need.
/// </para>
/// </summary>
internal static class ConsolePage
{
    private const string Template = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>RoS-Tools Sidecar</title>
<style>
  :root {
    --bg: #14161a; --panel: #1c1f26; --panel-2: #232733; --line: #2f3542;
    --text: #e6e8ee; --dim: #939aab; --accent: #c8a25a; --accent-dim: #8d7038;
    --ok: #5fb87a; --warn: #d8a13a; --bad: #d4635f; --link: #7aa7d8;
  }
  * { box-sizing: border-box; }
  body {
    margin: 0; background: var(--bg); color: var(--text);
    font: 14px/1.5 "Segoe UI", system-ui, -apple-system, sans-serif;
  }
  header {
    background: linear-gradient(180deg, #1e222b, #171a21);
    border-bottom: 1px solid var(--line); padding: 14px 20px;
    display: flex; align-items: baseline; gap: 14px; flex-wrap: wrap;
    position: sticky; top: 0; z-index: 5;
  }
  header h1 { margin: 0; font-size: 16px; font-weight: 600; letter-spacing: .3px; }
  header .guild { color: var(--accent); font-weight: 600; }
  header .spacer { flex: 1; }
  main { max-width: 1080px; margin: 0 auto; padding: 20px; display: grid; gap: 18px; }
  section {
    background: var(--panel); border: 1px solid var(--line); border-radius: 8px; padding: 16px 18px;
  }
  section h2 {
    margin: 0 0 4px; font-size: 13px; text-transform: uppercase;
    letter-spacing: .8px; color: var(--accent);
  }
  section p.hint { margin: 0 0 14px; color: var(--dim); font-size: 12.5px; }
  .row { display: flex; gap: 12px; flex-wrap: wrap; align-items: flex-end; }
  label { display: block; font-size: 12px; color: var(--dim); margin-bottom: 4px; }
  input[type=text], input[type=password], input[type=number], select {
    background: #0f1116; color: var(--text); border: 1px solid var(--line);
    border-radius: 5px; padding: 7px 9px; font: inherit; font-size: 13px; min-width: 0;
  }
  input:focus, select:focus { outline: 2px solid var(--accent-dim); outline-offset: -1px; }
  .field { display: flex; flex-direction: column; }
  .grow { flex: 1 1 200px; }
  button {
    background: var(--accent); color: #1a1408; border: 0; border-radius: 5px;
    padding: 8px 16px; font: inherit; font-weight: 600; cursor: pointer;
  }
  button:hover:not(:disabled) { filter: brightness(1.1); }
  button:disabled { opacity: .45; cursor: default; }
  button.ghost { background: transparent; color: var(--dim); border: 1px solid var(--line); }
  button.danger { background: transparent; color: var(--bad); border: 1px solid #4a2c2b; }
  .stats { display: flex; gap: 22px; flex-wrap: wrap; margin: 12px 0 0; }
  .stat .n { font-size: 22px; font-weight: 600; font-variant-numeric: tabular-nums; }
  .stat .l { font-size: 11.5px; color: var(--dim); text-transform: uppercase; letter-spacing: .5px; }
  .pill {
    display: inline-block; padding: 2px 9px; border-radius: 999px;
    font-size: 11.5px; font-weight: 600; border: 1px solid;
  }
  .pill.ok { color: var(--ok); border-color: #2c4a37; background: #17251c; }
  .pill.warn { color: var(--warn); border-color: #4a3d1e; background: #241f11; }
  .pill.bad { color: var(--bad); border-color: #4a2c2b; background: #241616; }
  .msg { margin-top: 12px; padding: 10px 12px; border-radius: 6px; font-size: 13px; border: 1px solid; }
  .msg.ok { color: var(--ok); border-color: #2c4a37; background: #16211a; }
  .msg.bad { color: var(--bad); border-color: #4a2c2b; background: #221515; }
  .msg.warn { color: var(--warn); border-color: #4a3d1e; background: #221e11; }
  .bar { height: 6px; background: #0f1116; border-radius: 3px; overflow: hidden; margin-top: 10px; }
  .bar > i { display: block; height: 100%; background: var(--accent); width: 0; transition: width .25s; }
  table { width: 100%; border-collapse: collapse; font-size: 13px; }
  th, td { text-align: left; padding: 6px 10px; border-bottom: 1px solid #262b36; }
  th {
    color: var(--dim); font-size: 11.5px; text-transform: uppercase; letter-spacing: .5px;
    cursor: pointer; user-select: none; position: sticky; top: 0; background: var(--panel-2);
  }
  td.num, th.num { text-align: right; font-variant-numeric: tabular-nums; }
  .scroll { max-height: 460px; overflow: auto; border: 1px solid var(--line); border-radius: 6px; }
  .tabs { display: flex; gap: 6px; margin-bottom: 12px; flex-wrap: wrap; }
  .tabs button {
    background: var(--panel-2); color: var(--dim); border: 1px solid var(--line);
    font-weight: 500; padding: 6px 13px; font-size: 12.5px;
  }
  .tabs button[aria-selected=true] { background: var(--accent); color: #1a1408; border-color: var(--accent); }
  .delta { display: grid; gap: 6px; margin-top: 12px; }
  .delta div { font-size: 12.5px; color: var(--dim); }
  .delta b { color: var(--text); font-variant-numeric: tabular-nums; }
  .up { color: var(--ok); } .down { color: var(--bad); }
  code { background: #0f1116; padding: 1px 5px; border-radius: 3px; font-size: 12px; color: var(--dim); }
  .checkline { display: flex; align-items: center; gap: 7px; font-size: 13px; color: var(--dim); }
  .muted { color: var(--dim); }
  .warnbox {
    border-left: 3px solid var(--warn); background: #1e1b12; padding: 10px 14px;
    border-radius: 0 6px 6px 0; font-size: 12.5px; color: #d9cdb0; margin-top: 12px;
  }
</style>
</head>
<body>
<header>
  <h1>RoS-Tools Sidecar</h1>
  <span id="guildLabel" class="guild"></span>
  <span class="spacer"></span>
  <span id="installedPill"></span>
</header>

<main>
  <section id="statusCard">
    <h2>Installed roster</h2>
    <p class="hint" id="addonPath">Looking for your addon...</p>
    <div class="stats" id="installedStats"></div>
    <div id="installedWarning"></div>
  </section>

  <section>
    <h2>Blizzard credentials</h2>
    <p class="hint">
      From an application at <code>develop.battle.net/access/clients</code>. The client
      credentials flow reads public profile data only - no redirect URI, no account access.
      The secret is encrypted with Windows DPAPI under your user account and is never sent
      back to this page.
    </p>
    <div class="row">
      <div class="field grow">
        <label for="clientId">Client ID</label>
        <input type="text" id="clientId" autocomplete="off" spellcheck="false" placeholder="stored">
      </div>
      <div class="field grow">
        <label for="clientSecret">Client secret</label>
        <input type="password" id="clientSecret" autocomplete="off" placeholder="stored">
      </div>
      <div class="field">
        <label for="credRegion">Region</label>
        <select id="credRegion"></select>
      </div>
      <button id="saveCreds">Save</button>
      <button id="clearCreds" class="danger">Clear</button>
    </div>
    <div id="credMsg"></div>
  </section>

  <section>
    <h2>Pull from Blizzard</h2>
    <p class="hint">
      One roster call plus one per character - about 180 requests. Nothing is written to
      your addon until you review the result and choose to install it.
    </p>
    <div class="row">
      <div class="field grow">
        <label for="realm">Realm</label>
        <input type="text" id="realm" placeholder="khadgar" spellcheck="false">
      </div>
      <div class="field grow">
        <label for="guild">Guild</label>
        <input type="text" id="guild" placeholder="Riddle of Steel" spellcheck="false">
      </div>
      <div class="field">
        <label for="minLevel">Min level</label>
        <input type="number" id="minLevel" value="1" min="1" max="80" style="width:90px">
      </div>
      <button id="pullBtn">Pull roster</button>
      <button id="cancelBtn" class="ghost" hidden>Cancel</button>
    </div>
    <div class="bar" id="progressBar" hidden><i></i></div>
    <div id="pullMsg"></div>
  </section>

  <section id="resultCard" hidden>
    <h2>Pulled roster</h2>
    <p class="hint" id="pullSummary"></p>
    <div class="stats" id="pullStats"></div>
    <div class="delta" id="deltaBox"></div>
    <div id="shrinkWarning"></div>
    <div class="row" style="margin-top:16px">
      <button id="installBtn">Install to addon</button>
      <label class="checkline">
        <input type="checkbox" id="overrideShrink">
        Install even though the roster shrank
      </label>
    </div>
    <div class="warnbox">
      Installing announces this roster to your guild. Other members running RoS-Tools will
      adopt it from you over addon comms, because it carries the newest export time in the
      guild. A partial pull replaces good data for everyone, not just you.
    </div>
    <div id="installMsg"></div>
  </section>

  <section>
    <h2>Characters</h2>
    <div class="tabs">
      <button id="tabPulled" aria-selected="false">Pulled</button>
      <button id="tabInstalled" aria-selected="true">Installed</button>
      <div class="field" style="margin-left:auto">
        <input type="text" id="filter" placeholder="Filter by name..." spellcheck="false">
      </div>
    </div>
    <div class="scroll">
      <table>
        <thead><tr>
          <th data-sort="key">Character</th>
          <th data-sort="realm">Realm</th>
          <th data-sort="ilvl" class="num">Item level</th>
        </tr></thead>
        <tbody id="rows"></tbody>
      </table>
    </div>
    <p class="hint" id="tableFoot" style="margin-top:10px"></p>
  </section>
</main>

<script>
(function () {
  "use strict";

  // The session token is delivered in the page body, not the URL, so it never
  // lands anywhere a URL outlives the session: browser history, shell history, a
  // saved command line. What the URL carried was a single-use bootstrap token,
  // already burned by the time this script runs. That bounds the exposure; it does
  // not remove it. Another process running as this user could have raced the
  // browser to that link, and the sidecar log is where a redemption shows up.
  //
  // sessionStorage, not a cookie: cookies ignore the port, so any other localhost
  // server could read this one. sessionStorage is scoped to the full origin, and
  // it survives a refresh, which is why the address bar is cleaned up below.
  var served = "__SESSION_TOKEN__";
  var token = null;

  try {
    if (served) {
      token = served;
      sessionStorage.setItem("rosToken", token);
    } else {
      token = sessionStorage.getItem("rosToken");
    }
    if (location.search) { history.replaceState(null, "", location.pathname); }
  } catch (e) {
    // Private mode, or storage blocked. The token still works for this page view;
    // only a refresh will need reopening from the tray.
    token = served || null;
  }

  var $ = function (id) { return document.getElementById(id); };
  var state = null, pulled = null, tab = "installed", sortKey = "ilvl", sortAsc = false;

  function esc(s) {
    return String(s).replace(/[&<>"']/g, function (c) {
      return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
    });
  }

  function api(path, method, body) {
    return fetch(path, {
      method: method || "GET",
      headers: { "X-RoS-Token": token || "", "Content-Type": "application/json" },
      body: body ? JSON.stringify(body) : undefined
    }).then(function (r) {
      return r.json().catch(function () { return { ok: false, error: "Unreadable response." }; })
        .then(function (j) { j.__status = r.status; return j; });
    });
  }

  function say(el, kind, text) {
    if (!text) { el.innerHTML = ""; return; }
    el.innerHTML = '<div class="msg ' + kind + '">' + esc(text) + "</div>";
  }

  function stat(n, l) {
    return '<div class="stat"><div class="n">' + esc(n) + '</div><div class="l">' + esc(l) + "</div></div>";
  }

  // ---- state ------------------------------------------------------------
  function loadState() {
    return api("/api/state").then(function (s) {
      if (!s.ok) {
        // Explicitly null, not "whatever was there before": everything downstream
        // guards on it, and a half-stale state object is worse than none.
        state = null;
        say($("credMsg"), "bad", s.error || "Could not read state.");
        return;
      }
      state = s;
      renderState();
    });
  }

  function renderState() {
    var g = state.guild;
    $("guildLabel").textContent = g ? (g.guild + " - " + g.realm + " (" + g.region.toUpperCase() + ")") : "";

    $("addonPath").textContent = state.addOn.found
      ? state.addOn.resolved
      : "No RoS-Tools addon found. Set the folder in the tray Settings window.";

    var sel = $("credRegion");
    if (!sel.options.length) {
      state.regions.forEach(function (r) {
        var o = document.createElement("option");
        o.value = r; o.textContent = r.toUpperCase();
        sel.appendChild(o);
      });
    }
    sel.value = state.credentials.region;

    if (state.credentials.fromEnvironment) {
      say($("credMsg"), "ok",
        "Using BLIZZARD_CLIENT_ID and BLIZZARD_CLIENT_SECRET from the environment. " +
        "Anything saved here is ignored while those are set.");
    } else if (state.credentials.present) {
      say($("credMsg"), "ok", "Credentials stored for client " + state.credentials.clientId + ".");
    } else if (!state.credentials.canStore) {
      say($("credMsg"), "warn", "This build cannot encrypt secrets at rest; use the environment variables.");
    } else {
      say($("credMsg"), "", "");
    }

    if (!$("realm").value && g) { $("realm").value = g.realm; }
    if (!$("guild").value && g) { $("guild").value = g.guild; }

    var inst = state.installed, pill = $("installedPill"), stats = $("installedStats");

    if (!inst) {
      pill.innerHTML = '<span class="pill bad">no addon</span>';
      stats.innerHTML = ""; $("installedWarning").innerHTML = "";
    } else if (!inst.ok) {
      pill.innerHTML = '<span class="pill bad">unreadable</span>';
      stats.innerHTML = "";
      say($("installedWarning"), "bad", inst.reason || "The installed roster could not be validated.");
    } else {
      var age = inst.ageDays == null ? "?" : inst.ageDays.toFixed(1);
      var cls = inst.ageDays == null ? "warn" : inst.ageDays > 14 ? "warn" : "ok";
      pill.innerHTML = '<span class="pill ' + cls + '">' + inst.entries + " characters - " + age + "d old</span>";
      stats.innerHTML =
        stat(inst.entries, "characters") +
        stat(age + " d", "since export") +
        stat(inst.exportBytes + " B", "share size / 40000") +
        stat(inst.generatedAt || "-", "exported (UTC)");
      $("installedWarning").innerHTML = inst.warning
        ? '<div class="msg warn">' + esc(inst.warning) + "</div>" : "";
    }

    if (tab === "installed") { renderRows(); }
  }

  // ---- credentials ------------------------------------------------------
  $("saveCreds").onclick = function () {
    var id = $("clientId").value.trim(), secret = $("clientSecret").value.trim();
    if (!id || !secret) { say($("credMsg"), "bad", "Enter both the client ID and the secret."); return; }
    api("/api/credentials", "POST", { clientId: id, clientSecret: secret, region: $("credRegion").value })
      .then(function (r) {
        say($("credMsg"), r.ok ? "ok" : "bad", r.message || r.error);
        if (r.ok) { $("clientId").value = ""; $("clientSecret").value = ""; loadState(); }
      });
  };

  $("clearCreds").onclick = function () {
    api("/api/credentials", "DELETE").then(function (r) {
      say($("credMsg"), r.ok ? "ok" : "bad", r.message || r.error);
      loadState();
    });
  };

  // ---- pull -------------------------------------------------------------
  var poller = null;

  $("pullBtn").onclick = function () {
    say($("pullMsg"), "", "");
    api("/api/pull", "POST", {
      region: $("credRegion").value,
      realm: $("realm").value.trim(),
      guild: $("guild").value.trim(),
      minLevel: parseInt($("minLevel").value, 10) || 1
    }).then(function (r) {
      if (!r.ok) { say($("pullMsg"), "bad", r.error); return; }
      $("pullBtn").disabled = true;
      $("cancelBtn").hidden = false;
      $("progressBar").hidden = false;
      startPolling();
    });
  };

  $("cancelBtn").onclick = function () { api("/api/pull", "DELETE"); };

  function startPolling() {
    stopPolling();
    poller = setInterval(pollPull, 700);
    pollPull();
  }

  // One place that puts the pull controls back, so no early return can leave the
  // page polling forever with the Pull button disabled.
  function stopPolling() {
    if (poller) { clearInterval(poller); poller = null; }
    $("pullBtn").disabled = false;
    $("cancelBtn").hidden = true;
    $("progressBar").hidden = true;
  }

  function pollPull() {
    api("/api/pull").then(function (p) {
      // A 401 or a 500 mid-pull used to return here without clearing the interval,
      // leaving a 700 ms poll running for the life of the page against an endpoint
      // that was never going to answer, with the Pull button disabled the whole
      // time and nothing on screen saying why.
      if (!p.ok) {
        stopPolling();
        say($("pullMsg"), "bad",
          p.error || "Lost contact with the sidecar. Reopen the console from the tray menu.");
        return;
      }

      var pr = p.progress;
      var pct = pr.total > 0 ? Math.round((pr.done / pr.total) * 100) : (p.running ? 8 : 0);
      $("progressBar").querySelector("i").style.width = pct + "%";

      if (p.running) {
        say($("pullMsg"), "ok", pr.message);
        return;
      }

      stopPolling();

      if (!p.result) { return; }

      if (!p.result.ok) {
        say($("pullMsg"), "bad", p.result.error || "The pull failed.");
        $("resultCard").hidden = true;
        return;
      }

      say($("pullMsg"), "ok", pr.message);
      pulled = p.result;
      showResult();
      tab = "pulled";
      selectTab();
    });
  }

  // The only call site for renderResult. Installing announces a roster to the whole
  // guild, so a render that throws must not leave a live Install button beside a
  // half-drawn card - which is exactly what happened when the shrink warning was
  // the line that threw: the summary and stats were already on screen, the warning
  // never appeared, and Install stayed enabled.
  function showResult() {
    try {
      renderResult();
      $("installBtn").disabled = false;
    } catch (e) {
      $("installBtn").disabled = true;
      say($("installMsg"), "bad",
        "This page could not finish checking the pulled roster, so installing is " +
        "disabled. Reopen the console from the tray menu and pull again.");
    }
  }

  function renderResult() {
    var r = pulled;
    $("resultCard").hidden = false;

    var id = r.identity;
    $("pullSummary").textContent =
      "Pulled " + r.entries.length + " of " + r.rosterSize + " roster members for " +
      id.guild + " - " + id.realm + " (" + id.region.toUpperCase() + ") at " +
      new Date(r.atUtc).toLocaleString() + ".";

    // "no profile" and "unreachable" are different facts and the review screen has
    // to keep them apart: the first is an alt that never logged in, the second is
    // Blizzard failing after five attempts, and only the second means "come back
    // later". The server refuses an install over the second on its own.
    $("pullStats").innerHTML =
      stat(r.entries.length, "with item level") +
      stat(r.noProfile, "no profile") +
      stat(r.unreachable || 0, "unreachable") +
      stat(r.droppedKeys.length, "unusable names") +
      stat(r.exportBytes + " B", "share size / 40000");

    var d = r.delta, box = $("deltaBox");
    var up = d.changed.filter(function (c) { return c.to > c.from; }).length;
    var down = d.changed.length - up;

    box.innerHTML =
      "<div>Against what is installed: <b>" + d.added.length + "</b> new, " +
      '<b class="' + (d.removed.length ? "down" : "") + '">' + d.removed.length + "</b> gone, " +
      '<b class="up">' + up + "</b> up, " + '<b class="down">' + down + "</b> down.</div>" +
      (d.removed.length
        ? '<div class="muted">Gone: ' + esc(d.removed.slice(0, 12).join(", ")) +
          (d.removed.length > 12 ? " and " + (d.removed.length - 12) + " more" : "") + "</div>"
        : "") +
      (r.droppedKeys.length
        ? '<div class="muted">Names too long or unusable for sharing, left out: ' +
          esc(r.droppedKeys.slice(0, 8).join(", ")) + "</div>"
        : "");

    // Every read of state on these lines is guarded. The previous version guarded
    // it on one line and dereferenced it on the next, so a failed /api/state - an
    // UnauthorizedAccessException from the addon-folder probe is enough - threw a
    // TypeError here, after the card was already unhidden and filled, and the one
    // thing it skipped was the shrink warning.
    var installedCount = state && state.installed && state.installed.ok ? state.installed.entries : 0;
    var floorPct = state && typeof state.shrinkFloorPercent === "number" ? state.shrinkFloorPercent : 80;
    var floor = installedCount * (floorPct / 100);

    if (installedCount && r.entries.length < floor) {
      say($("shrinkWarning"), "bad",
        "This pull has " + r.entries.length + " characters against " + installedCount +
        " installed. That is below the " + floorPct +
        "% floor, so installing it needs the override ticked. Pulling again usually fixes it - " +
        "a batch of throttled or private profiles looks exactly like this.");
    } else if (!state || !state.installed || !state.installed.ok) {
      // No baseline on this page means no comparison on this page. Say so rather
      // than showing an empty space that reads as "checked, and fine".
      say($("shrinkWarning"), "warn",
        "The installed roster could not be read, so this page cannot compare the pull " +
        "against it. The sidecar still checks before it writes anything.");
    } else {
      say($("shrinkWarning"), "", "");
    }

    if (r.warning) { say($("installMsg"), "warn", r.warning); }
  }

  $("installBtn").onclick = function () {
    $("installBtn").disabled = true;
    api("/api/install", "POST", { override: $("overrideShrink").checked })
      .then(function (r) {
        $("installBtn").disabled = false;
        say($("installMsg"), r.ok ? "ok" : "bad", r.message);
        if (r.ok) { loadState(); }
      });
  };

  // ---- table ------------------------------------------------------------
  function selectTab() {
    $("tabPulled").setAttribute("aria-selected", tab === "pulled");
    $("tabInstalled").setAttribute("aria-selected", tab === "installed");
    renderRows();
  }

  $("tabPulled").onclick = function () { tab = "pulled"; selectTab(); };
  $("tabInstalled").onclick = function () { tab = "installed"; selectTab(); };
  $("filter").oninput = renderRows;

  Array.prototype.forEach.call(document.querySelectorAll("th[data-sort]"), function (th) {
    th.onclick = function () {
      var k = th.getAttribute("data-sort");
      if (sortKey === k) { sortAsc = !sortAsc; } else { sortKey = k; sortAsc = k !== "ilvl"; }
      renderRows();
    };
  });

  function currentRows() {
    if (tab === "pulled") { return pulled ? pulled.entries : []; }
    return state && state.installed && state.installed.characters ? state.installed.characters : [];
  }

  // Keys are "Name-realm-slug" and a realm slug can itself contain hyphens, so
  // split on the FIRST hyphen only - "Aep-baelgun" and "Bob-argent-dawn" both
  // have to come apart correctly.
  function split(key) {
    var i = key.indexOf("-");
    return i < 0 ? { name: key, realm: "" } : { name: key.slice(0, i), realm: key.slice(i + 1) };
  }

  function renderRows() {
    var q = $("filter").value.trim().toLowerCase();
    var rows = currentRows().map(function (e) {
      var s = split(e.key);
      return { key: e.key, name: s.name, realm: s.realm, ilvl: e.ilvl };
    });

    if (q) {
      rows = rows.filter(function (r) { return r.key.toLowerCase().indexOf(q) >= 0; });
    }

    rows.sort(function (a, b) {
      var x = sortKey === "ilvl" ? a.ilvl : (sortKey === "realm" ? a.realm : a.name).toLowerCase();
      var y = sortKey === "ilvl" ? b.ilvl : (sortKey === "realm" ? b.realm : b.name).toLowerCase();
      return (x < y ? -1 : x > y ? 1 : 0) * (sortAsc ? 1 : -1);
    });

    $("rows").innerHTML = rows.map(function (r) {
      return "<tr><td>" + esc(r.name) + "</td><td class=muted>" + esc(r.realm) +
        '</td><td class="num">' + r.ilvl + "</td></tr>";
    }).join("");

    var total = currentRows().length;
    $("tableFoot").textContent = rows.length === total
      ? total + " characters (" + tab + ")"
      : rows.length + " of " + total + " characters (" + tab + ")";
  }

  // ---- go ---------------------------------------------------------------
  if (!token) {
    document.body.innerHTML =
      '<main><section><h2>Session expired</h2><p class="hint">' +
      "Reopen the console from the sidecar's tray menu.</p></section></main>";
    return;
  }

  loadState().then(function () {
    // A pull started before a reload is still running server-side; pick it up.
    api("/api/pull").then(function (p) {
      if (p.ok && p.running) {
        $("pullBtn").disabled = true;
        $("cancelBtn").hidden = false;
        $("progressBar").hidden = false;
        startPolling();
      } else if (p.ok && p.result && p.result.ok) {
        pulled = p.result;
        showResult();
      }
    });
  });
})();
</script>
</body>
</html>
""";

    /// <summary>
    /// The page, with the session token baked in when the caller has earned one.
    /// <para>
    /// <paramref name="token"/> is a base64url string of this server's own making,
    /// so it cannot break out of the JavaScript string literal it lands in. That is
    /// asserted rather than assumed, because a future change to how tokens are
    /// generated must not silently turn this into a script-injection point.
    /// </para>
    /// </summary>
    public static string For(string? token)
    {
        if (token is not null && !IsBase64Url(token))
        {
            throw new ArgumentException("Session token is not base64url.", nameof(token));
        }

        return Template.Replace("__SESSION_TOKEN__", token ?? string.Empty, StringComparison.Ordinal);
    }

    private static bool IsBase64Url(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
            {
                return false;
            }
        }

        return value.Length > 0;
    }
}
