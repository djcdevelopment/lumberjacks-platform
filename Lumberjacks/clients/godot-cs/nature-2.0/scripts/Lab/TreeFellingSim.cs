using System;

namespace CommunitySurvival.Lab;

/// <summary>
/// Standalone tree felling physics simulation.
/// No Godot dependency — pure math. Models trunk as polar cross-section slices.
///
/// Each TrunkSlice is a float[36] (10-degree sectors) representing remaining
/// wood radius fraction (1.0 = intact, 0.0 = fully cut). Cuts zero out sectors.
/// The hinge is the uncut bridge between notch and back cut.
/// </summary>
public class TreeFellingSim
{
    public const int Sectors = 36; // 10 degrees each
    public const float SectorAngle = 2f * MathF.PI / Sectors; // ~0.1745 rad

    // --- Tree configuration ---
    public TreeConfig Config;

    // --- Trunk state ---
    public int SliceCount;
    public float SliceHeight; // height of each slice in feet
    public float[][] Slices; // [sliceIndex][sector] = remaining radius fraction 0..1
    public float[][] OriginalSlices; // for reset

    // --- Cut tracking ---
    public NotchState Notch;
    public float BackCutHeight; // feet from base
    public float BackCutAngle; // radians, direction back cut comes from
    public float BackCutDepth; // fraction of diameter
    public bool HasNotch;
    public bool HasBackCut;
    public bool HasBoreCut;

    // --- Hinge ---
    public HingeState Hinge;

    // --- Dynamics ---
    public TreeDynamicState Dynamics;

    // --- Environment ---
    public float WindStrength; // mph
    public float WindDirection; // degrees
    public float SlopeAngle; // degrees
    public float SlopeDirection; // degrees

    public void Initialize(TreeConfig config)
    {
        Config = config;
        float heightFt = config.HeightFt;
        SliceCount = Math.Max(4, (int)(heightFt / 3f)); // ~3ft per slice
        SliceHeight = heightFt / SliceCount;

        Slices = new float[SliceCount][];
        OriginalSlices = new float[SliceCount][];
        for (int i = 0; i < SliceCount; i++)
        {
            Slices[i] = new float[Sectors];
            OriginalSlices[i] = new float[Sectors];
            // Taper: radius decreases linearly from base to top
            float t = (float)i / (SliceCount - 1);
            float radiusFraction = 1f - t * 0.6f; // top is 40% of base radius
            for (int s = 0; s < Sectors; s++)
            {
                Slices[i][s] = radiusFraction;
                OriginalSlices[i][s] = radiusFraction;
            }
        }

        HasNotch = false;
        HasBackCut = false;
        HasBoreCut = false;
        Notch = default;
        Hinge = default;
        Dynamics = new TreeDynamicState { Phase = FallPhase.Standing };
    }

    // --- Cuts ---

    /// <summary>
    /// Cut a notch (front face cut) into the trunk.
    /// faceAngle: direction the tree should fall toward (radians).
    /// depthFraction: how deep the notch goes as fraction of diameter (0.2 to 0.6).
    /// notchType: open-face (70 deg) or conventional (45 deg).
    /// height: feet from base where the notch is cut.
    /// </summary>
    public void ApplyNotchCut(float height, float faceAngle, float depthFraction, NotchType notchType)
    {
        int sliceIdx = HeightToSlice(height);
        if (sliceIdx < 0 || sliceIdx >= SliceCount) return;

        float halfSpread = notchType == NotchType.OpenFace
            ? 35f * MathF.PI / 180f  // 70 deg total opening
            : 22.5f * MathF.PI / 180f; // 45 deg total opening

        // Remove material in the notch zone
        float radius = Config.DbhInches / 2f;
        float cutDepth = depthFraction * Config.DbhInches; // inches into trunk

        // Affect the slice at cut height and one above/below for realism
        int startSlice = Math.Max(0, sliceIdx - 1);
        int endSlice = Math.Min(SliceCount - 1, sliceIdx + 1);

        for (int si = startSlice; si <= endSlice; si++)
        {
            for (int s = 0; s < Sectors; s++)
            {
                float sectorAngle = s * SectorAngle;
                float angleDiff = AngleDiff(sectorAngle, faceAngle);

                if (MathF.Abs(angleDiff) < halfSpread + SectorAngle * 0.5f)
                {
                    // Sector is within the notch opening
                    // Depth of cut tapers: full at center, zero at edges
                    float edgeFactor = 1f - MathF.Abs(angleDiff) / (halfSpread + SectorAngle * 0.5f);
                    float cutFraction = depthFraction * edgeFactor;

                    // Remaining = original - cut, clamped to 0
                    float remaining = Slices[si][s] - cutFraction * Slices[si][s];
                    Slices[si][s] = MathF.Max(0f, remaining);
                }
            }
        }

        Notch = new NotchState
        {
            Type = notchType,
            FaceAngle = faceAngle,
            DepthFraction = depthFraction,
            Height = height
        };
        HasNotch = true;
        RecomputeHinge();
    }

    /// <summary>
    /// Cut the back cut from the opposite side of the notch.
    /// heightOffset: inches above the notch floor (should be ~2" for proper technique).
    /// depthFraction: how far to cut; stops to leave hinge wood.
    /// </summary>
    public void ApplyBackCut(float heightOffsetInches, float depthFraction)
    {
        if (!HasNotch) return;

        // Back cut comes from opposite the notch face
        float backAngle = Notch.FaceAngle + MathF.PI;
        if (backAngle > 2f * MathF.PI) backAngle -= 2f * MathF.PI;

        float backHeight = Notch.Height + heightOffsetInches / 12f; // convert inches to feet
        int sliceIdx = HeightToSlice(backHeight);
        if (sliceIdx < 0 || sliceIdx >= SliceCount) return;

        float halfSpread = 15f * MathF.PI / 180f; // back cut is narrower, ~30 deg kerf

        for (int s = 0; s < Sectors; s++)
        {
            float sectorAngle = s * SectorAngle;
            float angleDiff = AngleDiff(sectorAngle, backAngle);

            if (MathF.Abs(angleDiff) < halfSpread + SectorAngle * 0.5f)
            {
                float edgeFactor = 1f - MathF.Abs(angleDiff) / (halfSpread + SectorAngle * 0.5f);
                float cutFraction = depthFraction * edgeFactor;
                float remaining = Slices[sliceIdx][s] - cutFraction * Slices[sliceIdx][s];
                Slices[sliceIdx][s] = MathF.Max(0f, remaining);
            }
        }

        BackCutHeight = backHeight;
        BackCutAngle = backAngle;
        BackCutDepth = depthFraction;
        HasBackCut = true;
        RecomputeHinge();
    }

    /// <summary>
    /// Bore (plunge) cut — removes material from the center outward,
    /// used for large trees or felling against the lean.
    /// </summary>
    public void ApplyBoreCut(float height, float faceAngle, float depthFraction)
    {
        int sliceIdx = HeightToSlice(height);
        if (sliceIdx < 0 || sliceIdx >= SliceCount) return;

        // Bore cut removes a band perpendicular to face angle
        // leaving hinge wood on the face side and holding wood on the back
        float perpLeft = faceAngle + MathF.PI / 2f;
        float perpRight = faceAngle - MathF.PI / 2f;

        for (int s = 0; s < Sectors; s++)
        {
            float sectorAngle = s * SectorAngle;
            float diffToFace = MathF.Abs(AngleDiff(sectorAngle, faceAngle));
            float diffToBack = MathF.Abs(AngleDiff(sectorAngle, faceAngle + MathF.PI));

            // Bore removes the middle band (not the hinge side, not the back holding wood)
            bool isHingeSide = diffToFace < 30f * MathF.PI / 180f;
            bool isBackHold = diffToBack < 20f * MathF.PI / 180f;

            if (!isHingeSide && !isBackHold)
            {
                float cutFraction = depthFraction * 0.8f;
                float remaining = Slices[sliceIdx][s] - cutFraction * Slices[sliceIdx][s];
                Slices[sliceIdx][s] = MathF.Max(0f, remaining);
            }
        }

        HasBoreCut = true;
        if (HasNotch) RecomputeHinge();
    }

    /// <summary>
    /// Individual axe strike — removes a wedge of material at a specific angle.
    /// Simulates forehand or backhand chop at ~45 degrees.
    /// </summary>
    public void ApplyAxeStrike(float height, float strikeAngle, float removeFraction)
    {
        int sliceIdx = HeightToSlice(height);
        if (sliceIdx < 0 || sliceIdx >= SliceCount) return;

        // Each strike removes a narrow wedge (~20-degree arc)
        float halfArc = 10f * MathF.PI / 180f;

        for (int s = 0; s < Sectors; s++)
        {
            float sectorAngle = s * SectorAngle;
            float angleDiff = AngleDiff(sectorAngle, strikeAngle);

            if (MathF.Abs(angleDiff) < halfArc + SectorAngle * 0.5f)
            {
                float edgeFactor = 1f - MathF.Abs(angleDiff) / (halfArc + SectorAngle * 0.5f);
                float remove = removeFraction * edgeFactor;
                Slices[sliceIdx][s] = MathF.Max(0f, Slices[sliceIdx][s] - remove);
            }
        }

        RecomputeHinge();
    }

    /// <summary>
    /// Physics-based axe strike using Cross's driven circular arc model.
    /// Computes head velocity from angular acceleration over a 90-degree swing arc,
    /// then penetration from kinetic energy vs species Janka hardness.
    ///
    /// Cross (2009): gravity is negligible during a swing; the implement follows a
    /// driven circular path with V_head = ω × R_swing.
    /// </summary>
    public SwingResult ApplyPhysicsStrike(float height, float strikeAngle, AxeConfig axe, float swingEffort)
    {
        // Cross model: constant angular acceleration over a 90-degree swing arc
        // For quarter-circle: s = αt²/2, s = π/2 rad → α = π/(2t²), ω = αt = π/(2t)
        // V_head = ω × R_swing
        float baseSwingTime = 0.35f; // seconds for full-effort swing
        float swingTime = baseSwingTime / MathF.Max(0.3f, swingEffort);
        float omega = MathF.PI / (2f * swingTime);
        float vHead = omega * axe.SwingRadiusM;

        // KE = ½mv² (head mass dominates; handle mass is small)
        float ke = 0.5f * axe.HeadMassKg * vHead * vHead;

        // Centripetal force FC = mv²/R (Cross: this is the dominant force, >>gravity)
        float fc = axe.HeadMassKg * vHead * vHead / axe.SwingRadiusM;

        // Species Janka hardness (N) mapped from relative Hardness field
        var specData = GetSpeciesData(Config.Species);
        float jankaN = specData.Hardness * 6672f; // Oak(0.9)=6005N, Pine(0.5)=3336N

        // Wedge angle factor: thinner blade penetrates deeper
        float wedgeBaseline = 0.19f; // ~11 deg half-angle baseline
        float wedgeFactor = wedgeBaseline / MathF.Max(0.05f, axe.WedgeHalfAngleRad);

        // Green wood is softer, dry wood is harder
        float moistureFactor = Config.IsDry ? 1.2f : 1.0f;

        // Penetration (m): KE × wedgeFactor / (Janka × bladeWidth × moisture)
        // Tuned so ~150J into Pine gives ~35-45mm depth
        float scalingConstant = 3.5f;
        float penetrationM = ke * wedgeFactor * scalingConstant
                           / (jankaN * axe.BladeWidthM * moistureFactor);
        penetrationM = MathF.Min(penetrationM, 0.09f); // max ~3.5 inches

        // Impact force estimate: KE delivered over penetration distance
        float impactForceN = penetrationM > 0.001f ? ke / penetrationM : 0;

        // Convert penetration to removeFraction for the slice model
        float radiusM = Config.DbhInches * 0.0127f; // inches to meters
        float removeFraction = penetrationM / radiusM;

        ApplyAxeStrike(height, strikeAngle, removeFraction);

        return new SwingResult
        {
            HeadVelocityMs = vHead,
            KineticEnergyJ = ke,
            PenetrationM = penetrationM,
            CentripetalForceN = fc,
            ImpactForceN = impactForceN,
        };
    }

    // --- Hinge Analysis ---

    public void RecomputeHinge()
    {
        if (!HasNotch) { Hinge = default; return; }

        int sliceIdx = HeightToSlice(Notch.Height);
        if (sliceIdx < 0 || sliceIdx >= SliceCount) { Hinge = default; return; }

        float[] slice = Slices[sliceIdx];
        float faceAngle = Notch.FaceAngle;

        // The hinge is the remaining wood on either side of the notch face direction
        // Find contiguous sectors with material that bridge notch and back cut sides
        int faceSector = (int)(faceAngle / SectorAngle) % Sectors;
        int backSector = (faceSector + Sectors / 2) % Sectors;

        // Walk from face toward each side to find the hinge bands
        float hingeWidthRad = 0;
        float minHingeDepth = float.MaxValue;
        int hingeSectors = 0;

        // The hinge is the remaining material perpendicular to the fall direction
        // Check sectors 90 degrees from face (left and right of hinge)
        int leftCenter = (faceSector + Sectors / 4) % Sectors;
        int rightCenter = (faceSector + 3 * Sectors / 4) % Sectors;

        // Scan for remaining material around the hinge zone
        for (int offset = -3; offset <= 3; offset++)
        {
            int leftIdx = (leftCenter + offset + Sectors) % Sectors;
            int rightIdx = (rightCenter + offset + Sectors) % Sectors;

            if (slice[leftIdx] > 0.01f)
            {
                hingeSectors++;
                hingeWidthRad += SectorAngle;
                if (slice[leftIdx] < minHingeDepth) minHingeDepth = slice[leftIdx];
            }
            if (slice[rightIdx] > 0.01f)
            {
                hingeSectors++;
                hingeWidthRad += SectorAngle;
                if (slice[rightIdx] < minHingeDepth) minHingeDepth = slice[rightIdx];
            }
        }

        float radius = Config.DbhInches / 2f;
        float hingeWidthInches = hingeWidthRad * radius;
        float hingeDepthInches = minHingeDepth == float.MaxValue ? 0 : minHingeDepth * radius;

        // Species fiber strength (relative, Oak = 1.0 baseline)
        float fiberStrength = GetSpeciesData(Config.Species).FiberStrength;
        float dryFactor = Config.IsDry ? 0.7f : 1.0f; // dry wood is more brittle

        // Hinge strength: section modulus w*t^2/6 * fiber strength
        float strength = hingeWidthInches * hingeDepthInches * hingeDepthInches / 6f * fiberStrength * dryFactor;

        Hinge = new HingeState
        {
            WidthInches = hingeWidthInches,
            DepthInches = hingeDepthInches,
            HingeSectors = hingeSectors,
            FiberStress = 0, // computed during dynamics
            Strength = strength,
            Failed = hingeWidthInches < 0.5f || hingeDepthInches < 0.1f
        };
    }

    // --- Force Analysis ---

    public float ComputeTippingMoment()
    {
        if (!HasNotch) return 0;

        float heightFt = Config.HeightFt;
        float cutHeight = Notch.Height;
        float aboveHeight = heightFt - cutHeight;
        float radius = Config.DbhInches / 2f;

        // Weight above cut (simplified: conical trunk + crown)
        var specData = GetSpeciesData(Config.Species);
        float trunkVolCubicFt = MathF.PI * (radius / 12f) * (radius / 12f) * aboveHeight * 0.5f; // cone approximation
        float trunkWeight = trunkVolCubicFt * specData.DensityLbPerCuFt;
        float crownWeight = trunkWeight * 0.4f; // crown is ~40% of above-cut weight

        // Lean offset: natural lean shifts center of mass
        float leanRad = Config.NaturalLeanDeg * MathF.PI / 180f;
        float leanOffset = MathF.Sin(leanRad) * aboveHeight; // feet

        // Project lean onto fall direction
        float leanDirRad = Config.NaturalLeanBearing * MathF.PI / 180f;
        float fallDirRad = HasNotch ? Notch.FaceAngle : 0;
        float leanProjection = MathF.Cos(leanDirRad - fallDirRad) * leanOffset;

        // Crown offset (asymmetric mass)
        float crownOffset = Config.CrownOffset * 2f; // feet of offset

        // Tipping moment: weight * horizontal offset from hinge
        float gravityMoment = (trunkWeight + crownWeight) * (leanProjection + crownOffset);

        // Wind contribution
        float windRad = WindDirection * MathF.PI / 180f;
        float windProjection = MathF.Cos(windRad - fallDirRad);
        float windForce = WindStrength * 0.5f * windProjection; // simplified force
        float windMoment = windForce * aboveHeight * 0.7f; // acts on upper 70% of tree

        // Dynamic tilt contribution
        float tiltRad = Dynamics.FallTiltDeg * MathF.PI / 180f;
        float tiltMoment = (trunkWeight + crownWeight) * MathF.Sin(tiltRad) * aboveHeight * 0.5f;

        return gravityMoment + windMoment + tiltMoment;
    }

    public float ComputeResistingMoment()
    {
        return Hinge.Strength;
    }

    public bool CheckBarberChair()
    {
        if (!HasNotch || !HasBackCut) return false;

        var specData = GetSpeciesData(Config.Species);
        float heightOffsetInches = (BackCutHeight - Notch.Height) * 12f;

        return Hinge.WidthInches < 0.08f * Config.DbhInches
            && heightOffsetInches <= 0
            && specData.BarberChairProne
            && Config.NaturalLeanDeg > 5f;
    }

    // --- Dynamics ---

    public void Advance(float dt)
    {
        if (Dynamics.Phase == FallPhase.Ground) return;

        float tippingMoment = ComputeTippingMoment();
        float resistingMoment = ComputeResistingMoment();

        switch (Dynamics.Phase)
        {
            case FallPhase.Standing:
                if (HasNotch && HasBackCut && tippingMoment > resistingMoment * 0.3f)
                {
                    Dynamics.Phase = FallPhase.HingeBending;
                    Dynamics.FallBearing = Notch.FaceAngle * 180f / MathF.PI;
                }
                break;

            case FallPhase.HingeBending:
                if (CheckBarberChair())
                {
                    Dynamics.Phase = FallPhase.BarberChair;
                    Dynamics.BarberChairProgress = 0;
                    break;
                }

                // Angular acceleration from net torque
                float heightFt = Config.HeightFt;
                float massLb = EstimateMassLb();
                float momentOfInertia = massLb * heightFt * heightFt / 3f; // rod about end
                float netTorque = tippingMoment - resistingMoment;
                float angularAccel = netTorque / Math.Max(momentOfInertia, 1f);

                Dynamics.AngularVelocity += angularAccel * dt * 50f; // scale factor for visual speed
                Dynamics.FallTiltDeg += Dynamics.AngularVelocity * dt;

                // Progressive hinge weakening
                Hinge.FiberStress = Math.Min(1f, Dynamics.FallTiltDeg / 15f); // stress builds to failure at ~15 deg
                Hinge.Strength *= (1f - Hinge.FiberStress * dt * 2f); // strength degrades

                if (Hinge.FiberStress > 0.95f || Hinge.Strength < 1f)
                {
                    Dynamics.Phase = FallPhase.FreeFall;
                    Hinge.Failed = true;
                }

                // Check notch closing for conventional vs open face
                float closeAngle = Notch.Type == NotchType.OpenFace ? 70f : 45f;
                if (Dynamics.FallTiltDeg > closeAngle && !Hinge.Failed)
                {
                    // Notch closed — tree stops or snaps
                    Dynamics.Phase = FallPhase.FreeFall;
                    Hinge.Failed = true;
                }
                break;

            case FallPhase.FreeFall:
                // Pure gravity rotation
                float g = 32.2f; // ft/s^2
                float halfHeight = Config.HeightFt / 2f;
                float gravAccel = g * MathF.Cos(Dynamics.FallTiltDeg * MathF.PI / 180f) / halfHeight;
                Dynamics.AngularVelocity += gravAccel * dt * 180f / MathF.PI; // deg/s
                Dynamics.FallTiltDeg += Dynamics.AngularVelocity * dt;

                // Ground impact
                float groundAngle = 90f + SlopeAngle; // flat ground = 90 deg tilt
                if (Dynamics.FallTiltDeg >= groundAngle)
                {
                    Dynamics.FallTiltDeg = groundAngle;
                    Dynamics.AngularVelocity = 0;
                    Dynamics.Phase = FallPhase.Ground;
                }
                break;

            case FallPhase.BarberChair:
                Dynamics.BarberChairProgress += dt * 0.8f;
                Dynamics.FallTiltDeg += dt * 5f; // slow tilt during split
                if (Dynamics.BarberChairProgress >= 1f)
                {
                    Dynamics.Phase = FallPhase.FreeFall;
                    Hinge.Failed = true;
                }
                break;
        }
    }

    // --- Reset ---

    public void Reset()
    {
        for (int i = 0; i < SliceCount; i++)
            Array.Copy(OriginalSlices[i], Slices[i], Sectors);

        HasNotch = false;
        HasBackCut = false;
        HasBoreCut = false;
        Notch = default;
        Hinge = default;
        Dynamics = new TreeDynamicState { Phase = FallPhase.Standing };
    }

    // --- Network projection ---

    /// <summary>
    /// Extract compact state suitable for network transmission (24 bytes).
    /// The detailed TrunkSlice model is lab-only; production sends this.
    /// </summary>
    public CompactTreeState GetCompactState()
    {
        return new CompactTreeState
        {
            NotchAngleRad = HasNotch ? Notch.FaceAngle : 0,
            NotchDepthFraction = HasNotch ? Notch.DepthFraction : 0,
            BackCutDepthFraction = BackCutDepth,
            HingeWidthFraction = Config.DbhInches > 0 ? Hinge.WidthInches / Config.DbhInches : 0,
            FallTiltDeg = Dynamics.FallTiltDeg,
            FallBearing = Dynamics.FallBearing,
        };
    }

    // --- Helpers ---

    private int HeightToSlice(float heightFt)
    {
        if (SliceHeight <= 0) return 0;
        return Math.Clamp((int)(heightFt / SliceHeight), 0, SliceCount - 1);
    }

    private float EstimateMassLb()
    {
        var specData = GetSpeciesData(Config.Species);
        float radius = Config.DbhInches / 2f / 12f; // feet
        float vol = MathF.PI * radius * radius * Config.HeightFt * 0.5f;
        return vol * specData.DensityLbPerCuFt;
    }

    public static float AngleDiff(float a, float b)
    {
        float diff = a - b;
        while (diff > MathF.PI) diff -= 2f * MathF.PI;
        while (diff < -MathF.PI) diff += 2f * MathF.PI;
        return diff;
    }

    public static SpeciesData GetSpeciesData(TreeSpecies species) => species switch
    {
        TreeSpecies.Oak => new SpeciesData(48f, 1.0f, false, 0.9f),
        TreeSpecies.Pine => new SpeciesData(35f, 0.7f, false, 0.5f),
        TreeSpecies.Ash => new SpeciesData(42f, 0.85f, true, 0.8f),
        TreeSpecies.Birch => new SpeciesData(38f, 0.6f, true, 0.6f),
        _ => new SpeciesData(40f, 0.8f, false, 0.7f),
    };
}

// --- Data Structures ---

public enum TreeSpecies { Oak, Pine, Ash, Birch }
public enum NotchType { Conventional, OpenFace }
public enum FallPhase { Standing, HingeBending, FreeFall, Ground, BarberChair }

public readonly struct SpeciesData
{
    public readonly float DensityLbPerCuFt;
    public readonly float FiberStrength; // relative, 1.0 = oak
    public readonly bool BarberChairProne;
    public readonly float Hardness; // relative, 1.0 = hardest

    public SpeciesData(float density, float fiber, bool barberChair, float hardness)
    {
        DensityLbPerCuFt = density;
        FiberStrength = fiber;
        BarberChairProne = barberChair;
        Hardness = hardness;
    }
}

public struct TreeConfig
{
    public TreeSpecies Species;
    public float DbhInches;    // diameter at breast height
    public float HeightFt;
    public float NaturalLeanDeg;    // degrees from vertical
    public float NaturalLeanBearing; // degrees, compass direction of lean
    public float CrownOffset;  // 0-1, asymmetry
    public float Age;
    public bool IsDry;
    public float Twist;
    public int FireScars;

    public static TreeConfig Default => new()
    {
        Species = TreeSpecies.Oak,
        DbhInches = 18f,
        HeightFt = 60f,
        NaturalLeanDeg = 3f,
        NaturalLeanBearing = 0f,
        CrownOffset = 0.2f,
        Age = 80f,
        IsDry = false,
        Twist = 0f,
        FireScars = 0
    };
}

public struct NotchState
{
    public NotchType Type;
    public float FaceAngle;     // radians — direction tree falls toward
    public float DepthFraction; // 0.0 to 1.0 of diameter
    public float Height;        // feet from base
}

public struct HingeState
{
    public float WidthInches;
    public float DepthInches;
    public int HingeSectors;
    public float FiberStress;   // 0-1, how close to failure
    public float Strength;      // resisting moment capacity
    public bool Failed;
}

public struct TreeDynamicState
{
    public FallPhase Phase;
    public float FallTiltDeg;        // degrees from vertical
    public float FallBearing;    // degrees, compass
    public float AngularVelocity;  // degrees per second
    public float BarberChairProgress; // 0-1 during barber chair split
}

public struct AxeConfig
{
    public float HeadMassKg;        // kg (SI)
    public float HandleLengthM;     // m — butt to head
    public float SwingRadiusM;      // m — shoulder to head (arm + handle)
    public float WedgeHalfAngleRad; // radians — half the included blade angle
    public float BladeWidthM;       // m (SI)

    public static AxeConfig Default => new()
    {
        HeadMassKg = 1.5f,
        HandleLengthM = 0.76f,
        SwingRadiusM = 1.1f,        // ~0.34m arm + 0.76m handle
        WedgeHalfAngleRad = 0.19f,  // ~11 deg half-angle (22 deg included)
        BladeWidthM = 0.10f,        // ~4 inches
    };
}

/// <summary>
/// Result of a physics-based swing using Cross's driven arc model.
/// All units SI (m, kg, s, N, J).
/// </summary>
public struct SwingResult
{
    public float HeadVelocityMs;     // V_head = ω × R (m/s)
    public float KineticEnergyJ;     // ½mv² (J)
    public float PenetrationM;       // depth into wood (m)
    public float CentripetalForceN;  // FC = mv²/R — dominant swing force (N)
    public float ImpactForceN;       // KE / penetration distance (N)
}

/// <summary>
/// Compact network-ready tree state — 24 bytes (6 floats).
/// Proves the detailed polar slice model collapses to ADR 0012 budget.
/// </summary>
public struct CompactTreeState
{
    public float NotchAngleRad;
    public float NotchDepthFraction;
    public float BackCutDepthFraction;
    public float HingeWidthFraction;
    public float FallTiltDeg;
    public float FallBearing;
}
