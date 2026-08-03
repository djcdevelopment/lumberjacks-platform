# P7 C10b tools

`Invoke-C10bCandidateProof.ps1` is the fail-closed wrapper for the retained C8
two-client scenario against P7. It does not start the VM or promote artifacts.
Those are separate, rollback-aware operations:

1. Rehearse `tools/p7/Invoke-P7BootDeterminism.ps1` in its default read-only
   preflight mode. With explicit P7 power/fix authority, run it once with
   `-Action run -Execute`; it preserves the pre-fix evidence, refuses unsafe
   save/disk states, installs the staged fix, performs the real cold cycle, and
   proves automatic systemd retry with a one-shot injected failure.
2. Retain its instance-id and boot-id-bound `p7_boot_determinism_acceptance`
   JSON receipt. Promotion and proof reject the receipt after another boot.
3. Rehearse `tools/p7/Invoke-C10bPairPromotion.ps1` without `-Execute`, then
   execute it with the boot receipt. It snapshots and promotes the exact Gateway
   and frozen mod as one rollback unit; either-side failure restores both.
4. Run `tools/p7/Invoke-C10bCandidateProof.ps1 -Action preflight
   -BootReceiptPath <receipt>`; only a fully green receipt admits `-Action run`.

The wrapper verifies the local release pair, P7's loaded image and both visible
DLL copies, the server/unit/Gateway state, and the i5 lane before it generates a
fresh single-use C8 manifest or launches either game client.
