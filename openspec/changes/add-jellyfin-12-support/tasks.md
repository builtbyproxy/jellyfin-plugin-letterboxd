# Tasks: Jellyfin 12 support

## 1. Phase A: RC window (now)

- [x] 1.1 Compile probe against `12.0.0-rc2`: confirmed net10.0-only packages (NU1202 on net9.0), 2026-07-06; this is what makes SDK adoption a stream split
- [x] 1.2 Publish this plan (openspec change + README compatibility statement)
- [ ] 1.3 csproj: make `TargetFramework` and the Jellyfin package versions overridable MSBuild properties (defaults: net9.0 / 10.11.9; no behaviour change)
- [ ] 1.4 ci.yml: non-blocking `jellyfin-12-prerelease` job: setup-dotnet 9+10, build with net10.0 + pinned `12.0.0-rc3` (newest RC as of 2026-07-26; bump the pin as each RC ships), run unit tests, `continue-on-error` with warning annotation
- [ ] 1.5 Verify dependabot surfaces the `12.0.0` stable bump when it ships (allowlist covers `Jellyfin.*`; confirm major bumps are not filtered)
- [ ] 1.6 Manual smoke test on a 12.0 RC server (newest RC, currently rc3; docker, throwaway library): install current release from the catalog, link an account, sync one film, run watchlist + diary import once. Also confirms our third-party catalog still installs on 12 despite the RC3 "disable all external plugins" guidance
- [ ] 1.7 Private ops repo: add "error categories segmented by jellyfin_version" to the monthly report watch list (tracked there, not here)

## 2. Phase B: 12.0.0 stable ships

- [ ] 2.1 Repoint the prerelease CI leg at `12.0.0` stable; build + full test suite must pass
- [ ] 2.2 Floor decision on the dependabot SDK-12 PR: default is close-without-merge and stay on the 10.11.9 floor (merging = net10.0 = dropping every 10.11 user; see design)
- [ ] 2.3 README + site: declare 12.0 support explicitly ("runs on 10.11.x and 12.x from one release"), with migration guidance covering Jellyfin's remove-plugins-before-migrating advice (RC3 notes escalated this to "disable all external plugins") and that config + sync history survive a remove/reinstall
- [ ] 2.4 Re-run the 1.6 smoke test against stable

## 3. Phase C: SDK adoption / stream split (only when a 12-only API is needed or ABI breaks)

- [ ] 3.1 Open a dedicated openspec change for the split before starting it
- [ ] 3.2 New minor: net10.0 + SDK 12.0.x, `targetAbi.txt` → 12.0.0.0 in the same PR (version-gate enforces both rules)
- [ ] 3.3 Verify manifest routing: a 10.11 server is still offered the last 10.11-floor version; a 12 server gets the new line
- [ ] 3.4 Cut `maintenance/10.11` branch from the last 10.11-floor tag; document the backport policy and the maintenance end condition (fleet supermajority on 12, or 6 months, whichever first)
- [ ] 3.5 Announce on the site + release notes; update the README compatibility statement
