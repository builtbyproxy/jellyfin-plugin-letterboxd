# Design: Jellyfin 12 support

## Context

Three facts drive everything here:

1. **Runtime compatibility is one-directional.** Jellyfin 12 runs on .NET 10 and loads our net9.0 assembly; Jellyfin 10.11 runs on .NET 9 and cannot load a net10.0 assembly. The 12 SDK packages are net10.0-only (verified: NU1202 on restore against net9.0, 2026-07-06).
2. **The compiled SDK version is the effective floor.** Jellyfin assemblies carry full per-patch AssemblyVersions, so whatever we compile against is the real minimum server version (floor policy, issue #63). Compiling against 12.0.0 therefore floors us at 12.0.0 twice over: assembly version AND target framework.
3. **The manifest routes by `targetAbi`.** A Jellyfin server only offers plugin versions whose `targetAbi` is ≤ its own version. One manifest can carry a 10.11-floor line and a 12-floor line simultaneously; each server sees only what it can load.

## Decision 1: Single stream on the 10.11.9 floor until forced

We stay net9.0 / SDK 10.11.9 for as long as the ABI surface we use survives on 12. Field evidence (instances syncing on 12.0 RC) plus the CI prerelease leg give continuous confirmation. This serves the whole install base with one release stream and zero migration work for users.

Rejected alternative: adopt the 12 SDK at stable and dual-build every release (net9.0 + net10.0 artifacts from one tag). Doable but doubles release surface and CI, and buys nothing while the net9.0 build runs fine on 12. Reconsider only when a 12-only API is genuinely needed.

## Decision 2: Non-blocking CI prerelease leg, not a blocking one

A separate job in `ci.yml` (matrix or standalone) that:
- installs the .NET 10 SDK alongside 9 (`actions/setup-dotnet` with both versions),
- builds with `-p:TargetFramework=net10.0 -p:JellyfinSdkVersion=12.0.0-rc3` (csproj gains overridable properties; defaults unchanged),
- runs the unit test suite,
- is `continue-on-error: true` with a visible warning annotation on failure.

Non-blocking because an RC ABI change must not block shipping fixes to the stable fleet; its job is early warning. The Jellyfin package versions come from a pinned string in the workflow (bump manually per RC), not `*-rc*` floating, so a red leg always identifies the exact RC that broke us. Exact pinning also dodges a real hazard observed on NuGet (2026-07-26): alongside `12.0.0-rc3` there is a malformed `12.0.0-rcrc3` entry, which prerelease-floating version ranges could resolve to.

## Decision 3: The split, when it comes, is targetAbi-mediated

When we adopt the 12 SDK (new minor, e.g. 1.x → 1.(x+1) or 2.0):
- `targetAbi.txt` moves to 12.0.0.0 in the same PR (version-gate already enforces SDK == targetAbi and targetAbi change == minor bump).
- The manifest keeps prior 10.11-floor entries; 10.11 servers keep being offered the last 10.11-floor version, 12 servers get the new line. No manifest surgery needed; this is how `targetAbi` already works.
- The 10.11 line goes maintenance-only: critical fixes backported by cherry-pick onto a `maintenance/10.11` branch cut from the last 10.11-floor tag, released with the old floor. Time-box: maintenance ends when the fleet's Jellyfin version mix (telemetry, reviewed privately) shows a supermajority on 12, or 6 months after the split, whichever is sooner.

## Decision 4: Migration guidance is "keep it installed, or reinstall from the catalog, your data survives"

Jellyfin's 12 upgrade guidance says remove repository plugins before migrating, and the RC3 release notes escalate this for RC installers: disable **all external plugins** and reinstall from a 12-compatible repository, "or plugins may fail to load and cause unintended side effects". That instruction is aimed at the official plugin repositories (which carry a separate unstable channel); this plugin ships from its own catalog with no unstable channel, so our equivalent answer is unchanged and the smoke test (task 1.6) verifies a catalog reinstall works on a 12 server. For this plugin either path is safe and the README will say so: configuration lives in Jellyfin's plugin configuration store and sync history is stored next to the plugin DLL; a catalog reinstall restores both paths, and Jellyfin regenerates `meta.json` on catalog installs. The only unsupported path is manually copying a plugin directory without `meta.json`.

## Risks / watch items

- **Metadata behaviour changes in 12.** 12.0 reworks database and metadata internals; TMDb provider-ID behaviour could shift in ways that surface as `tmdb_lookup` telemetry errors rather than compile failures. The fleet watch (error categories segmented by `jellyfin_version`) is the tripwire; it is operated from the private ops repo and needs no plugin change.
- **RC packages disappearing at stable.** NuGet prerelease packages may be delisted after 12.0.0 ships; the CI leg then repoints at stable and effectively becomes the Phase-B verification.
- **.NET 10 SDK availability on runners.** `setup-dotnet` handles side-by-side installs; if the pinned SDK image lags, the leg fails visibly, which is acceptable for a non-blocking job.
- **Stable is close and Phase A isn't landed.** RC cadence (rc1 2026-06-21, rc2 2026-06-28, rc3 2026-07-22) points at stable within weeks. The early-warning value of the CI leg drops to zero once stable ships (it becomes plain Phase-B verification), so tasks 1.3–1.4 are only worth doing now, not later.
