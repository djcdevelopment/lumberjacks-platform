# Extraction provenance

- Source repository: `https://github.com/djcdevelopment/baseline`
- Immutable source: `split-base-20260811`
- Source commit: `aceb2eb48d770885a2c4171b926867f4ee82b4a4`
- Extracted: 2026-08-12
- Commit map: [`docs/provenance/commit-map.txt`](docs/provenance/commit-map.txt)

The source tag maps to filtered commit
`93a2513ca424be24eb3e500cf1f12df5abd26d97` before the sovereign scaffold.

## Filter invocations

```text
git filter-repo --force --path Lumberjacks/ --path infra/gcp/p7/ --path fieldlab/scripts/ --path fieldlab/autonomous/ --path fieldlab/scenarios/ --path fieldlab/routes/ --path fieldlab/docs/ --path fieldlab/MULTIPLAYER-NETWORK-SETUP.md --path fieldlab/NETCODE-MAP.md --path fieldlab/PORTAL-LIFECYCLE-MAP.md --path fieldlab/plan-native-network-final-cutover.md --path fieldlab/experiments/m7/ --path tools/p7/ --path tools/wave0/ --path tools/workbench/ --path tools/authority-lab/ --path tools/guest-package/ --path .githooks/ --path tests/test_powershell_param_contracts.py --path tests/test_guest_package.py --path tests/fixtures/guest-package/
git filter-repo --force --invert-paths --path Lumberjacks/oldimages/ --path Lumberjacks/network/mcp/ --path Lumberjacks/src/Quest.Studio/ --path-glob fieldlab/experiments/m7/**/runs/**
git filter-repo --force --strip-blobs-bigger-than 5M
git filter-repo --force --replace-text gitleaks-replacements.txt
```

The size pass dropped no additional blobs; every remaining historical blob is
below 5 MiB. `Lumberjacks/oldimages`, Quest Studio, MCP, and historical M7 run
outputs were removed by explicit ownership filters.

## Secret scrub

Gitleaks 8.30.1 initially reported five non-secret fixtures/false positives.
History was rewritten to redact three credential-shaped negative-test values
and two prose/manifest strings whose adjacency triggered the generic API-key
heuristic. The full 670-commit filtered history then scanned clean: zero leaks.
