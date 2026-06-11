namespace LetterboxdSync;

/// <summary>
/// Telemetry backend endpoint and the publishable ingest key.
/// The backend is a Cloudflare Worker + D1 (see worker/ in the repo); the database is
/// reachable only through the Worker. The key is publishable by design (like a
/// Plausible domain): it stops drive-by scanner POSTs, and its extraction from plugin
/// source is bounded to junk rows — never reads, never privacy.
/// Mutable (not const) so tests can point the sender at a fake.
/// </summary>
internal static class TelemetryConstants
{
    /// <summary>Current payload schema version; the ingest Worker rejects unknown versions.</summary>
    public const int SchemaVersion = 1;

    public static string IngestUrl = "https://lbsync-telemetry.lachlanbyoung.workers.dev";

    public static string IngestKey = "81eb48923c912d49ef6f1cd331c0cc8ced6abd46cf1ae96d";
}
