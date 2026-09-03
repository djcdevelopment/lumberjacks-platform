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
.\tools\Start-AxeSwingLab.ps1 -Stage contact
```

Both default to Vulkan. `--lab=axe-arc` preserves Lab 01 without the chopping head;
`--lab=axe-head` preserves Lab 02 with it. The older `--lab=axe-swing` command remains an alias for
Lab 02. `--lab=axe-contact` preserves Lab 03: the accepted free swing crosses a finite neutral
witness and pauses exactly at the first deterministic cutting-edge contact.

Controls are `Space` or left mouse to replay, `F` to hold at contact, `L` to loop, `R` to restore
the accepted baseline, and `Tab` to tune. Tuning is exploratory; it does not alter the accepted
baseline until a reviewed code change updates the pinned values.

Lab 03 adds no force, wood, or damage. Cyan marks arrival velocity, red marks first contact, and
lime marks the already-committed follow-through behind the translucent witness. `F` continues the
free swing after the inspection pause. Target presets exercise near, nominal, far, high, and low
contact without making the answer depend on render-frame rate.

## Next gates

The next accepted question is one embedded bite into a fresh, upright, uniform trunk. Separate
effort and aim-through controls replace a generic power scalar. Later gates accumulate a
same-direction cut, oppose it to release a geometric chip, compare fixed and state-aware strike
sequences, and finally feed remaining support into controlled fall behavior. Each lab stops for
human visual acceptance before the next one introduces another variable. Networking, authority,
persistence, and the playable forest remain outside the isolated labs.

The earlier all-in-one `TreeFellingLab` remains reference material. Its tweened axe animation and
empirical Janka-based penetration equation are not inputs to the new contact or cutting models.
