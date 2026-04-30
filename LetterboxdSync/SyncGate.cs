using System.Threading;

namespace LetterboxdSync;

/// <summary>
/// Single global semaphore shared by every code path that scrapes Letterboxd: the diary
/// runner, the watchlist runner, and any future on-demand task. Sharing it ensures we
/// never run two scrapes concurrently against the same Cloudflare-protected origin and
/// that <see cref="SyncProgress"/> (also a singleton) is only ever driven by one caller.
/// </summary>
internal static class SyncGate
{
    public static readonly SemaphoreSlim Instance = new(1, 1);

    public static bool IsRunning => Instance.CurrentCount == 0;
}
