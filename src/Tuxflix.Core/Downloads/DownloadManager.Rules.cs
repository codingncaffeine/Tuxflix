using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.Core.Downloads;

/// <summary>
/// Rules: "keep the next N unwatched episodes of this series (or season)". Episodes are fetched
/// ahead as others are watched, and the ones a rule fetched go a while after they are watched.
/// </summary>
/// <remarks>
/// Watched means watched on the server or here (a change not yet sent counts), and an episode a
/// rule has let go is never fetched again, whatever the server says. A series' rule leaves the
/// specials out; a season's takes what the season holds. Only what a rule fetched is ever removed
/// by one: a film or an episode downloaded by hand stays.
/// A rule is brought up to date when its server opens, when it is made, and when something is
/// watched while the server is open. The shape follows Plezy's sync rules (GPL-3.0,
/// github.com/edde746/plezy): queue what is short of the count, and count a watch the server has
/// not heard of yet.
/// </remarks>
public sealed partial class DownloadManager
{
    public IReadOnlyList<DownloadRule> Rules()
    {
        lock (_gate) return [.. _rules.Select(r => r.Copy())];
    }

    /// <summary>The rule for a series or a season, or null.</summary>
    public DownloadRule? RuleFor(string serverId, string ratingKey)
    {
        var id = DownloadRecord.Key(serverId, ratingKey);
        lock (_gate) return _rules.FirstOrDefault(r => r.Id == id)?.Copy();
    }

    /// <summary>Keeps the next <paramref name="keep"/> unwatched episodes of a series or a season; replaces a rule for the same one.</summary>
    public DownloadRule SetRule(MetadataItem showOrSeason, string serverId, string serverName, int keep)
    {
        ArgumentNullException.ThrowIfNull(showOrSeason);
        var rule = new DownloadRule
        {
            ServerId = serverId,
            ServerName = serverName,
            RatingKey = showOrSeason.RatingKey,
            Type = showOrSeason.Type == "season" ? "season" : "show",
            Title = showOrSeason.Type == "season" ? $"{showOrSeason.ParentTitle} · {showOrSeason.Title}" : showOrSeason.Title,
            Keep = Math.Clamp(keep, 1, 50),
        };
        lock (_gate)
        {
            _rules.RemoveAll(r => r.Id == rule.Id);
            _rules.Add(rule);
        }

        Log.Info($"Download rule: keep the next {rule.Keep} unwatched of {rule.Title}.");
        RulesChanged?.Invoke();
        SaveSoon();
        if (IsAttached(serverId)) _ = Task.Run(() => ApplyRulesAsync(serverId, CancellationToken.None));
        return rule.Copy();
    }

    /// <summary>Drops a rule; what it fetched stays, as downloads of their own.</summary>
    public void RemoveRule(string ruleId)
    {
        var changed = new List<DownloadRecord>();
        lock (_gate)
        {
            if (_rules.RemoveAll(r => r.Id == ruleId) == 0) return;
            foreach (var record in _records.Where(r => r.RuleId == ruleId))
            {
                record.RuleId = null;
                changed.Add(Snap(record));
            }
        }

        foreach (var record in changed) Changed?.Invoke(record);
        RulesChanged?.Invoke();
        SaveSoon();
    }

    /// <summary>
    /// Brings every rule of an open server up to date: queues the unwatched episodes each one
    /// wants, and removes the ones it fetched that were watched long enough ago.
    /// </summary>
    public async Task ApplyRulesAsync(string serverId, CancellationToken cancellation)
    {
        PlexServerClient? client;
        List<DownloadRule> rules;
        lock (_gate)
        {
            client = _servers.GetValueOrDefault(serverId);
            rules = [.. _rules.Where(r => r.ServerId == serverId).Select(r => r.Copy())];
        }

        if (client is null) return;
        foreach (var rule in rules)
        {
            IReadOnlyList<MetadataItem> episodes;
            try
            {
                episodes = rule.Type == "season"
                    ? await client.GetChildrenAsync(rule.RatingKey, cancellation).ConfigureAwait(false)
                    : [.. (await client.GetAllLeavesAsync(rule.RatingKey, cancellation).ConfigureAwait(false)).Where(e => e.ParentIndex is not 0)];
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException or System.Text.Json.JsonException)
            {
                Log.Info($"The rule for {rule.Title} waits: its episodes could not be listed ({ex.Message}).");
                continue;
            }

            Apply(rule, [.. episodes.Where(e => e.Type == "episode")]);
        }
    }

    private void Apply(DownloadRule rule, List<MetadataItem> episodes)
    {
        var now = Time.GetUtcNow();
        var wanted = new List<MetadataItem>();
        var expired = new List<string>();
        var marked = new List<DownloadRecord>();
        lock (_gate)
        {
            // The rule may have been dropped while its episodes were being listed.
            if (_rules.FirstOrDefault(r => r.Id == rule.Id) is not { } live) return;
            var watchedOnServer = episodes.Where(e => e.IsWatched).Select(e => e.RatingKey).ToHashSet(StringComparer.Ordinal);

            // Watched here counts before the server has heard: a kept copy marked so, a scrobble still to
            // send, or one this rule already let go.
            bool WatchedHere(MetadataItem episode) =>
                Get(DownloadRecord.Key(rule.ServerId, episode.RatingKey)) is { Watched: true }
                || live.Released.Contains(episode.RatingKey)
                || _pending.Any(p => p.Kind == PendingWrite.Scrobble && p.ServerId == rule.ServerId && p.RatingKey == episode.RatingKey);

            wanted = [.. episodes.Where(e => !e.IsWatched && !WatchedHere(e)).Take(live.Keep).Where(e => Get(DownloadRecord.Key(rule.ServerId, e.RatingKey)) is null)];

            foreach (var record in _records.Where(r => r.RuleId == rule.Id && (r.Watched || watchedOnServer.Contains(r.RatingKey))))
            {
                if (record.WatchedAt is null)
                {
                    record.WatchedAt = now.ToUnixTimeSeconds();
                    marked.Add(Snap(record));
                }

                if (now - DateTimeOffset.FromUnixTimeSeconds(record.WatchedAt.Value) >= RemoveWatchedAfter)
                {
                    expired.Add(record.Id);
                    live.Released.Add(record.RatingKey);
                    if (live.Released.Count > 500) live.Released.RemoveAt(0);
                }
            }
        }

        foreach (var record in marked) Changed?.Invoke(record);
        if (marked.Count > 0 || expired.Count > 0) SaveSoon();
        foreach (var episode in wanted) Enqueue(episode, rule.ServerId, rule.ServerName, rule.Id);
        foreach (var id in expired) Remove(id);
        if (wanted.Count > 0 || expired.Count > 0) Log.Info($"Rule for {rule.Title}: {wanted.Count} queued, {expired.Count} watched removed.");
    }
}
