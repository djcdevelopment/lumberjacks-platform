# M7 authority experiment lab

This is temporary R&D scaffolding for authority decisions. It is not a production
telemetry service and it does not authorize a P7 behavior change. Each experiment
keeps its question and prediction in `experiment.md`, the input in `scenario.yaml`,
machine facts in `runs/<run_id>/receipt.json`, raw rows in `runs/<run_id>/raw/`, and
one interpretation in `learning-log.jsonl`.

The first slice is synthetic and runs through the .NET 9 AuthorityLab container.
`scenario.yaml` is JSON-compatible YAML so the first runner can remain dependency
free. All public fields are snake_case and identities are fixture-local opaque IDs.

Result classes are `supported`, `refuted`, `mixed`, `inconclusive`, and
`harness_failed`. A passing synthetic result proves wiring and direction only; it
does not prove Valheim visual quality or production capacity.
