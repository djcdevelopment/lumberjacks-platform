namespace Lumberjacks.Contracts.Valheim;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

/// <summary>
/// The dispatch shape Valheim expects after a routed RPC reaches
/// <c>ZRoutedRpc.HandleRoutedRPC</c>.
/// </summary>
public enum ValheimRoutedRpcScope
{
    Global,
    Instance,
}

/// <summary>
/// What the cutover does when Valheim attempts to send the method.
/// <c>Route</c> methods cross the Lumberjacks reliable lane; <c>Supersede</c>
/// methods are consumed because another admitted lane owns their semantics.
/// </summary>
public enum ValheimRoutedRpcDisposition
{
    Route,
    Supersede,
}

/// <summary>
/// Why an RPC is present in the cutover admission set.
/// </summary>
public enum ValheimRoutedRpcPriority
{
    Harness,
    Runtime,
    P1,
    P2,
    P3,
    Superseded,
}

/// <summary>
/// One exact routed-RPC admission shared by the Valheim mod and Gateway.
/// Payload signatures use the names emitted by synthetic-baseline-extractor v2;
/// an empty string means a zero-argument RPC.
/// </summary>
public sealed class ValheimRoutedRpcAdmission
{
    readonly string[][] _payloadTypes;

    internal ValheimRoutedRpcAdmission(
        string name,
        ValheimRoutedRpcScope scope,
        ValheimRoutedRpcDisposition disposition,
        ValheimRoutedRpcPriority priority,
        string replacementLane,
        string requiredTargetKind,
        params string[] payloadSignatures)
    {
        if (disposition == ValheimRoutedRpcDisposition.Supersede
            && string.IsNullOrWhiteSpace(replacementLane))
            throw new ArgumentException(
                "superseded routed RPC requires a replacement lane",
                nameof(replacementLane));
        if (disposition == ValheimRoutedRpcDisposition.Route
            && !string.IsNullOrEmpty(replacementLane))
            throw new ArgumentException(
                "routed RPC cannot declare a replacement lane",
                nameof(replacementLane));
        Name = name;
        MethodHash = ValheimRoutedRpcAdmissions.StableHash(name);
        Scope = scope;
        Disposition = disposition;
        Priority = priority;
        ReplacementLane = replacementLane;
        var targetKinds = string.IsNullOrEmpty(requiredTargetKind)
            ? Array.Empty<string>()
            : requiredTargetKind.Split(new[] { '|' }, StringSplitOptions.None);
        var uniqueTargetKinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var targetKind in targetKinds)
            if (string.IsNullOrWhiteSpace(targetKind)
                || !uniqueTargetKinds.Add(targetKind))
                throw new ArgumentException(
                    "typed routed RPC target kinds must be non-empty and unique",
                    nameof(requiredTargetKind));
        TargetKinds = Array.AsReadOnly(targetKinds);
        RequiredTargetKind = TargetKinds.Count == 1
            ? TargetKinds[0]
            : string.Empty;
        PayloadSignatures = Array.AsReadOnly(
            payloadSignatures ?? Array.Empty<string>());
        _payloadTypes = new string[PayloadSignatures.Count][];
        for (var index = 0; index < PayloadSignatures.Count; index++)
        {
            var signature = PayloadSignatures[index];
            _payloadTypes[index] = signature.Length == 0
                ? Array.Empty<string>()
                : signature.Split(new[] { ',' }, StringSplitOptions.None);
        }
    }

    public string Name { get; }
    public int MethodHash { get; }
    public ValheimRoutedRpcScope Scope { get; }
    public ValheimRoutedRpcDisposition Disposition { get; }
    public ValheimRoutedRpcPriority Priority { get; }
    public string ReplacementLane { get; }
    /// <summary>
    /// Exact component classifications allowed to use this method contract.
    /// An empty collection means the envelope must be untyped. More than one
    /// entry is valid when Valheim registers one name/hash and payload shape on
    /// components whose runtime semantics are kept separate after admission.
    /// </summary>
    public ReadOnlyCollection<string> TargetKinds { get; }
    public string RequiredTargetKind { get; }
    public bool RequiresTypedTarget => TargetKinds.Count > 0;
    public ReadOnlyCollection<string> PayloadSignatures { get; }

    public bool AcceptsTarget(
        long targetZdoUserId,
        uint targetZdoId,
        string targetKind = "")
    {
        var hasCompleteTarget = targetZdoUserId != 0 && targetZdoId != 0;
        var hasNoTarget = targetZdoUserId == 0 && targetZdoId == 0;
        var scopeAccepted = Scope == ValheimRoutedRpcScope.Instance
            ? hasCompleteTarget
            : hasNoTarget;
        return scopeAccepted && AcceptsTargetKind(targetKind);
    }

    public bool AcceptsTargetKind(string targetKind)
    {
        var normalized = targetKind ?? string.Empty;
        if (TargetKinds.Count == 0)
            return normalized.Length == 0;
        foreach (var allowed in TargetKinds)
            if (string.Equals(allowed, normalized, StringComparison.Ordinal))
                return true;
        return false;
    }

    public bool AcceptsPayload(byte[] parameters) =>
        ValheimRoutedRpcPayloadValidator.Matches(_payloadTypes, parameters);
}

/// <summary>
/// The release-aligned admission contract. This source file is compiled into both
/// ComfyNetworkSense and Game.Contracts; neither side owns a private allow-list.
/// </summary>
public static class ValheimRoutedRpcAdmissions
{
    public const int MaximumParameterBytes = 32_768;

    public const string CutoverRequest =
        "ComfyNetworkSense_CutoverRoutedRequest";
    public const string CutoverResponse =
        "ComfyNetworkSense_CutoverRoutedResponse";
    public const string CutoverBroadcastRequest =
        "ComfyNetworkSense_CutoverRoutedBroadcastRequest";
    public const string CutoverBroadcast =
        "ComfyNetworkSense_CutoverRoutedBroadcast";
    public const string CutoverTargetReceipt =
        "ComfyNetworkSense_CutoverRoutedTargetReceipt";
    public const string CutoverResetCloth = "RPC_ResetCloth";
    public const string CutoverZdoJournalRequest =
        "ComfyNetworkSense_CutoverZdoJournalRequest";
    public const string ModAutoPort = "ComfyNetworkSense_AutoPort";
    public const string ModGameplayEvent = "ComfyNetworkSense_GameplayEvent";
    public const string ModServerPulse = "ComfyNetworkSense_ServerPulse";
    public const string ModShipSnapshot =
        "ComfyNetworkSense_ShipSnapshot";
    public const string ModSaddleSnapshot =
        "ComfyNetworkSense_SaddleSnapshot";
    public const string CutoverShipSpawnRequest =
        "ComfyNetworkSense_CutoverShipSpawnRequest";
    public const string CutoverShipSpawnResponse =
        "ComfyNetworkSense_CutoverShipSpawnResponse";
    public const string CutoverShipTransferRequest =
        "ComfyNetworkSense_CutoverShipTransferRequest";
    public const string CutoverShipTransferResponse =
        "ComfyNetworkSense_CutoverShipTransferResponse";
    public const string CutoverSaddleSpawnRequest =
        "ComfyNetworkSense_CutoverSaddleSpawnRequest";
    public const string CutoverSaddleSpawnResponse =
        "ComfyNetworkSense_CutoverSaddleSpawnResponse";
    public const string CutoverSaddleTransferRequest =
        "ComfyNetworkSense_CutoverSaddleTransferRequest";
    public const string CutoverSaddleTransferResponse =
        "ComfyNetworkSense_CutoverSaddleTransferResponse";
    public const string ShipTargetKind = "ship";
    public const string SaddleTargetKind = "saddle";

    static readonly ValheimRoutedRpcAdmission[] EntryArray = BuildEntries();
    static readonly ReadOnlyCollection<ValheimRoutedRpcAdmission> EntryView =
        Array.AsReadOnly(EntryArray);
    static readonly Dictionary<int, ValheimRoutedRpcAdmission> ByHash =
        BuildHashIndex(EntryArray);

    public static ReadOnlyCollection<ValheimRoutedRpcAdmission> Entries =>
        EntryView;

    public static bool TryGetByHash(
        int methodHash,
        out ValheimRoutedRpcAdmission admission) =>
        ByHash.TryGetValue(methodHash, out admission!);

    public static bool TryGet(
        string methodName,
        int methodHash,
        out ValheimRoutedRpcAdmission admission)
    {
        return TryGet(
            methodName,
            methodHash,
            string.Empty,
            out admission);
    }

    public static bool TryGet(
        string methodName,
        int methodHash,
        string targetKind,
        out ValheimRoutedRpcAdmission admission)
    {
        if (!TryGetByHash(methodHash, out admission))
            return false;
        return string.Equals(admission.Name, methodName, StringComparison.Ordinal)
            && admission.AcceptsTargetKind(targetKind);
    }

    public static bool AllowsEnvelope(
        string methodName,
        int methodHash,
        long targetZdoUserId,
        uint targetZdoId,
        byte[] parameters)
    {
        return AllowsEnvelope(
            methodName,
            methodHash,
            targetZdoUserId,
            targetZdoId,
            string.Empty,
            parameters);
    }

    public static bool AllowsEnvelope(
        string methodName,
        int methodHash,
        long targetZdoUserId,
        uint targetZdoId,
        string targetKind,
        byte[] parameters)
    {
        return parameters != null
            && parameters.Length <= MaximumParameterBytes
            && TryGet(
                methodName,
                methodHash,
                targetKind ?? string.Empty,
                out var admission)
            && admission.AcceptsTarget(
                targetZdoUserId,
                targetZdoId,
                targetKind ?? string.Empty)
            && admission.AcceptsPayload(parameters);
    }

    /// <summary>
    /// Validates an envelope that is allowed to cross the generic routed lane.
    /// Replacement-owned methods remain recognizable and shape-checked, but can
    /// never be injected through the Gateway or client inbound queue.
    /// </summary>
    public static bool AllowsRoutedEnvelope(
        string methodName,
        int methodHash,
        long targetZdoUserId,
        uint targetZdoId,
        byte[] parameters)
    {
        return AllowsRoutedEnvelope(
            methodName,
            methodHash,
            targetZdoUserId,
            targetZdoId,
            string.Empty,
            parameters);
    }

    public static bool AllowsRoutedEnvelope(
        string methodName,
        int methodHash,
        long targetZdoUserId,
        uint targetZdoId,
        string targetKind,
        byte[] parameters)
    {
        return AllowsEnvelope(
                methodName,
                methodHash,
                targetZdoUserId,
                targetZdoId,
                targetKind,
                parameters)
            && TryGet(
                methodName,
                methodHash,
                targetKind ?? string.Empty,
                out var admission)
            && admission.Disposition == ValheimRoutedRpcDisposition.Route;
    }

    /// <summary>
    /// Valheim's stable string hash, copied from the game implementation. This is
    /// intentionally part of the shared wire contract rather than a Gateway helper.
    /// </summary>
    public static int StableHash(string value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));

        unchecked
        {
            var first = 5381;
            var second = first;
            for (var index = 0;
                 index < value.Length && value[index] != 0;
                 index += 2)
            {
                first = ((first << 5) + first) ^ value[index];
                if (index == value.Length - 1 || value[index + 1] == 0)
                    break;
                second = ((second << 5) + second) ^ value[index + 1];
            }
            return first + second * 1566083941;
        }
    }

    static Dictionary<int, ValheimRoutedRpcAdmission> BuildHashIndex(
        ValheimRoutedRpcAdmission[] entries)
    {
        var result = new Dictionary<int, ValheimRoutedRpcAdmission>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!names.Add(entry.Name))
                throw new InvalidOperationException(
                    "duplicate routed RPC admission name: " + entry.Name);
            if (result.ContainsKey(entry.MethodHash))
                throw new InvalidOperationException(
                    "routed RPC admission hash collision: " + entry.Name);
            result.Add(entry.MethodHash, entry);
        }
        return result;
    }

    static ValheimRoutedRpcAdmission[] BuildEntries() =>
        new[]
        {
            Harness(CutoverRequest, ValheimRoutedRpcScope.Global, "ZPackage"),
            Harness(CutoverResponse, ValheimRoutedRpcScope.Global, "ZPackage"),
            Harness(CutoverBroadcastRequest, ValheimRoutedRpcScope.Global, "ZPackage"),
            Harness(CutoverBroadcast, ValheimRoutedRpcScope.Global, "ZPackage"),
            Harness(CutoverTargetReceipt, ValheimRoutedRpcScope.Global, "ZPackage"),
            Harness(CutoverResetCloth, ValheimRoutedRpcScope.Instance, ""),
            Harness(CutoverZdoJournalRequest, ValheimRoutedRpcScope.Global, "ZPackage"),
            Harness(CutoverShipSpawnRequest, ValheimRoutedRpcScope.Global, "ZPackage"),
            Harness(CutoverShipSpawnResponse, ValheimRoutedRpcScope.Global, "ZPackage"),
            Harness(CutoverShipTransferRequest, ValheimRoutedRpcScope.Global, "ZPackage"),
            Harness(CutoverShipTransferResponse, ValheimRoutedRpcScope.Global, "ZPackage"),
            Harness(CutoverSaddleSpawnRequest, ValheimRoutedRpcScope.Global, "ZPackage"),
            Harness(CutoverSaddleSpawnResponse, ValheimRoutedRpcScope.Global, "ZPackage"),
            Harness(CutoverSaddleTransferRequest, ValheimRoutedRpcScope.Global, "ZPackage"),
            Harness(CutoverSaddleTransferResponse, ValheimRoutedRpcScope.Global, "ZPackage"),

            Runtime(ModAutoPort, ValheimRoutedRpcScope.Global, "ZPackage"),
            Runtime(ModGameplayEvent, ValheimRoutedRpcScope.Global, "ZPackage"),
            Runtime(ModServerPulse, ValheimRoutedRpcScope.Global, "ZPackage"),
            Runtime(ModShipSnapshot, ValheimRoutedRpcScope.Global, "ZPackage"),
            Runtime(ModSaddleSnapshot, ValheimRoutedRpcScope.Global, "ZPackage"),

            // These native calls still occur during normal Valheim bookkeeping,
            // but their semantics are already owned by the named replacement lane.
            // Recognizing and consuming them closes the former outbound fallback
            // without reintroducing duplicate delivery through the generic router.
            Superseded("DestroyZDO", ValheimRoutedRpcScope.Global,
                "zdo_journal", "ZPackage"),
            Superseded("GlobalKeys", ValheimRoutedRpcScope.Global,
                "world_zone_descriptor", "List`1"),
            Superseded("LocationIcons", ValheimRoutedRpcScope.Global,
                "world_zone_descriptor", "ZPackage"),
            Superseded("Ping", ValheimRoutedRpcScope.Global,
                "logical_peer_session", "Single"),
            Superseded("Pong", ValheimRoutedRpcScope.Global,
                "logical_peer_session", "Single"),
            Superseded("RemoveGlobalKey", ValheimRoutedRpcScope.Global,
                "world_zone_descriptor", "String"),
            Superseded("RequestZDO", ValheimRoutedRpcScope.Global,
                "zdo_journal", "ZDOID"),
            Superseded("SetGlobalKey", ValheimRoutedRpcScope.Global,
                "world_zone_descriptor", "String"),

            // The first poison-armed C10 candidate run observed these during the
            // ordinary C8 movement/combat/event window. Per the breadth audit, a
            // normal-play trip reopens the row instead of leaving it behind fallback.
            P2("FlashShield", ValheimRoutedRpcScope.Instance, ""),
            P2("RPC_DamageText", ValheimRoutedRpcScope.Global, "ZPackage"),
            P2("RPC_HealthChanged", ValheimRoutedRpcScope.Instance, "Single"),
            P2("RPC_UpdateMaterial", ValheimRoutedRpcScope.Instance, "Int32"),
            P2("SetEvent", ValheimRoutedRpcScope.Global,
                "String,Single,Vector3"),
            P2("Step", ValheimRoutedRpcScope.Instance, "Int32,Vector3"),

            // Optional gameplay can legitimately invoke this on a remotely owned
            // player. Harpoon status processing debits the remote attacker, and an
            // authoritative fishing float can debit a rod owner held by another
            // peer. Keep the exact one-float contract instead of allowing those
            // features to fall back to native transport.
            P3("UseStamina", ValheimRoutedRpcScope.Instance, "Single"),

            // Ship control shares three native method hashes with Sadle, whose
            // grant changes ZDO ownership and uses a different identity. These
            // entries are therefore invalid unless the sender classifies the
            // target as a Ship and the receiver independently resolves that
            // component before dispatch. An untyped generic envelope remains
            // rejected even though the payload itself has the right shape.
            TypedP2("RequestControl", ValheimRoutedRpcScope.Instance,
                ShipTargetKind + "|" + SaddleTargetKind, "Int64"),
            TypedP2("ReleaseControl", ValheimRoutedRpcScope.Instance,
                ShipTargetKind + "|" + SaddleTargetKind, "Int64"),
            TypedP2("RequestRespons", ValheimRoutedRpcScope.Instance,
                ShipTargetKind + "|" + SaddleTargetKind, "Boolean"),
            TypedP2("Stop", ValheimRoutedRpcScope.Instance,
                ShipTargetKind, ""),
            TypedP2("Forward", ValheimRoutedRpcScope.Instance,
                ShipTargetKind, ""),
            TypedP2("Backward", ValheimRoutedRpcScope.Instance,
                ShipTargetKind, ""),
            TypedP2("Rudder", ValheimRoutedRpcScope.Instance,
                ShipTargetKind, "Single"),

            // Sadle (the spelling used by Valheim) shares its three control
            // handshake hashes with ShipControlls but transfers the creature's
            // ZDO owner to the rider and identifies that rider by session/ZDO
            // user id. It therefore requires a separate target-resolved
            // contract even though the serialized payload shapes collide.
            TypedP2("Controls", ValheimRoutedRpcScope.Instance,
                SaddleTargetKind, "Vector3,Int32,Single"),
            TypedP2("RemoveSaddle", ValheimRoutedRpcScope.Instance,
                SaddleTargetKind, "Vector3"),

            P1("ChatMessage", ValheimRoutedRpcScope.Global,
                "Vector3,Int32,UserInfo,String"),
            P1("ShowMessage", ValheimRoutedRpcScope.Global, "Int32,String"),
            P1("SleepStart", ValheimRoutedRpcScope.Global, ""),
            P1("SleepStop", ValheimRoutedRpcScope.Global, ""),

            P1("RPC_Damage", ValheimRoutedRpcScope.Instance,
                "HitData", "HitData,Int32"),
            P1("Hit", ValheimRoutedRpcScope.Instance, "HitData,Int32"),
            P1("ApplyOperation", ValheimRoutedRpcScope.Instance, "ZPackage"),
            P1("UseDoor", ValheimRoutedRpcScope.Instance, "Boolean"),
            P1("RequestOpen", ValheimRoutedRpcScope.Instance, "Int64"),
            P1("OpenRespons", ValheimRoutedRpcScope.Instance, "Boolean"),
            P1("RequestTakeAll", ValheimRoutedRpcScope.Instance, "Int64"),
            P1("TakeAllRespons", ValheimRoutedRpcScope.Instance, "Boolean"),
            P1("RPC_RequestStack", ValheimRoutedRpcScope.Instance, "Int64"),
            P1("RPC_StackResponse", ValheimRoutedRpcScope.Instance, "Boolean"),
            P1("RPC_MakePiece", ValheimRoutedRpcScope.Instance, ""),
            P1("Pick", ValheimRoutedRpcScope.Instance, ""),
            P1("RPC_Pick", ValheimRoutedRpcScope.Instance, "Int32"),
            P1("RPC_SetPicked", ValheimRoutedRpcScope.Instance, "Boolean"),
            P1("RPC_AddFuel", ValheimRoutedRpcScope.Instance, ""),
            P1("RPC_AddFuelAmount", ValheimRoutedRpcScope.Instance, "Single"),
            P1("RPC_AddItem", ValheimRoutedRpcScope.Instance, "String"),
            P1("RPC_AddOre", ValheimRoutedRpcScope.Instance, "String"),
            P1("RPC_AddStatusEffect", ValheimRoutedRpcScope.Instance,
                "Int32,Boolean,Int32,Single"),
            P1("RPC_EmptyProcessed", ValheimRoutedRpcScope.Instance, ""),
            P1("RPC_Extract", ValheimRoutedRpcScope.Instance, ""),
            P1("RPC_Heal", ValheimRoutedRpcScope.Instance, "Single,Boolean"),
            P1("RPC_RemoveDoneItem", ValheimRoutedRpcScope.Instance,
                "Vector3,Int32"),
            P1("RPC_Remove", ValheimRoutedRpcScope.Instance, "Boolean"),
            P1("RPC_Repair", ValheimRoutedRpcScope.Instance, ""),
            P1("RPC_Stagger", ValheimRoutedRpcScope.Instance, "Vector3"),
            P1("RPC_TeleportTo", ValheimRoutedRpcScope.Instance,
                "Vector3,Quaternion,Boolean"),
            P1("Message", ValheimRoutedRpcScope.Instance,
                "Int32,String,Int32"),
            P1("Say", ValheimRoutedRpcScope.Instance,
                "Int32,UserInfo,String"),
        };

    static ValheimRoutedRpcAdmission Harness(
        string name,
        ValheimRoutedRpcScope scope,
        params string[] payloadSignatures) =>
        new(
            name,
            scope,
            ValheimRoutedRpcDisposition.Route,
            ValheimRoutedRpcPriority.Harness,
            string.Empty,
            string.Empty,
            payloadSignatures);

    static ValheimRoutedRpcAdmission Runtime(
        string name,
        ValheimRoutedRpcScope scope,
        params string[] payloadSignatures) =>
        new(
            name,
            scope,
            ValheimRoutedRpcDisposition.Route,
            ValheimRoutedRpcPriority.Runtime,
            string.Empty,
            string.Empty,
            payloadSignatures);

    static ValheimRoutedRpcAdmission P1(
        string name,
        ValheimRoutedRpcScope scope,
        params string[] payloadSignatures) =>
        new(
            name,
            scope,
            ValheimRoutedRpcDisposition.Route,
            ValheimRoutedRpcPriority.P1,
            string.Empty,
            string.Empty,
            payloadSignatures);

    static ValheimRoutedRpcAdmission P2(
        string name,
        ValheimRoutedRpcScope scope,
        params string[] payloadSignatures) =>
        new(
            name,
            scope,
            ValheimRoutedRpcDisposition.Route,
            ValheimRoutedRpcPriority.P2,
            string.Empty,
            string.Empty,
            payloadSignatures);

    static ValheimRoutedRpcAdmission TypedP2(
        string name,
        ValheimRoutedRpcScope scope,
        string requiredTargetKind,
        params string[] payloadSignatures) =>
        new(
            name,
            scope,
            ValheimRoutedRpcDisposition.Route,
            ValheimRoutedRpcPriority.P2,
            string.Empty,
            requiredTargetKind,
            payloadSignatures);

    static ValheimRoutedRpcAdmission P3(
        string name,
        ValheimRoutedRpcScope scope,
        params string[] payloadSignatures) =>
        new(
            name,
            scope,
            ValheimRoutedRpcDisposition.Route,
            ValheimRoutedRpcPriority.P3,
            string.Empty,
            string.Empty,
            payloadSignatures);

    static ValheimRoutedRpcAdmission Superseded(
        string name,
        ValheimRoutedRpcScope scope,
        string replacementLane,
        params string[] payloadSignatures) =>
        new(
            name,
            scope,
            ValheimRoutedRpcDisposition.Supersede,
            ValheimRoutedRpcPriority.Superseded,
            replacementLane,
            string.Empty,
            payloadSignatures);
}

/// <summary>
/// Allocation-free reader for the subset of Valheim's ZRpc/ZPackage binary format used
/// by the admitted surface. A payload must match one extracted signature exactly; short,
/// malformed, or trailing bytes fail closed before reflection dispatch.
/// </summary>
static class ValheimRoutedRpcPayloadValidator
{
    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool Matches(string[][] signatures, byte[] parameters)
    {
        if (signatures == null || parameters == null)
            return false;

        foreach (var signature in signatures)
        {
            var offset = 0;
            var valid = true;
            foreach (var type in signature)
            {
                if (!TryConsume(type, parameters, ref offset))
                {
                    valid = false;
                    break;
                }
            }
            if (valid && offset == parameters.Length)
                return true;
        }
        return false;
    }

    static bool TryConsume(string type, byte[] data, ref int offset)
    {
        switch (type)
        {
            case "Boolean":
                if (!CanConsume(data, offset, 1) || data[offset] > 1)
                    return false;
                offset++;
                return true;
            case "Int32":
            case "Single":
                return ConsumeFixed(data, ref offset, 4);
            case "Int64":
                return ConsumeFixed(data, ref offset, 8);
            case "Vector3":
                return ConsumeFixed(data, ref offset, 12);
            case "Quaternion":
                return ConsumeFixed(data, ref offset, 16);
            case "String":
                return ConsumeString(data, ref offset);
            case "List`1":
                return ConsumeStringList(data, ref offset);
            case "ZPackage":
                return ConsumePackage(data, ref offset);
            case "ZDOID":
                return ConsumeFixed(data, ref offset, 12);
            case "UserInfo":
                return ConsumeString(data, ref offset)
                    && ConsumeString(data, ref offset);
            case "HitData":
                return ConsumeHitData(data, ref offset);
            default:
                return false;
        }
    }

    static bool ConsumeHitData(byte[] data, ref int offset)
    {
        if (!TryReadUInt16(data, ref offset, out var flags))
            return false;

        for (var bit = 0; bit <= 10; bit++)
            if ((flags & (1 << bit)) != 0
                && !ConsumeFixed(data, ref offset, 4))
                return false;

        if (!ConsumeFixed(data, ref offset, 2))
            return false;
        for (var bit = 11; bit <= 13; bit++)
            if ((flags & (1 << bit)) != 0
                && !ConsumeFixed(data, ref offset, 4))
                return false;

        if (!CanConsume(data, offset, 1) || (data[offset] & 0xf0) != 0)
            return false;
        offset++;

        if (!ConsumeFixed(data, ref offset, 12)
            || !ConsumeFixed(data, ref offset, 12)
            || !ConsumeFixed(data, ref offset, 4))
            return false;

        if ((flags & (1 << 14)) != 0
            && !ConsumeFixed(data, ref offset, 12))
            return false;
        if (!ConsumeFixed(data, ref offset, 2))
            return false;
        if ((flags & (1 << 15)) != 0
            && !ConsumeFixed(data, ref offset, 4))
            return false;

        return ConsumeChar(data, ref offset)
            && ConsumeFixed(data, ref offset, 4)
            && ConsumeFixed(data, ref offset, 2)
            && ConsumeFixed(data, ref offset, 1)
            && ConsumeFixed(data, ref offset, 1)
            && ConsumeFixed(data, ref offset, 4)
            && ConsumeFixed(data, ref offset, 4);
    }

    static bool ConsumePackage(byte[] data, ref int offset)
    {
        if (!TryReadInt32(data, ref offset, out var length) || length < 0)
            return false;
        return ConsumeFixed(data, ref offset, length);
    }

    static bool ConsumeStringList(byte[] data, ref int offset)
    {
        if (!TryReadInt32(data, ref offset, out var count)
            || count < 0
            // Every serialized string consumes at least its one-byte length.
            // This also bounds CPU for a malicious count before the loop begins.
            || count > data.Length - offset)
            return false;
        for (var index = 0; index < count; index++)
            if (!ConsumeString(data, ref offset))
                return false;
        return true;
    }

    static bool ConsumeString(byte[] data, ref int offset)
    {
        if (!TryRead7BitEncodedInt(data, ref offset, out var byteCount)
            || byteCount < 0
            || !CanConsume(data, offset, byteCount))
            return false;
        try
        {
            StrictUtf8.GetCharCount(data, offset, byteCount);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        offset += byteCount;
        return true;
    }

    static bool ConsumeChar(byte[] data, ref int offset)
    {
        if (!CanConsume(data, offset, 1))
            return false;
        var first = data[offset];
        var byteCount = first < 0x80
            ? 1
            : first >= 0xc2 && first <= 0xdf
                ? 2
                : first >= 0xe0 && first <= 0xef
                    ? 3
                    : 0;
        if (byteCount == 0 || !CanConsume(data, offset, byteCount))
            return false;
        try
        {
            if (StrictUtf8.GetCharCount(data, offset, byteCount) != 1)
                return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        offset += byteCount;
        return true;
    }

    static bool TryRead7BitEncodedInt(
        byte[] data,
        ref int offset,
        out int value)
    {
        value = 0;
        for (var index = 0; index < 5; index++)
        {
            if (!CanConsume(data, offset, 1))
                return false;
            var current = data[offset++];
            if (index == 4 && (current & 0xf0) != 0)
                return false;
            value |= (current & 0x7f) << (index * 7);
            if ((current & 0x80) == 0)
                return true;
        }
        return false;
    }

    static bool TryReadUInt16(
        byte[] data,
        ref int offset,
        out ushort value)
    {
        value = 0;
        if (!CanConsume(data, offset, 2))
            return false;
        value = (ushort)(data[offset] | (data[offset + 1] << 8));
        offset += 2;
        return true;
    }

    static bool TryReadInt32(byte[] data, ref int offset, out int value)
    {
        value = 0;
        if (!CanConsume(data, offset, 4))
            return false;
        value = data[offset]
            | (data[offset + 1] << 8)
            | (data[offset + 2] << 16)
            | (data[offset + 3] << 24);
        offset += 4;
        return true;
    }

    static bool ConsumeFixed(byte[] data, ref int offset, int count)
    {
        if (!CanConsume(data, offset, count))
            return false;
        offset += count;
        return true;
    }

    static bool CanConsume(byte[] data, int offset, int count) =>
        offset >= 0 && count >= 0 && offset <= data.Length - count;
}
