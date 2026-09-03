# Axe lab series

The axe work advances through small, separately runnable experiments. A later lab may reuse an
accepted model, but it must not silently change an earlier lab's question or defaults.

## Accepted baseline

`accepted-axe-v1` records Derek's visual acceptance on 2026-09-02 of:

- an articulated shoulder, upper arm, elbow, forearm, wrist, and 0.76 m handle;
- a 22-degree horizontal-fell plane with the fixed 108 to -38 degree drive arc;
- 0.24 s windup, 0.28 s drive, and 0.42 s recovery;
- a 110-degree shoulder sweep from 65 degrees at windup to -45 degrees at follow-through;
- a 0.34 m by 0.27 m chopping head with a distinct cutting edge.

The reset action in every descendant lab returns to this code-pinned baseline. Unit tests spell out
every accepted value so unrelated work cannot retune it accidentally.

## Runnable labs

```powershell
.\tools\Start-AxeSwingLab.ps1 -Stage arc
.\tools\Start-AxeSwingLab.ps1 -Stage head
```

Both default to Vulkan. `--lab=axe-arc` preserves Lab 01 without the chopping head;
`--lab=axe-head` preserves Lab 02 with it. The older `--lab=axe-swing` command remains an alias for
Lab 02.

Controls are `Space` or left mouse to replay, `F` to hold at contact, `L` to loop, `R` to restore
the accepted baseline, and `Tab` to tune. Tuning is exploratory; it does not alter the accepted
baseline until a reviewed code change updates the pinned values.

## Next gates

The planned sequence is neutral contact witness, reach rack, single pine bite, impact position on
the same arc, geometric chip formation, and controlled pine fall. Each lab stops for human visual
acceptance before the next one introduces another variable. Networking, authority, persistence,
and the playable forest remain outside the isolated labs.

The earlier all-in-one `TreeFellingLab` remains reference material. Its tweened axe animation and
empirical Janka-based penetration equation are not inputs to the new contact or cutting models.
