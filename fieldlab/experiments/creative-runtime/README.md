# Creative runtime experiments

This family tests whether Lumberjacks can make mod and presentation work observable,
budgeted, routable, and reversible without prematurely changing Valheim authority.

The experiments reuse AuthorityLab's scenario, append-only event, receipt, comparison,
and bounded-failure contracts. Synthetic cost units prove policy shape only. Measured
patch and frame costs must replace them before a live gate is considered.

## Current train

| Experiment | Result | Meaning |
|---|---|---|
| `cre-e01-runtime-envelope` | supported | protected mutations survived; presentation degraded monotonically; route semantics and bounded queue held; repeat hash matched |
| `cre-e02-gateway-pressure-route` | supported | both real Gateway motion paths delivered the same nine selected frames in order while all 23 suppressed decisions stayed out of transport |
| `cre-e03-transport-faults` | supported after refinement | both paths rejected stale motion, accepted gaps/wrap/resume, and exposed recipient fanout as a separate cost multiplier; refuted receipts are retained |
| `cre-e04-presentation-consumer` | supported | repeat decisions matched; latest-wins reduced 19 applies to 14, expiry reduced them to 12 while preserving both final fresh sequences; Unity feel remains untested |
| `cre-e05-current-apply-model` | supported model | repeat rows matched; receive is already latest-per-ZDO, while render apply scales at FPS x fresh remotes and exponential convergence is frame-rate stable under the modeled defaults |
| `cre-e06-motion-phase-rollups` | instrumentation ready | cumulative receive/drain/bind/render/error/displacement rollups flow through existing client JSONL and Companion capture; fixture analysis passes, live timing remains pending |

Next: complete the patch-load A/B run, run the bounded two-client CRE-E06 capture,
map measured call cost and projected recipient fanout into CRE-E01, then feed captured
timing into interpolation candidates. No broader P7 gameplay authority change is
authorized by these results.
