using System;
using System.Collections.Generic;
using System.Linq;

namespace LetterboxdSync.Configuration;

/// <summary>
/// Centralised helpers for picking accounts off the configuration. Every sync path
/// goes through one of these so account-selection rules (enabled, primary, by user,
/// by username) can't drift apart across files.
/// </summary>
public static class AccountExtensions
{
    /// <summary>
    /// All enabled accounts for a Jellyfin user, in config order. Primary first
    /// (when one is marked) so callers that walk the list and stop at the first
    /// match get primary-wins semantics for free.
    /// </summary>
    public static IEnumerable<Account> GetEnabledAccountsForUser(this PluginConfiguration config, string userJellyfinId)
    {
        if (config?.Accounts == null || string.IsNullOrEmpty(userJellyfinId))
            return Array.Empty<Account>();

        var matches = config.Accounts
            .Where(a => a.Enabled && a.UserJellyfinId == userJellyfinId)
            .ToList();

        // Stable sort: primary first, otherwise preserve config order.
        return matches.OrderByDescending(a => a.IsPrimary);
    }

    /// <summary>
    /// The primary enabled account for a Jellyfin user, or the first enabled
    /// account if none is explicitly marked primary (defensive default).
    /// </summary>
    public static Account? GetPrimaryAccountForUser(this PluginConfiguration config, string userJellyfinId)
    {
        var enabled = config.GetEnabledAccountsForUser(userJellyfinId).ToList();
        if (enabled.Count == 0) return null;
        return enabled.FirstOrDefault(a => a.IsPrimary) ?? enabled[0];
    }

    /// <summary>
    /// Look up a specific enabled account by Jellyfin user + Letterboxd username.
    /// Used by manual API paths when the caller specifies which account to target.
    /// </summary>
    public static Account? FindAccount(this PluginConfiguration config, string userJellyfinId, string letterboxdUsername)
    {
        if (string.IsNullOrEmpty(letterboxdUsername)) return null;
        return config.GetEnabledAccountsForUser(userJellyfinId)
            .FirstOrDefault(a => string.Equals(a.LetterboxdUsername, letterboxdUsername, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// One-time normalisation: ensures every Jellyfin user with at least one enabled
    /// account has exactly one primary. Auto-promotes the first enabled account when
    /// none is marked, and demotes extras when more than one is marked. Idempotent;
    /// safe to call on every config save. Returns true when something was changed.
    /// </summary>
    public static bool NormalisePrimaryFlags(this PluginConfiguration config)
    {
        if (config?.Accounts == null || config.Accounts.Count == 0)
            return false;

        var changed = false;
        var byUser = config.Accounts
            .Where(a => a.Enabled && !string.IsNullOrEmpty(a.UserJellyfinId))
            .GroupBy(a => a.UserJellyfinId);

        foreach (var group in byUser)
        {
            var primaries = group.Where(a => a.IsPrimary).ToList();
            if (primaries.Count == 1) continue;

            if (primaries.Count == 0)
            {
                // Auto-promote: first enabled account becomes primary.
                group.First().IsPrimary = true;
                changed = true;
            }
            else
            {
                // More than one primary: keep the first, demote the rest.
                foreach (var extra in primaries.Skip(1))
                {
                    extra.IsPrimary = false;
                    changed = true;
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// Default playlist name for an account: "Letterboxd Watchlist ({username})".
    /// Falls back to the configured override if set.
    /// </summary>
    public static string GetPlaylistName(this Account account)
    {
        if (!string.IsNullOrWhiteSpace(account.PlaylistName))
            return account.PlaylistName!.Trim();
        return $"Letterboxd Watchlist ({account.LetterboxdUsername})";
    }
}
