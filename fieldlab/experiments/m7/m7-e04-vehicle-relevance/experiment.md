# M7-E04 - Do live vehicle snapshots fan out by independent native relevance edges?

Status: supported by repeatable pure-driver receipts; physical gate pending

## Goal

Exercise the exact Unity-free state machine used by ship and saddle snapshot
fan-out before a physical release is launched.

## Objective

Keep the owner near, move one observer out, and drive a third recipient through
outside, enter, hysteresis retention, leave, and re-enter around the 64 m outer
band.

## Predicted outcome

The third recipient produces `Outside, Entered, Retained, Left, Entered`.
Only `Entered` and `Retained` decisions deliver a direct snapshot. One
recipient's transition never changes another recipient's edge or authority.

## Limits

This is the pure producer-side decision seam. It proves exact edge math and
fan-out independence, not native peer enumeration or rendered gameplay; the
physical cutover run owns those claims.

## Setup and procedure

Run through `tools/authority-lab/Invoke-AuthorityExperiment.ps1` with experiment
`m7-e04-vehicle-relevance`, then retain the checked receipt alongside the
physical gate.

## Result

The settled-source runs `pure-20260803T021107Z` and
`pure-20260803T021107Z-repeat` each emitted 15 complete events, satisfied all
four invariants, and normalized equal. The observed third-recipient sequence
was exactly `Outside, Entered, Retained, Left, Entered`; owner and observer
edges remained independent. This supports the producer policy only. Native
peer enumeration, direct routed delivery, untagged authority discovery,
dedicated-server-owned publishing, and rendered AoI leave/re-entry remain owned
by the r39 physical run.
