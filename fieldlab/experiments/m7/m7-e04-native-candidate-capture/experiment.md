# M7-E04 native candidate capture

## Question

Can the existing Valheim-side priority and ZDO probe logs be converted into the
common M7 evidence envelope without losing source rows or pretending that native
observation is already Lumberjacks authority?

## Hypothesis

The probe JSONL can be normalized by source line into candidate observations for
priority objects and outbound ZDO rows. Status rows and inbound rows should remain
available as ignored evidence, while malformed rows must make the result
inconclusive rather than silently disappear.

## Limits and assumptions

- This experiment reads a captured file; it does not launch Valheim or change
  replication behavior.
- `object` rows and outbound `zdo` rows are candidates. Status rows and inbound
  ZDO rows are retained as ignored rows.
- No native capture is committed yet. A successful parser run is not native
  evidence until a real probe file is supplied.
- The normalizer preserves the exact source as `raw/native-source.jsonl` and writes
  anomalies/ignored rows separately.

## Prediction

Valid candidate rows produce `authority.native_candidate_observed` events with
snake_case payloads and source-line ordering. A malformed line produces an
inconclusive receipt while still retaining valid rows and the malformed raw line.

## Replay boundary

`replay-native` applies the current pure distance-band policy to candidates that
carry a distance and emits an explicit `observation_only` comparison. A missing
native decision is intentional at this stage; the comparator must not infer that
Valheim agreed merely because the Lumberjacks policy produced an output.

## Next step

Capture one bounded native probe window from a disposable local client, then run
the `replay` comparator against the same scenario. Do not promote any result until
native completeness and candidate-to-decision correspondence are observed.
