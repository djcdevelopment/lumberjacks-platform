# Living Valheim roadmap

The living roadmap has three parts:

- `valheim-volunteer-roadmap.json` — current milestone state, gates, proof, and
  authority boundary;
- `commit-notes.jsonl` — append-only implementation journal, one record for every
  non-merge commit; and
- `../../src/Game.Gateway/Community/roadmap.html` — deterministic, self-contained
  HTML generated from those two sources and served by the Gateway at `/roadmap`.

The detailed design remains in
`../network/valheim-volunteer-platform-plan.md`. The roadmap is the scannable
operational view, not a second source of architecture prose.

## Add a commit note

Before committing, update milestone state when necessary, then append a note:

```powershell
npm run roadmap:note -- `
  --milestone M0 `
  --kind implementation `
  --summary "Froze the reproducible Gateway and mod release." `
  --impact "Volunteer packages can now be traced to exact source and runtime hashes." `
  --verification "Clean checkout reproduced the declared DLL hash." `
  --evidence "path/to/release-manifest.json"
```

Repeat `--milestone`, `--verification`, or `--evidence` as needed. Allowed kinds are
`planning`, `implementation`, `verification`, `deployment`, `decision`, `rollback`,
and `documentation`.

The note belongs to the Git commit that contains it. Do not add that commit's SHA to
the note: the SHA does not exist until after the file is committed, and Git history
already provides the association.

## Check before committing

```powershell
npm run roadmap:check
git add <changed-files> docs/roadmap/commit-notes.jsonl `
  src/Game.Gateway/Community/roadmap.html
npm run roadmap:check -- --staged
```

The normal check validates schemas, milestone dependencies, journal references,
ordering, no-secret patterns, and deterministic output. The staged check additionally
requires an appended journal record and rejects modification or deletion of historic
journal lines.

## Update milestone state without a note

Do not. A milestone-state change is itself a decision or implementation result and
must have a journal note in the same commit. Edit the JSON, run `roadmap:note`, and
stage both sources plus the generated HTML.

## No-secrets rule

The roadmap is public. Never put SteamIDs, invite URLs, enrollment credentials,
bearer tokens, access keys, private IPs, passwords, or unredacted diagnostic links in
either structured source.

## Re-render only

```powershell
npm run roadmap:render
```

The renderer uses Node built-ins only and does not access the network.

## Republish `/roadmap` without a release

The Gateway re-reads the asset per request behind an mtime-and-length cache, and
`LUMBERJACKS_ROADMAP_HTML` relocates it, so a rendered page reaches P7 by copying one
file — no image rebuild, no restart:

```powershell
gcloud compute scp src/Game.Gateway/Community/roadmap.html `
  "$VM_NAME:/mnt/lumberjacks/roadmap/roadmap.html" `
  --project "$PROJECT_ID" --zone "$ZONE" --tunnel-through-iap
```

`scp` is used deliberately: `gcloud compute ssh` over IAP does not exit from a
backgrounded shell, while `scp` returns normally.

Verify that P7 serves the exact committed bytes — the response header is the SHA-256 of
the file on disk, so it matches `sha256sum` of the artifact in the tree:

```powershell
curl -sI http://<p7>:<port>/roadmap | Select-String X-Roadmap-Sha256
```

The mount is a directory, not a file: a bind-mounted *file* pins an inode on a Linux
host, so a replacing copy would never be seen inside the container. An absent or empty
mount costs freshness, not the page — resolution falls through to the copy built into
the image.

Without the mount, `/roadmap` still serves the asset baked into the image at build time,
which is stale for exactly as long as the deployed release lags the tree.
