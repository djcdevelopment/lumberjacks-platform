# Native cutover scenario lane

This lane drives the two physical Windows Valheim clients against the AM4 development
server without putting an operator in the game loop. It retains one correlated run
directory under `fieldlab/runs/native-valheim/`.

The scenario manifest is data, not a keyboard macro. The client mod accepts only its
fixed bounded action types, validates every bound in-process, and correlates the
manifest with an expiring native-autotest request.

## Preconditions

- Run from the unified `C:\work\baseline` checkout.
- AM4's native Linux Docker server and the local Lumberjacks Gateway are healthy.
- The server address is reachable from both physical clients.
- OMEN has an interactive Steam session with its seeded character.
- i5 is reachable through the existing BatchMode SSH lane and has its one-time Steam
  login and character seed.
- Build `ComfyNetworkSense` in Release before invoking the composition.

If i5 preflight says the laptop is offline, stop and report that state. Do not retry
in a loop and do not fall back to password authentication.

## Generate a manifest

The baseline profile proves launch, join, bounded movement, disconnect, fresh-process
resume, rejoin, and shutdown:

```powershell
$runId = 'native-YYYYMMDD-c0-example'
$scenario = "fieldlab\runs\native-valheim\$runId\scenario.json"
fieldlab\scripts\New-NativeValheimCutoverScenario.ps1 `
    -RunId $runId `
    -OutputPath $scenario `
    -Profile baseline
```

The full profile additionally includes nearest-item pickup, run-tagged ownership
targets, and bounded zone crossing. Its ownership tags must begin with
`cutover-<run-id>` so a stale world object cannot satisfy the action.

The `c1` profile adds two Lumberjacks-only controls per client. The resume probe drops
the canonical WebSocket after receiving a numbered request but before ack/response,
then requires the same connection id, a later resume epoch, the exact replayed
sequence, and one Gateway-accepted response. The timeout probe sends its response while
Gateway intentionally withholds the reliable receipt; success is the bounded
`bounded_receipt_timeout_no_native_fallback` marker.

The `c2a` profile adds a selected typed direct-control pulse and an intentional
withhold per client. Run it with `-EnableDirectControlCutover`; the orchestrator stamps
the AM4 run id, arms the exact native suppression class, retrieves the server receipt,
and disarms the gate in `finally`.

The `c2b` profile adds targeted request/response, server broadcast, a real
zero-argument target-ZDO `RPC_ResetCloth`, and an intentional withhold per client. Run
it with `-EnableRoutedRpcCutover`; the native-autotest request arms the bounded client
registry, while runtime control arms AM4 and selects the private Gateway URL. The
orchestrator retrieves the server receipt, disarms the gate, and restores the previous
Gateway URL in `finally`.

## Run both clients

```powershell
fieldlab\scripts\Invoke-NativeValheimCutoverScenario.ps1 `
    -RunId $runId `
    -ScenarioPath $scenario `
    -Server 'AM4_ADDRESS:2456'
```

For C2a:

```powershell
fieldlab\scripts\Invoke-NativeValheimCutoverScenario.ps1 `
    -RunId $runId `
    -ScenarioPath $scenario `
    -EnableDirectControlCutover
```

For C2b:

```powershell
fieldlab\scripts\Invoke-NativeValheimCutoverScenario.ps1 `
    -RunId $runId `
    -ScenarioPath $scenario `
    -EnableRoutedRpcCutover
```

The orchestrator:

1. performs the one-shot i5 link preflight;
2. SHA256-verifies the harness and mod deployment;
3. queues i5 work through its interactive scheduled-task seam;
4. runs OMEN in the current interactive session;
5. waits for both clients' terminal scenario receipts;
6. retrieves i5 evidence and writes `composition.json`; and
7. stops both clients on failure.

The i5 Gateway route is a bounded SSH reverse tunnel owned by the orchestrator. It
keeps the development Gateway private, fails immediately if the remote port cannot be
allocated, and is force-closed in the same `finally` block that stops failed clients.

`disconnect_resume` deliberately exits Valheim. The harness waits for the process to
release files, launches a fresh process, and resumes only completed action IDs.
If Valheim left a completed Steam Cloud `.fch.new` transaction without the final
character file, the client uses Valheim's Cloud API to promote it and relaunches once.

## Reduce the evidence

After copying the server ledger and BepInEx/runtime receipts into the run directory:

```powershell
fieldlab\scripts\Write-NativeNetworkCutoverSummary.ps1 `
    -RunDirectory "fieldlab\runs\native-valheim\$runId" `
    -RunId $runId `
    -PoisonRunDirectory 'fieldlab\runs\native-valheim\<poison-run>' `
    -PoisonRunId '<poison-run>'
```

Accept the slice only when `machine-summary.json` reports `result: passed`, both
client lifecycles completed with the expected resume count and terminal state, all
three actors have exact final ledger summaries, plugin hashes agree, and ledger
writer drops/faults are zero.

For a normal baseline, native totals are expected to be non-zero until the cutover
ladder removes them. For the final native-zero scenario, enable native poison: any
trip is a failed boundary, even if the visible gameplay action appeared to work.

For C1, reduce the accepted run separately:

```powershell
fieldlab\scripts\Write-LumberjacksSessionCutoverSummary.ps1 `
    -RunDirectory "fieldlab\runs\native-valheim\$runId" `
    -RunId $runId
```

`c1-machine-summary.json` must pass every stable-id, resume-epoch, exact-replay,
single-response, bounded-timeout, lifecycle, artifact-hash, and Gateway-health check.

For C2a:

```powershell
fieldlab\scripts\Write-DirectControlCutoverSummary.ps1 `
    -RunDirectory "fieldlab\runs\native-valheim\$runId" `
    -RunId $runId
```

`c2a-machine-summary.json` must prove one typed delivery and one bounded stale result
per client, an explicitly registered native tripwire, zero native copies, every
selected AM4 native attempt suppressed, matching artifacts, a clean runtime disarm,
and healthy Gateway.

For C2b:

```powershell
fieldlab\scripts\Write-RoutedRpcCutoverSummary.ps1 `
    -RunDirectory "fieldlab\runs\native-valheim\$runId" `
    -RunId $runId
```

`c2b-machine-summary.json` must prove one targeted request/response, one broadcast,
one real target-ZDO dispatch, and one bounded stale result per client; exact server
dispatch counts; zero native selected-method copies, duplicate deliveries, or handler
failures; every selected native attempt suppressed; matching artifacts; clean runtime
disarm and Gateway-URL restoration; and healthy Gateway.

## Retained evidence contract

Keep the manifest, composition receipt, `machine-summary.json`, both client lifecycle
files/logs/autotest receipts, the server native-use ledger, and the relevant server
receipts under the run directory. Record verified observations separately from
inferences. Do not retain credentials, Steam identifiers, or backup copies of test
world data.
