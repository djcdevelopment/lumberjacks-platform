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
.\tools\Start-AxeSwingLab.ps1 -Stage bite
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

Lab 04 keeps that accepted motion and answers only what one fresh bite does to an upright, uniform
trunk. Effort changes delivered kinetic energy; aim-through independently caps the intended depth.
The deterministic solver stops at whichever limit is reached first and briefly pauses for
inspection. Retention is a separate event: harder and deeper bites raise the chance that the wood
holds the head, while a released chip lowers it and poor cut alignment raises it. Lab 04 has no chip
release yet, so it supplies that condition explicitly rather than fabricating one. An embedded head
does not begin any backswing until `F` is pressed; a free head withdraws and recovers automatically.
Every replay starts with fresh wood, so this lab does not accumulate strikes or release chips.

The retention event uses an explicit deterministic roll sequence. The HUD shows both probability
and roll, allowing identical trials to be replayed and edge cases to be tested without tying the
outcome to render timing or hidden randomness.

The wood state stores two related but distinct results: 5 mm kerf cells describe removed/severed
material, while a retained cut record stores the exact centerline, deepest endpoint, and simplified
tangential face width for every strike.
The elevation, plan, and bark-face views project that same state. Center-dense concentric growth
rings in the plan view are a diagnostic ruler for penetration; they do not yet change resistance.
The nominal accepted-motion preset currently stops near 5 cm in the uniform calibration.

## Next gates

After human acceptance of Lab 04, later gates accumulate a same-direction cut, oppose it to release
a geometric chip, compare fixed and state-aware strike sequences, and finally feed remaining
support into controlled fall behavior. The potential chip is derived from history rather than
spawned by a strike:

```text
potential chip volume = bounded cut cross-section area × overlapping cut width
```

Repeated same-direction cuts can deepen or widen an opening but cannot bound that cross-section.
An opposing cut must intersect the retained geometry and close a region against another cut or the
bark boundary. Even then the result is only a potential chip: fracture and remaining support decide
whether it releases. Lab 04 stores the required history and tests this geometry without enabling
release. Small stems are a separate support-exhaustion path and need not form a conventional notch.
Each lab stops for human visual acceptance before the next one introduces another variable.
Networking, authority, persistence, and the playable forest remain outside the isolated labs.

The earlier all-in-one `TreeFellingLab` remains reference material. Its tweened axe animation and
empirical Janka-based penetration equation are not inputs to the new contact or cutting models.
