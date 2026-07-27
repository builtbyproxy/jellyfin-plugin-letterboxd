# Add Jellyfin 12 support

## Why

Jellyfin is skipping version 11: 10.11.x is the last release line with the "10." prefix and the next major is 12.0, already at RC3 (rc1 2026-06-21, rc2 2026-06-28, rc3 2026-07-22), with stable expected soon. The RC3 notes confirm the rationale: the `10.` prefix is dropped because no hard API break is planned, reinforcing that our risk is patch-level ABI holes, not a wholesale rewrite. Early adopters are already running the plugin on 12.0 RCs in the field, and the current net9.0 build (compiled against SDK 10.11.9) loads and syncs there because a newer .NET runtime loads older assemblies.

That happy accident hides a hard cliff, confirmed by a compile probe on 2026-07-06: the 12.0 SDK packages (`Jellyfin.Controller`/`Jellyfin.Model` 12.0.0-rc2) target **net10.0** while the plugin targets net9.0, so the packages do not restore (NU1202). The moment we compile against the 12 SDK we must move to net10.0, and a net10.0 assembly will not load on Jellyfin 10.11's .NET 9 host. Unlike previous SDK bumps, adopting the 12 SDK is not "raise the floor a patch": it is a one-way split of the release stream. We also know from the 10.11.9 incident (`IUserManager.Users` removed in a patch release) that ABI holes can appear without warning, so "it runs on the RC today" is evidence, not a guarantee.

Jellyfin's upgrade guidance tells users to remove repository plugins before migrating to 12 and warns the database migration cannot be rolled back; the RC3 release notes go further, telling RC installers to disable **all external plugins** and reinstall from a 12-compatible repository "or plugins may fail to load and cause unintended side effects". So users will ask what to do with this plugin. We need the answer written down before stable ships, not improvised the day dependabot opens the SDK-12 PR.

## What Changes

- New `jellyfin-compatibility` capability spec codifying the compatibility contract: explicit floor policy, staged 12.0 support (RC watch → stable verification → declared support), the manifest `targetAbi` mechanism as the dual-track split, and user migration guidance.
- CI gains a non-blocking prerelease leg that builds the plugin and runs the test suite against the latest 12.0 RC SDK on net10.0, so ABI drift in the 12 line is visible weeks before stable.
- README Requirements section documents the compatibility statement and what a Jellyfin 12 migration means for this plugin (config and sync history survive a remove/reinstall).

### New Capabilities
- `jellyfin-compatibility`: floor policy, prerelease verification, major-version support declaration, stream-split mechanics, migration guidance.

## Impact

- **Now**: docs + one CI workflow edit. No runtime code changes, no floor change; releases keep `targetAbi` 10.11.9.0 and serve both 10.11 and 12 servers.
- **At 12.0 stable**: verification work only, unless the suite fails against 12.
- **If/when we adopt the 12 SDK**: mandatory split: a net10.0 line with `targetAbi` 12.0.0.0 alongside a maintenance 10.11 line. Deferred until 12-only APIs are needed or an ABI break forces it; scoped as tasks here but implemented as its own change.
- **Non-goals**: no multi-targeted dual builds out of one release today; no floor raise; no changes to telemetry payloads (the existing `jellyfin_version` dimension already covers the watch duty).
