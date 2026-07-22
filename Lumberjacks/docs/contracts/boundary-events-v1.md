# Boundary events v1

The Gateway may emit four append-only JSONL event types:

- `identity.resolved`
- `authorization.decided`
- `zdo.batch.queued`
- `request.completed`

Every row has `schema_version`, `event_version`, `event_id`, `timestamp_utc`,
`event_type`, `trace_id`, `span_id`, `source`, and `data`. Event-specific values
live under `data`; old rows are never rewritten when the contract evolves.

The writer is bounded and non-blocking at request time. It rotates by UTC date or
size using `<date>-<sequence>.open.jsonl`, then flushes, closes, and renames the
segment to `<date>-<sequence>.jsonl`. Compression, manifests, hashes, and
derived storage are deferred.

Identity fields are observations, not credentials. Steam IDs, access keys,
request bodies, raw headers, correlations, and recipient IDs must not be placed
in this stream.
