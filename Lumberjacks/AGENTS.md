# Repository working notes

## Living roadmap commit rule

Every non-merge commit must append one concise implementation note to
`docs/roadmap/commit-notes.jsonl` and regenerate
`src/Game.Gateway/Community/roadmap.html` in the same commit. Use
`npm run roadmap:note -- ...`, then run `npm run roadmap:check -- --staged` before
committing. Update `docs/roadmap/valheim-volunteer-roadmap.json` in that commit when
milestone truth, readiness, release identity, blockers, or proof changes.

The roadmap is public. Do not include SteamIDs, invite links, credentials, access
keys, passwords, or private diagnostic URLs. The note is associated with its
containing commit through Git history; do not create an amend loop by adding its own
future SHA.
