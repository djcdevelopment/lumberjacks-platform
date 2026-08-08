# ADR 0019 — A guard that cannot fail is decoration; prove the failure, not the pass

- **Status:** Accepted (2026-08-08)
- **Rung:** cross-cutting — any gate, contract, acceptance criterion, or test that admits work

## Context

On 2026-08-08 a single session found four independent guards that were structurally incapable of
returning a failure. They were not broken by a regression; none of them had ever been able to fail.

1. **The identity gate.** [PD-8](../../../docs/decisions/pd-8-isolated-runtime-and-toolset-repository.md)
   names `GET /identity` as *the* contract boundary between `baseline` and the extracted `isolate`
   runtime. `gateway_identity()` hardcoded `"project": "baseline"`, and `source_root` resolves to
   `/workspace` inside any container built from the shared Dockerfile. Both repositories therefore
   returned byte-identical provenance. The endpoint nominated to discriminate them could not.
2. **The API contract.** `network/mcp/contracts/api-contract.json` declared `/healthz` returning
   `status` (it returns `ok`/`gateway`), `/identity` returning `port` (it returns
   `listen_port`/`published_port`), and a transport default of 8721 (the kernel's is 8720; 8721 is a
   different container's host publish). The file had drifted from the implementation beside it
   because no test read it.
3. **The C9 acceptance criterion.** "Produce one side-by-side clip" was satisfied by the existence
   of a file. The retained artifact's own receipt records 4 and 5 motion events across two 20-second
   panels — a static scene with a counter overlay. The plan recorded the missing verdict as a
   pending *reviewer action* for six days rather than as an insufficient artifact.
4. **The compose launch.** A generated plan's launch command declared the same project `name:` as
   the live lab and, with `AUTONOMOUS_ROOT` unset, rendered the world mounts as blank-rooted
   absolute paths. `docker compose config` exits **0** with warnings. The failure mode of the guard
   was a green run against an empty world directory.

The common shape is not carelessness. Each guard was written by someone competent, and each *looks*
like a check when read. What they share is that their success condition is the existence of a thing
rather than a property of it: a file exists, a response arrives, a command exits 0, an artifact is
retained. None of them was ever run against a case that should have produced red.

This is `L-2026-08-05-8` ("should work now is a claim; verified delivery is a fact") generalized
from a reporting habit to a design defect. That lesson was recorded as an individual discipline
problem and recurred anyway, in the artifacts themselves.

## Decision

**A guard is not accepted until it has been observed to fail.**

1. **Every new gate ships with a demonstrated red.** Disable the mechanism, or feed it the case it
   exists to reject, and record the failure output alongside the pass. This is mutation testing
   applied to guards rather than to production code, and it is cheap: three contract mutations, one
   planted privacy leak, and one disabled fix each took under a minute this session.
2. **Acceptance criteria state a property, never an existence.** "Produce one clip" becomes "produce
   one clip whose per-panel motion event counts are stated and non-trivial." "The endpoint responds"
   becomes "the endpoint distinguishes X from Y." If the criterion cannot be written as something
   that could be observably absent, it is not a criterion.
3. **Contract files are read by a test or they are deleted.** A declarative contract that nothing
   parses is documentation with a schema header. `test_api_contract_conformance.py` is the pattern:
   drive the real routes, assert in **both** directions — a declared field the response omits is red,
   *and* a response field the contract does not declare is red — so the contract cannot silently
   describe a subset of reality.
4. **Negative controls are part of a receipt.** A verification receipt that contains only passes is
   not evidence that the checks work. The isolate boundary receipt records a deliberate probe of the
   wrong port and the non-zero exit it produced; without that line the passing probes prove nothing.
5. **Where a command's failure mode is a zero exit, render before you run.** Compose substitution,
   config templating, and anything else that degrades to a plausible-looking default must be
   rendered and inspected, not executed and observed.

## Consequences

- New tests cost roughly double: write it, break the thing it guards, confirm red, restore. Accepted.
- Some existing guards will not survive contact with this rule. That is the point; finding them is
  cheaper than trusting them.
- Mutation runs need care with build staleness. This session produced a false red because
  `Copy-Item` preserved a backup's original mtime and MSBuild reused the DLL compiled during the
  mutation. Touch the source after any restore before believing an incremental result.
- The rule applies to acceptance artifacts, not just code. C9's six-day stall was an acceptance
  criterion failing this test, and it cost more than any of the code defects.

## Evidence

- [`fieldlab/evidence/isolate-boundary-verification-20260807.json`](../../evidence/isolate-boundary-verification-20260807.json)
  — negative control, three contract mutations, planted privacy leak
- [`fieldlab/evidence/am4-blackscreen-refresh-snapshot-20260808.md`](../../evidence/am4-blackscreen-refresh-snapshot-20260808.md)
  — the disabled-fix mutation (2 of 3 tests red) and the stale-build false red
- [`fieldlab/retro/SESSION-RETRO-2026-08-08.md`](../../retro/SESSION-RETRO-2026-08-08.md) — lessons
  `L-2026-08-08-1`, `-2`, `-8`
- Related: [0017](0017-prove-the-lane-users-ship-on.md) (proofs must run the lane users ship on) —
  same failure at lane scale; [0014](0014-boot-must-converge-or-say-so.md) (three ways for a failure
  to look like a success).
