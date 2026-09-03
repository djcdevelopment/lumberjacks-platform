# ADR 0021: Human-Gated, Stateful Axe Mechanics

## Status

Accepted on 2026-09-03.

Depends on ADR 0006 (Godot client), ADR 0014 (deterministic simulation), and ADR 0019
(tree-felling physics). This ADR refines ADR 0019's claim that the earlier all-in-one
`TreeFellingLab` validated the axe interaction. Its polar trunk model and compact network projection
remain useful; its tweened swing and empirical one-shot penetration are not the accepted axe model.

## Context

Tree felling is intended to be a physical, skill-expressive interaction rather than a damage button.
The older prototype combined animation, impact, material removal, notch geometry, falling, and a
network projection. Several individually small errors survived inside that combined surface: an axe
could rotate on the wrong axis, one joint could move while the rest stayed locked, a felling stroke
could resemble splitting firewood, and a target could be scaled or oriented so poorly that the
contact looked plausible only from one camera.

Automated tests can prove determinism, units, bounds, and state transitions. They cannot decide that
a shoulder rotation feels human, that an axe head is upside down, or that a tree is visibly too small
relative to the tool. Those judgments require a person looking at one isolated question at a time.

## Decision

### Use serial, human-gated labs

Advance the mechanic through separately runnable experiments. Each lab preserves the previous
accepted baseline and adds one question:

| Lab | Question | Accepted result |
|---|---|---|
| 01 | Does the articulated arc feel like a person swinging? | Shoulder, elbow, wrist, handle, and follow-through |
| 02 | Is the chopping head oriented and proportioned correctly? | Physical head and cutting-edge geometry |
| 03 | Where and how does the free edge first meet a neutral witness? | Deterministic contact sample and inspection pause |
| 04 | What does one accepted stroke do to fresh uniform wood? | Energy/aim-limited bite, retained cut line, kerf, rings, and conditional retention |
| 05 | Can an opposing upward stroke be aimed at the lower notch lip? | Accepted under-the-shoulder motion and centered tangent contact |

The accepted motion calibrations are:

- `accepted-axe-v1`: downward/top stroke, visually accepted 2026-09-02;
- `accepted-axe-up-v1`: upward/under-the-shoulder stroke, visually accepted 2026-09-03.

Exploratory sliders may change the in-memory UP profile, but the UI must mark it as not accepted.
Reset restores the code-pinned calibration. A later lab may consume an accepted calibration; it may
not silently retune it.

### Separate mechanism, material state, and presentation

Pure C# contracts own deterministic kinematics, contact registration, playback clamping, bite
solving, retention rolls, kerf state, retained cut marks, and potential-chip geometry. Godot projects
that state into the interactive scene. Frame rate and renderer choice must not change the result.

Lab presentation may cheat where that improves readability without corrupting the mechanism. The
current actor is a slime-like blob: stance-height changes squash it wider or stretch it narrower,
preserving scaled volume while keeping its base on the floor. Its green shell is 20 percent larger
and sits 6 cm behind the measured skeleton. The shoulder, arm, axe, and contact state remain exact.

### Treat input as part of the physical model

In the opposing-stroke lab:

- left mouse selects and replays the upward under-swing;
- right mouse selects and replays the downward top-swing;
- `Space` replays the selected stroke;
- `F` continues from the exact contact inspection pause.

The direct mapping is intentional. Choosing the cutting sense should feel like choosing the stroke,
not like changing a mode and then pressing a generic attack button.

### Register a centered bit, not an edge-leading corner

The Lab 05 witness is 1.5 m in diameter, about 4.4 accepted cutting-edge lengths across. Its tangent
at contact is parallel to the horizontal projection of the cutting edge. The cutting center reaches
the declared lip exactly, and both edge endpoints begin at equal depth. Inward speed, vertical speed,
and edge alignment remain separate diagnostics.

Registration may translate the complete measured stance to a target. It may not move contact math
without moving the actor, and an UP/DOWN label is rejected unless cutting-center velocity actually
travels in that vertical sense at contact.

### Keep a cut as history

A strike leaves both rasterized 5 mm kerf state and an exact retained centerline with penetration,
vertical travel, and face-width metadata. Retention is an event after penetration: a retained axe
does not begin a backswing until `F`; a free axe withdraws automatically.

A chip is not a strike effect. It can become a candidate only when opposing retained cut lines bound
material. Same-direction strikes may deepen or widen an opening but cannot invent a closed chip.
Lab 04 contains the history and candidate-geometry foundation; release is disabled.

## Evidence and References

- Derek visually reviewed each motion iteration on OMEN and accepted the final DOWN and UP strokes.
- USDA Forest Service, [One Moving Part](https://www.fs.usda.gov/t-d/pubs/pdfpubs/pdf18232812P/Part11a_UsingAnAx.pdf),
  distinguishes downward over-the-shoulder and upward under-the-shoulder felling strokes.
- OSHA, [Felling Cuts and Notches](https://www.osha.gov/etools/logging/manual-operations/felling/cuts/notches),
  provides the notch vocabulary and shows that lower-cut direction depends on notch style.
- The executable evidence is `AxeOpposingStrokeTests`, the earlier axe/contact/bite tests, and the
  independently runnable Godot stages described in `docs/labs/axe-lab-series.md`.

## Consequences

Positive:

- Human judgment is spent on one visible variable at a time.
- Deterministic tests protect accepted motion from accidental retuning.
- Physics and presentation can evolve independently without hiding their boundary.
- Contact, retention, and eventual chip release have explicit state transitions.
- The labs expose mistakes that a combined gameplay slice can camouflage.

Negative:

- Serial acceptance is slower than generating one large prototype.
- The accepted UP stroke is intentionally simple: it reflects the swing plane and does not yet model
  feet, hips, torso twist, grip sliding, or two independent hands.
- Lab 04 still uses a simplified face-width band. It is not the blade length and not a curved-bark
  contact solution.
- The declared contact sample is a controlled lab witness, not continuous production collision.
- No current lab proves chip fracture, hinge failure, falling, multiplayer authority, or persistence.

## Alternatives Considered

- **Continue the all-in-one tree-felling lab.** Rejected because coupled variables made basic visual
  and mechanical errors expensive to identify.
- **Mirror cut math while reusing one swing.** Rejected because an upward label must correspond to
  actual upward tool motion.
- **Let collision placement define the stroke.** Rejected for this gate because target movement can
  conceal bad kinematics. The lab uses an exact declared contact sample and reports registration.
- **Use a rigid humanoid capsule.** Rejected as a presentation constraint. The blob deformation is
  clearer, grounded, and consistent with the game's provisional slime/ooze character language.
- **Spawn a chip after a hit count.** Rejected because a chip must be derived from intersecting cut
  history and remaining material.

## Follow-Up Work

1. Build Lab 06 as accumulated strikes on one trunk, with `R` providing fresh wood.
2. Preserve DOWN/UP mouse semantics and the embedded-axe `F` gate.
3. Replace the arbitrary face-width band with effective blade contact clipped against curved bark.
4. Show old cuts muted, the newest cut bright, and intersection points explicitly.
5. Construct a translucent potential-chip volume only when opposing cuts bound material; do not
   release or animate it in that lab.
6. Add edge-leading contact to retention/binding risk before fracture work.
7. Gate fracture, remaining support, controlled fall, authority, persistence, and network projection
   independently.
