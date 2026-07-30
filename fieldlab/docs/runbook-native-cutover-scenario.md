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

## Run both clients

```powershell
fieldlab\scripts\Invoke-NativeValheimCutoverScenario.ps1 `
    -RunId $runId `
    -ScenarioPath $scenario `
    -Server 'AM4_ADDRESS:2456'
```

The orchestrator:

1. performs the one-shot i5 link preflight;
2. SHA256-verifies the harness and mod deployment;
3. queues i5 work through its interactive scheduled-task seam;
4. runs OMEN in the current interactive session;
5. waits for both clients' terminal scenario receipts;
6. retrieves i5 evidence and writes `composition.json`; and
7. stops both clients on failure.

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

## Retained evidence contract

Keep the manifest, composition receipt, `machine-summary.json`, both client lifecycle
files/logs/autotest receipts, the server native-use ledger, and the relevant server
receipts under the run directory. Record verified observations separately from
inferences. Do not retain credentials, Steam identifiers, or backup copies of test
world data.
