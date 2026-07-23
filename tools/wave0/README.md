# Wave 0 non-human gates

These commands reduce live two-client testing to the observations only Derek can
provide. They do not replace the final apply/observe gate.

```powershell
# Gateway motion relay semantics, no Valheim clients required
tools\wave0\Test-Wave0SyntheticMotion.ps1 -OutputJson captures\wave0-synthetic-motion.json

# Runtime release/readiness alignment across P7, OMEN, and i5
tools\i5\Test-Wave0Readiness.ps1 -SummaryOnly -OutputJson captures\wave0-readiness.json
```

Run order before asking for a live movement course:

1. `Test-Wave0SyntheticMotion.ps1`
2. `Test-Wave0Readiness.ps1`
3. two-client idle capture
4. one bounded apply/observe course
5. role reversal

If either non-human gate fails, stop and use its receipt instead of repeating a
live join/movement test.
