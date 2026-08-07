*Meta: forum thread title suggestion — "Recoverable pieces". Paste everything below the divider as
the thread's opening post. Both pieces this thread was opened for now run, so it is kept as a
signpost rather than renamed: posts are matched by title, and renaming would orphan this thread and
the replies in it. Threads are referenced by name, not URL, so this seed needs no id to be correct.
It is deliberately long enough to render as two messages, matching what is already posted — the
tool refuses to shrink a post, because that would mean deleting a message people can already see.*

---

**Both pieces from this thread now run — here is where they went**

This thread was opened for two things that had been pruned out of the repo: a camera flythrough
that never had a flight mod, and a quest submission bridge that was never wired to anything.
Neither is recoverable any more. Both got built, and each has its own thread in this forum now.

**World photography → gallery.** The flythrough plan was replaced rather than revived. Instead of
flying a route and cutting a recording, it shoots stills: it reads a world save, finds every
structure people built, works out from each building's own geometry where a camera should stand and
which way it should point, and then photographs them unattended. The last run found 1,833
structures, photographed 161 of them, and produced 1,411 photographs — none of them framed by a
human. The planning half lives in the repo at `tools/selfie-stick/`; the in-game camera plugin is
still archive-only, which is the honest gap. Continue in the **"World photography → gallery"**
thread.

**Quest submission → review bridge.** This one turned out never to be a port. The old consumer
expected an evidence envelope — a screenshot on disk, a trace file, position and biome — and the
live mod deliberately produces none of that; its proof is the durable EventLog row. So the real
question was whether to rebuild the envelope or accept a thinner record. That is decided in ADR
0018: the EventLog row *is* the evidence. The bridge is ported to it at `tools/quest-bridge/` and
passes against a fixture; what remains is one real in-game completion travelling the whole path.
Continue in the **"Quest submission → review bridge"** thread.

**Neither is closed.** "Not recoverable" only means the code exists and runs — it is a different
word from finished, and both still have their claiming task open. CG-1 is bringing the camera
plugin into the repo so the capture half runs from a checkout. CG-2 is resolving prefab hashes to
real material names. QB-1 is the bridge's live proof, and it is the last step rather than the first
— the design is settled and the fixture path already passes. Claiming any of them still jumps you
straight to Contributor on the ladder, same as before; see the pinned post.

Replies already in this thread stay here, and nothing in them is stale for having been written
before the work landed. New discussion belongs in whichever of the two threads above it is actually
about.
