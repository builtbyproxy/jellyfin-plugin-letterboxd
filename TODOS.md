# TODOS

## Release workflow pushes directly to main
**What:** The release.yml GitHub Action commits the updated manifest checksum directly to main without a PR.
**Why:** If branch protection rules are ever added, this will break the release pipeline.
**Context:** Currently works fine for a solo project. The CI bot (github-actions[bot]) updates `manifest.json` checksum after building the release ZIP. If branch protection is added, the bot will need a PAT with bypass permissions or the workflow needs to create a PR instead.
**Depends on:** Nothing. Low priority.
