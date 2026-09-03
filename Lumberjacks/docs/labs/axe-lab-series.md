# Axe lab series

The axe work advances through small, separately runnable experiments. A later lab may reuse an
accepted model, but it must not silently change an earlier lab's question or defaults.

## Accepted motion baselines

`accepted-axe-v1` records Derek's visual acceptance on 2026-09-02 of:

- an articulated shoulder, upper arm, elbow, forearm, wrist, and 0.76 m handle;
- a 22-degree horizontal-fell plane with the fixed 108 to -38 degree drive arc;
- 0.24 s windup, 0.28 s drive, and 0.42 s recovery;
- a 110-degree shoulder sweep from 65 degrees at windup to -45 degrees at follow-through;
- a 0.34 m by 0.27 m chopping head with a distinct cutting edge.

The reset action in every descendant lab returns to this code-pinned baseline. Unit tests spell out
every accepted value so unrelated work cannot retune it accidentally.

`accepted-axe-up-v1` records Derek's visual acceptance on 2026-09-03 of the opposing
under-the-shoulder stroke. It preserves the same body linkage, physical tool, timing, drive arc, and
joint keyframes while reflecting the swing plane to -22 degrees. Tests require DOWN to have negative
vertical velocity and UP to have positive vertical velocity at contact; changing a label or moving a
target cannot counterfeit the opposing motion.

## Runnable labs

```powershell
.\tools\Start-AxeSwingLab.ps1 -Stage arc
.\tools\Start-AxeSwingLab.ps1 -Stage head
.\tools\Start-AxeSwingLab.ps1 -Stage contact
.\tools\Start-AxeSwingLab.ps1 -Stage bite
.\tools\Start-AxeSwingLab.ps1 -Stage opposing
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

Lab 05 returns to motion and contact only. The accepted downward stroke remains locked while
`accepted-axe-up-v1` supplies the upward, under-the-shoulder stroke. A right-side toggle switches
between them. The two strokes register to symmetric upper and lower lip
targets around a tunable mouth height. Registration moves the entire simple actor stance -- body,
head, shoulder, arms, handle, and axe -- so target placement cannot conceal an incorrect arc.
Both strokes pause at their exact declared contact sample until `F` continues follow-through.
Left mouse selects and replays the upward under-swing; right mouse selects and replays the locked
downward top-swing. `Space` replays whichever stroke is currently selected.
The neutral witness is 1.5 m in diameter, about 4.4 accepted cutting-edge lengths across. Its bark
tangent is registered parallel to the cutting edge at impact, so the center of the bit makes an even
initial contact instead of one corner leading deeply into the trunk. The HUD reports inward velocity
separately from vertical velocity and edge alignment; no binding or retention consequence is applied
in this motion-only gate.
The simple actor is presented as a grounded blob: when whole-stance registration moves vertically,
the body squashes wider or stretches narrower while keeping constant scaled volume and a base on the
floor. This is presentation only; it does not alter the shoulder rig, swing path, or exact contact.
For the large witness, the green body and head shell are 20 percent larger and sit 6 cm farther from
the bark than the kinematic skeleton. The soft silhouette can move independently; the shoulder,
arms, axe, and centered tangent contact remain the measured mechanism.

The accepted UP baseline is a vertical reflection of the accepted swing plane while preserving the
accepted link lengths, head geometry, timings, and joint keyframes. Its plane, tool angles, shoulder
rotation, elbow rotation, and timing remain available for exploratory tuning.
The lab rejects a relabeled stroke if its cutting center is not actually travelling upward or
downward at the selected lip. Moving any UP motion slider marks the in-memory profile as tuned and
not accepted; `R` restores `accepted-axe-up-v1` and the default 10 cm mouth. Exploratory controls
never mutate either code-pinned baseline.

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

Lab 05 passed human visual acceptance on 2026-09-03. Lab 06 is therefore the next implementation
gate, but it has not started. It will accumulate same-direction cuts, oppose them to construct a
geometric chip candidate, compare fixed and state-aware strike sequences, and eventually feed
remaining support into controlled fall behavior. A potential chip must be derived from history
rather than spawned by a strike:

```text
potential chip volume = bounded cut cross-section area × overlapping cut width
```

Repeated same-direction cuts can deepen or widen an opening but cannot bound that cross-section.
An opposing cut must intersect the retained geometry and close a region against another cut or the
bark boundary. Even then the result is only a potential chip: fracture and remaining support decide
whether it releases. Lab 04 stores the required history and tests this geometry without enabling
release. Small stems are a separate support-exhaustion path and need not form a conventional notch.
Later retention work should distinguish a centered, tangent bit from an edge-leading strike: the
latter concentrates one corner too deeply and should carry materially more binding risk.
Each lab stops for human visual acceptance before the next one introduces another variable.
Networking, authority, persistence, and the playable forest remain outside the isolated labs.

The earlier all-in-one `TreeFellingLab` remains reference material. Its tweened axe animation and
empirical Janka-based penetration equation are not inputs to the new contact or cutting models.
