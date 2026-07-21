# Why I Modeled Real Forestry Physics for a Multiplayer Game — And What It Taught Me About Network Architecture

*Building a tree felling simulator from USDA forestry manuals, academic cutting dynamics, and Rod Cross's swing physics paper. Then compressing it all into 24 bytes.*

---

## The Problem

Every survival game has tree chopping. Most of them treat it like a health bar: swing, number goes down, tree falls in a canned animation. That's fine for most games. But what if tree felling *was* the game?

I'm building a community survival platform where the server owns everything — every tree, every structure, every player position. The client is a thin rendering shell. So when a player chops a tree, the server has to compute *exactly* what happens: where the notch forms, how much hinge wood remains, whether the tree barber-chairs, which direction it falls. And then send that result to every nearby player in under 33 bytes.

That constraint — realistic physics that compress to almost nothing — is what led me down a rabbit hole of forestry manuals, academic papers on cutting dynamics, and a physics paper about swinging hammers.

## Three Perspectives on One Problem

I started by reading the source material the way a forester would read it, looking for three things:

**What can the player do?** The USDA Forest Service manual ("An Ax to Grind") describes two notch types: conventional (45-degree opening) and open-face (70+ degrees). The back cut goes 2 inches above the notch floor. The hinge wood — the uncut bridge between notch and back cut — is what controls the fall. Cut through the hinge and you lose all control. Leave too much and the tree can split (barber chair). The Wisconsin Timber Felling manual adds bore cutting for large trees and felling against the natural lean with wedges.

These aren't game mechanics I invented. They're real techniques that real people use. The game mechanic is learning them.

**What are the tree's physical properties?** Species matters. Oak is dense (48 lb/ft^3) with strong fibers. Ash is prone to barber-chairing. Pine is soft and easy to cut. Each tree has a diameter (DBH), height, natural lean from growth, crown mass distribution, age, and history (fire scars, wind twist). All of these affect how the tree responds to cuts and falls.

**What's the terrain doing?** Slope changes everything — a tree on a hillside falls differently, rolls after impact. Wind can redirect a falling tree mid-fall. Surrounding trees create hung-up scenarios. Spring poles (saplings pinned under fallen trees) are dangerous.

## The Physics Model

### Polar Cross-Sections

I modeled the trunk as a stack of vertical slices, each divided into 36 angular sectors (10 degrees each). Each sector stores how much wood remains as a fraction from 0 (fully cut) to 1 (intact). When you cut a notch, the sectors on the notch side go to zero. The back cut removes sectors from the opposite side. What's left in between is the hinge wood — and you can compute its width, depth, and structural strength directly from the remaining sectors.

This is elegant because it naturally handles any cut geometry: conventional notches, open-face notches, bore cuts, individual axe strikes at arbitrary angles. You don't need separate code for each technique. The polar model unifies them all.

### Why Gravity Doesn't Drive the Swing

This one surprised me. My first implementation modeled the axe swing as a pendulum: velocity = sqrt(2 * g * handle_length). A physicist named Rod Cross at the University of Sydney published a paper showing why this is wrong.

When you swing a striking implement, gravity is negligible compared to the forces your muscles apply. Cross measured the forces during an actual swing: the centripetal force (pulling the head inward along the arc) reached 68 Newtons, while gravity was only 2.7 Newtons. The implement follows a *driven circular arc*, not a free pendulum.

The correct model:
- Angular acceleration is approximately constant over the swing arc
- The wrist provides a couple that controls rotation direction (otherwise the handle's tangential force would spin the head backwards)
- Head velocity at impact: V = ω × R, where R is the distance from shoulder to axe head

This gives realistic velocities (8-20 m/s), energies (50-300 joules), and centripetal forces (50-500 newtons) that match real-world measurements.

### Hinge Mechanics and Barber Chair

The hinge is where the physics gets interesting. Its resistance to tipping is governed by the rectangular section modulus: σ × w × t² / 6, where w is width and t is depth. As the tree begins to tilt, the fibers stretch and weaken progressively. When stress exceeds fiber strength, the hinge fails and the tree enters free fall.

Barber chair — where the trunk splits vertically instead of falling — happens when four conditions align: the hinge is too narrow (< 8% of DBH), the back cut is at or below the notch floor, the species is prone to splitting (ash, elm), and the tree has significant lean. Get any of those wrong and the simulation produces the same catastrophic failure that kills real loggers.

## The Network Constraint

Here's where it connects back to the actual product. My server runs at 20 Hz with binary payloads capped at 33 bytes per entity update. The detailed polar model (36 floats per slice × 20 slices) would cost ~2,880 bytes per tree — 85× over budget.

The solution: the detailed model runs on the server, but the *result* compresses into 6 floats:
- Notch angle and depth
- Back cut depth  
- Hinge width fraction
- Fall tilt angle and bearing

That's 24 bytes. Fits in one datagram. The client reconstructs approximate visual geometry from these 6 numbers — it won't show per-sector material removal, but it will show the notch, the hinge zone, and the fall direction correctly.

The detailed physics run server-side. The compressed result flies over the network. The client renders it. This is what "thin client, platform-owned authority" means in practice.

## What This Is Really About

The tree felling lab isn't the game. It's a proof that the network architecture works for something that feels complex and physical. If I can send a realistic tree fell in 24 bytes at 20 Hz, I can send anything the game needs.

The real gameplay loop isn't "click to chop." It's: walk through the forest, cruise the trees, inspect the ones you're interested in (the data loads progressively as you stay near — that's the spatial interest management system working as game feel), plan where you want it to land versus where physics says it will land, then execute the fell and see if your skill matches your planning.

The network architecture that makes this possible — progressive AoI loading, binary serialization, spatial hashing, graceful degradation — is the actual product. The trees are just the most beautiful stress test I could imagine.

---

*The tree felling physics lab is part of an open-source community survival platform built with .NET 9, Godot 4.6, and an unreasonable number of forestry manuals.*
