# The Physics of Swinging an Axe: What Rod Cross Taught Me About Game Development

*A game developer reads a physics paper about hammers and realizes his entire axe model was wrong.*

---

I'm building a multiplayer survival game where tree felling is a core skill. My first physics model treated an axe swing like a pendulum: the handle swings down, gravity accelerates the head, velocity = sqrt(2 * g * L). Simple. Intuitive. Wrong.

Rod Cross, a physicist at the University of Sydney, published a paper titled "Physics of swinging a striking implement" that changed how I think about this. His key finding: **gravity is negligible when you swing something.**

## The Numbers

Cross measured the forces during an actual swing of a 0.277 kg rod. At the moment the rod was vertical (peak speed):
- **Centripetal force (FC):** 68 N — pulling the head inward along the arc
- **Gravity (Mg):** 2.7 N — barely a rounding error

The force keeping the implement on its circular path was **25 times stronger** than gravity. Gravity doesn't drive the swing. Your muscles do.

## The Correct Model

An axe swing is a driven circular arc, not a free fall. Three forces matter:

1. **Tangential force (FT = Ma)** — your muscles accelerating the head along the arc. Approximately constant.
2. **Centripetal force (FC = MV^2/R)** — grows as speed increases. This is the force that makes a full swing feel "heavy" at the bottom of the arc. For a golf club, FC reaches 500 N — almost a full body weight.
3. **Wrist couple (C)** — a torque applied by the wrists to control rotation direction. Without it, FT would spin the handle backwards.

The head velocity at impact is simply: **V = omega x R**, where omega comes from constant angular acceleration over the swing arc, and R is the distance from shoulder to axe head.

For an axe with a 1.1m swing radius and 0.35-second swing: V = 4.5 x 1.1 = **~10 m/s**. A powerful overhead chop might reach 15-20 m/s with body rotation.

## What This Means for Game Design

The interesting thing about Cross's model isn't the math — it's the **skill inputs**.

In the pendulum model, the only variable is "did you swing or not." In Cross's model, three things matter:
- **Swing radius** (longer handle = faster head, but harder to control)
- **Swing time** (faster swing = more energy, but less accuracy)
- **Wrist couple timing** (early release = faster whip, less directional control; late release = more control, less speed)

That third one is the key. The wrist couple is what separates a skilled chopper from a novice. Early in the swing, you lock the wrist to prevent wrong-direction rotation. Late in the swing, you release it to let centripetal force whip the head around. The timing of that release determines both speed and accuracy.

For my game, this maps to a skill axis: swing effort vs. precision. A rushed swing has more energy but less control over where the notch forms. A deliberate swing is slower but places the cut exactly where you planned.

## The Network Punchline

All of this physics runs on the server. What goes over the network? Three bytes per swing: strike angle (1 byte), strike height (1 byte), swing effort (1 byte). The server computes everything else.

The result — where the notch formed, how much hinge wood remains, whether the tree is about to barber-chair — compresses to 6 floats (24 bytes) in the entity update datagram.

Realistic physics. Three bytes in. Twenty-four bytes out. That's the design constraint that makes it interesting.

---

*Reference: Cross, R. (2009). "Physics of swinging a striking implement." Rod Cross, Physics Department, University of Sydney.*
