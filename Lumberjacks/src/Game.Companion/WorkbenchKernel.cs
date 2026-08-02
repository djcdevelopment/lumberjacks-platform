using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lumberjacks.Companion;

sealed class WorkbenchStore
{
    readonly object _lock = new();
    readonly string _root;
    readonly string _installationPath;
    readonly string _browserTokenPath;
    readonly string _runnerTokenPath;
    readonly string _externalRunnerTokenPath;
    readonly string _heartbeatPath;

    public WorkbenchStore(CompanionStateStore companionState)
    {
        _root = Path.Combine(companionState.DataDirectory, "workbench");
        Directory.CreateDirectory(_root);
        _installationPath = Path.Combine(_root, "installation.json");
        _browserTokenPath = Path.Combine(_root, "browser-token");
        _runnerTokenPath = Path.Combine(_root, "runner-token");
        _externalRunnerTokenPath = Environment.GetEnvironmentVariable("LUMBERJACKS_WORKBENCH_RUNNER_TOKEN_FILE") ?? "/run/workbench/runner-token";
        _heartbeatPath = Path.Combine(_root, "runner-heartbeat.json");
        _ = RunnerToken();
    }

    public string RootDirectory => _root;
    public string JobsDirectory => Path.Combine(_root, "jobs");
    public string RunnerTokenPath => _runnerTokenPath;
    public string HeartbeatPath => _heartbeatPath;

    public WorkbenchInstallation ReadInstallation()
    {
        lock (_lock)
        {
            if (!File.Exists(_installationPath)) return new WorkbenchInstallation();
            try
            {
                return JsonSerializer.Deserialize<WorkbenchInstallation>(File.ReadAllText(_installationPath), Json.Options)
                    ?? new WorkbenchInstallation();
            }
            catch
            {
                return new WorkbenchInstallation { LastError = "installation_state_unreadable" };
            }
        }
    }

    public void WriteInstallation(WorkbenchInstallation value)
    {
        lock (_lock) WriteAtomic(_installationPath, value);
    }

    public string BrowserToken()
    {
        lock (_lock)
        {
            if (File.Exists(_browserTokenPath)) return File.ReadAllText(_browserTokenPath).Trim();
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .Replace("+", "-").Replace("/", "_").TrimEnd('=');
            WriteAtomicText(_browserTokenPath, token + Environment.NewLine);
            return token;
        }
    }

    public string RunnerToken()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_externalRunnerTokenPath))
                {
                    var external = File.ReadAllText(_externalRunnerTokenPath).Trim();
                    if (!string.IsNullOrWhiteSpace(external)) return external;
                }
            }
            catch { /* a missing optional bind falls back to the persistent local store */ }
            if (File.Exists(_runnerTokenPath))
            {
                var local = File.ReadAllText(_runnerTokenPath).Trim();
                if (!string.IsNullOrWhiteSpace(local)) return local;
            }
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .Replace("+", "-").Replace("/", "_").TrimEnd('=');
            WriteAtomicText(_runnerTokenPath, token + Environment.NewLine);
            return token;
        }
    }

    public WorkbenchRunnerHeartbeat? ReadHeartbeat()
    {
        lock (_lock)
        {
            if (!File.Exists(_heartbeatPath)) return null;
            try
            {
                return JsonSerializer.Deserialize<WorkbenchRunnerHeartbeat>(File.ReadAllText(_heartbeatPath), Json.Options);
            }
            catch { return null; }
        }
    }

    public void WriteHeartbeat(WorkbenchRunnerHeartbeat value)
    {
        lock (_lock) WriteAtomic(_heartbeatPath, value);
    }

    static void WriteAtomic<T>(string path, T value) =>
        WriteAtomicText(path, JsonSerializer.Serialize(value, Json.Options));

    static void WriteAtomicText(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, value, new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }
}

sealed class WorkbenchJobStore
{
    readonly WorkbenchStore _store;
    readonly object _lock = new();

    public WorkbenchJobStore(WorkbenchStore store)
    {
        _store = store;
        Directory.CreateDirectory(_store.JobsDirectory);
    }

    public IReadOnlyList<WorkbenchJob> List(int limit = 25)
    {
        lock (_lock)
        {
            ReconcileExpiredLeases();
            return Directory.EnumerateFiles(_store.JobsDirectory, "job.json", SearchOption.AllDirectories)
                .Select(ReadFile)
                .Where(x => x is not null)
                .Cast<WorkbenchJob>()
                .OrderByDescending(x => x.CreatedUtc)
                .Take(Math.Clamp(limit, 1, 100))
                .ToList();
        }
    }

    public WorkbenchJob? Read(string jobId)
    {
        if (!IsSafeId(jobId)) return null;
        lock (_lock)
        {
            var path = Path.Combine(_store.JobsDirectory, jobId, "job.json");
            return File.Exists(path) ? ReadFile(path) : null;
        }
    }

    public WorkbenchJob Create(string capabilityId, string title, string profile, string target, JsonElement inputs)
    {
        lock (_lock)
        {
            var job = new WorkbenchJob
            {
                JobId = "job-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" +
                        Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(),
                CapabilityId = capabilityId,
                Title = title,
                Profile = profile,
                Target = target,
                Inputs = inputs.Clone(),
                State = "queued",
                CreatedUtc = DateTimeOffset.UtcNow,
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
            Write(job);
            AppendEvent(job.JobId, "queued", "job_created");
            return job;
        }
    }

    public WorkbenchJob Complete(WorkbenchJob job, string verdict, object result, string? reasonCode = null)
    {
        lock (_lock)
        {
            job.State = verdict.Equals("passed", StringComparison.OrdinalIgnoreCase) ? "succeeded" : "failed";
            job.Verdict = verdict;
            job.ReasonCode = reasonCode;
            job.UpdatedUtc = DateTimeOffset.UtcNow;
            job.CompletedUtc = job.UpdatedUtc;
            job.LeaseExpiresUtc = null;
            job.ReceiptPath = Path.Combine(_store.JobsDirectory, job.JobId, "receipt.json");
            Write(job);
            var receipt = new WorkbenchReceipt
            {
                SchemaVersion = 1,
                EventType = "workbench.job_receipt",
                JobId = job.JobId,
                CapabilityId = job.CapabilityId,
                Profile = job.Profile,
                Target = job.Target,
                Verdict = verdict,
                ReasonCode = reasonCode,
                CreatedUtc = job.CreatedUtc,
                CompletedUtc = job.CompletedUtc,
                Result = result,
                Artifacts = job.Artifacts,
                Source = new
                {
                    companion_version = CompanionVersion.Value,
                    source_revision = CompanionVersion.SourceRevision,
                    source_branch = CompanionVersion.SourceBranch,
                    source_dirty = CompanionVersion.SourceDirty,
                    image = CompanionVersion.Image,
                },
                EvidenceBoundary = "local_workbench_job",
            };
            WriteAtomic(Path.Combine(_store.JobsDirectory, job.JobId, "receipt.json"), receipt);
            AppendEvent(job.JobId, job.State, verdict);
            return job;
        }
    }

    public WorkbenchJob? Cancel(string jobId)
    {
        lock (_lock)
        {
            var job = Read(jobId);
            if (job is null || job.State is not ("queued" or "leased")) return null;
            job.State = "cancelled";
            job.Verdict = "cancelled";
            job.ReasonCode = "operator_cancelled_before_execution";
            job.UpdatedUtc = DateTimeOffset.UtcNow;
            job.CompletedUtc = job.UpdatedUtc;
            job.LeaseExpiresUtc = null;
            Write(job);
            AppendEvent(job.JobId, job.State, job.ReasonCode);
            return job;
        }
    }

    public WorkbenchJob? AddArtifact(string jobId, WorkbenchArtifact artifact)
    {
        lock (_lock)
        {
            var job = Read(jobId);
            if (job is null || job.State is "succeeded" or "failed" or "cancelled" or "interrupted") return null;
            job.Artifacts ??= new List<WorkbenchArtifact>();
            job.Artifacts.Add(artifact);
            job.UpdatedUtc = DateTimeOffset.UtcNow;
            Write(job);
            AppendEvent(job.JobId, job.State, "artifact_registered");
            return job;
        }
    }

    public WorkbenchJob? ClaimNext(string runnerId)
    {
        lock (_lock)
        {
            ReconcileExpiredLeases();
            var job = List(1).FirstOrDefault(x => x.State == "queued");
            if (job is null) return null;
            job.State = "leased";
            job.RunnerId = runnerId;
            job.LeaseExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(90);
            job.UpdatedUtc = DateTimeOffset.UtcNow;
            Write(job);
            AppendEvent(job.JobId, "leased", "runner_lease_acquired");
            return job;
        }
    }

    public WorkbenchJob? UpdateState(string jobId, string state, string reason)
    {
        lock (_lock)
        {
            var job = Read(jobId);
            if (job is null) return null;
            if (state is not ("leased" or "running" or "waiting_dependency" or "waiting_human" or "cancelling")) return null;
            var now = DateTimeOffset.UtcNow;
            job.State = state;
            job.ReasonCode = reason;
            job.UpdatedUtc = now;
            // A runner event is also proof that the owning runner is alive. Long,
            // bounded jobs (notably the rendered multi-machine lane) renew their
            // short crash-detection lease while the child process is active.
            if (state is "leased" or "running") job.LeaseExpiresUtc = now.AddSeconds(90);
            Write(job);
            AppendEvent(job.JobId, state, reason);
            return job;
        }
    }

    public WorkbenchJob? WaitForHuman(string jobId, string reason, JsonElement? machineResult = null)
    {
        lock (_lock)
        {
            var job = Read(jobId);
            if (job is null || job.State is not ("leased" or "running")) return null;
            job.State = "waiting_human";
            job.ReasonCode = reason;
            job.UpdatedUtc = DateTimeOffset.UtcNow;
            job.LeaseExpiresUtc = null;
            job.MachineResult = machineResult is { ValueKind: not JsonValueKind.Undefined } value ? value.Clone() : null;
            Write(job);
            AppendEvent(job.JobId, job.State, reason);
            return job;
        }
    }

    public WorkbenchJob? CompleteObservation(string jobId, WorkbenchHumanObservation observation)
    {
        lock (_lock)
        {
            var job = Read(jobId);
            if (job is null || job.State != "waiting_human") return null;
            var result = new
            {
                capability = job.CapabilityId,
                machine_result = job.MachineResult,
                human_observation = observation,
                evidence_boundary = "machine_evidence_plus_operator_observation",
            };
            return Complete(job, observation.Outcome.Equals("pass", StringComparison.OrdinalIgnoreCase) ? "passed" : "failed", result,
                observation.Outcome.Equals("pass", StringComparison.OrdinalIgnoreCase) ? "human_observation_recorded" : "human_observation_failed");
        }
    }

    public string? ReceiptPath(string jobId)
    {
        if (!IsSafeId(jobId)) return null;
        var path = Path.Combine(_store.JobsDirectory, jobId, "receipt.json");
        return File.Exists(path) ? path : null;
    }

    public string? EventsPath(string jobId)
    {
        if (!IsSafeId(jobId)) return null;
        return Path.Combine(_store.JobsDirectory, jobId, "events.jsonl");
    }

    public void AppendEvent(string jobId, string state, string reason)
    {
        if (!IsSafeId(jobId)) return;
        var directory = Path.Combine(_store.JobsDirectory, jobId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "events.jsonl");
        var item = new WorkbenchJobEvent
        {
            Sequence = File.Exists(path) ? File.ReadLines(path).LongCount() + 1 : 1,
            JobId = jobId,
            State = state,
            ReasonCode = reason,
            ObservedUtc = DateTimeOffset.UtcNow,
        };
        File.AppendAllText(path, JsonSerializer.Serialize(item, Json.CompactOptions) + Environment.NewLine, new UTF8Encoding(false));
    }

    void ReconcileExpiredLeases()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var job in Directory.EnumerateFiles(_store.JobsDirectory, "job.json", SearchOption.AllDirectories)
                     .Select(ReadFile).Where(x => x is not null).Cast<WorkbenchJob>()
                     .Where(x => x.State is "leased" or "running" && x.LeaseExpiresUtc is not null && x.LeaseExpiresUtc < now))
        {
            job.State = "interrupted";
            job.ReasonCode = "runner_lease_expired";
            job.UpdatedUtc = now;
            job.CompletedUtc = now;
            Write(job);
            AppendEvent(job.JobId, job.State, job.ReasonCode);
        }
    }

    void Write(WorkbenchJob job)
    {
        var directory = Path.Combine(_store.JobsDirectory, job.JobId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "job.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(job, Json.Options), new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }

    static WorkbenchJob? ReadFile(string path)
    {
        try { return JsonSerializer.Deserialize<WorkbenchJob>(File.ReadAllText(path), Json.Options); }
        catch { return null; }
    }

    static void WriteAtomic<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json.Options), new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }

    static bool IsSafeId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 100 &&
        value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
}

sealed class WorkbenchRegistry
{
    static readonly IReadOnlyList<WorkbenchCapability> All = new[]
    {
        new WorkbenchCapability("explore.system.inspect", "Inspect local system", "Explore", "read_only", "none", new[] { "Explore", "Admin", "Dev", "Lab", "Production" }, new[] { "local", "AM4", "OMEN", "i5", "P7" }, true, "local_projection", "{}", "operator_safe", true),
        new WorkbenchCapability("explore.evidence.list", "Browse receipts and evidence", "Explore", "read_only", "none", new[] { "Explore", "Admin", "Dev", "Lab", "Production" }, new[] { "local" }, true, "local_projection", "{}", "operator_safe", true),
        new WorkbenchCapability("build.mod.release", "Build the mod in Docker", "Build", "local_artifact", "none", new[] { "Dev", "Lab" }, new[] { "local" }, true, "host_runner", "{\"type\":\"object\"}", "private_local", true),
        new WorkbenchCapability("build.rendered.c6-role-reversal", "Run rendered C6 role reversal", "Build", "player_impacting", "watch", new[] { "Lab" }, new[] { "AM4", "OMEN", "i5" }, false, "host_runner", "{\"type\":\"object\",\"properties\":{\"human_observation\":{\"description\":\"submitted after the machine run reaches waiting_human\"}}}", "private_run", false),
        new WorkbenchCapability("operate.mod.check", "Check admitted mod update", "Operate", "read_only", "none", new[] { "Admin", "Dev", "Lab", "Production" }, new[] { "local", "P7" }, true, "companion", "{}", "private_local", true),
        new WorkbenchCapability("operate.mod.install", "Install admitted mod update", "Operate", "player_impacting", "join_once", new[] { "Admin", "Dev", "Lab", "Production" }, new[] { "OMEN", "i5" }, false, "host_runner", "{\"type\":\"object\",\"required\":[\"game_closed_confirmed\"]}", "private_local", true),
        new WorkbenchCapability("operate.mod.rollback", "Rollback latest mod update", "Operate", "player_impacting", "join_once", new[] { "Admin", "Dev", "Lab", "Production" }, new[] { "OMEN", "i5" }, false, "host_runner", "{\"type\":\"object\",\"required\":[\"game_closed_confirmed\"]}", "private_local", true),
        new WorkbenchCapability("operate.transport.capture", "Capture bounded transport evidence", "Operate", "read_only", "watch", new[] { "Admin", "Dev", "Lab", "Production" }, new[] { "OMEN", "i5", "AM4" }, false, "host_runner", "{\"type\":\"object\",\"properties\":{\"duration_seconds\":{\"maximum\":180}}}", "private_run", true),
        new WorkbenchCapability("recover.snapshot.create", "Capture a redacted Workbench snapshot", "Recover", "local_artifact", "none", new[] { "Explore", "Admin", "Dev", "Lab", "Production" }, new[] { "local" }, true, "companion", "{}", "private_local", true),
        new WorkbenchCapability("recover.support.export", "Export a public-safe support capsule", "Recover", "local_artifact", "none", new[] { "Explore", "Admin", "Dev", "Lab", "Production" }, new[] { "local" }, false, "host_runner", "{}", "public_safe", true),
        new WorkbenchCapability("recover.recreate.verify", "Verify safe container recreate", "Recover", "destructive_recovery", "operator_recovery", new[] { "Admin", "Dev", "Lab", "Production" }, new[] { "local" }, false, "host_runner", "{\"type\":\"object\",\"required\":[\"confirm_recreate\"]}", "private_local", true),
    };

    public IReadOnlyList<WorkbenchCapabilityView> For(string profile, bool runnerReady = true, bool rollbackReady = true) => All
        .Select(capability => new WorkbenchCapabilityView
        {
            Id = capability.Id,
            Title = capability.Title,
            Intent = capability.Intent,
            SideEffect = capability.SideEffect,
            HumanTouch = capability.HumanTouch,
            EligibleProfiles = capability.EligibleProfiles,
            EligibleTargets = capability.EligibleTargets,
            ReadOnly = capability.ReadOnly,
            Runner = capability.Runner,
            InputSchema = capability.InputSchema,
            PrivacyClass = capability.PrivacyClass,
            SupportsCancellation = capability.SupportsCancellation,
            Eligible = capability.EligibleProfiles.Contains(profile, StringComparer.OrdinalIgnoreCase) &&
                (capability.Runner != "host_runner" || runnerReady) &&
                (capability.Id != "operate.mod.rollback" || rollbackReady),
            ReasonCode = !capability.EligibleProfiles.Contains(profile, StringComparer.OrdinalIgnoreCase)
                ? "profile_not_eligible"
                : capability.Runner == "host_runner" && !runnerReady
                    ? "runner_unavailable"
                    : capability.Id == "operate.mod.rollback" && !rollbackReady
                        ? "rollback_not_available"
                        : null,
        }).ToList();

    public WorkbenchCapability? Find(string id) => All.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

sealed class WorkbenchService
{
    readonly WorkbenchStore _store;
    readonly WorkbenchJobStore _jobs;
    readonly WorkbenchRegistry _registry;
    readonly ValheimLocator _locator;
    readonly CompanionStateStore _companionState;
    readonly GatewayClient _gateway;

    public WorkbenchService(WorkbenchStore store, WorkbenchJobStore jobs, WorkbenchRegistry registry,
        ValheimLocator locator, CompanionStateStore companionState, GatewayClient gateway)
    {
        _store = store;
        _jobs = jobs;
        _registry = registry;
        _locator = locator;
        _companionState = companionState;
        _gateway = gateway;
    }

    public WorkbenchInstallation Installation => _store.ReadInstallation();

    public string EffectiveProfile
    {
        get
        {
            // A fresh installation is always Explore. Launcher overrides are
            // authority-bearing profiles and become meaningful only after the
            // owner claims this local installation.
            if (!Installation.Claimed) return "Explore";
            var launch = NormalizeProfile(Environment.GetEnvironmentVariable("LUMBERJACKS_WORKBENCH_PROFILE"));
            if (launch is "Dev" or "Lab" or "Production") return launch;
            if (launch == "Admin" && Installation.Claimed) return "Admin";
            return "Admin";
        }
    }

    public async Task<object> ProjectionAsync(CancellationToken cancellationToken)
    {
        var installation = Installation;
        var profile = EffectiveProfile;
        var runnerReady = IsRunnerReady();
        var deploymentTask = _gateway.GetJson("/api/v0/telemetry/deployment", cancellationToken);
        var valheimTask = _gateway.GetJson("/api/v0/telemetry/valheim", cancellationToken);
        var cutoverTask = _gateway.GetJson("/api/v0/telemetry/cutover", cancellationToken);
        var motionTask = _gateway.GetJson("/live/valheim-motion", cancellationToken);
        var journalTask = _gateway.GetJson("/valheim/zdo-journal/status", cancellationToken);
        await Task.WhenAll(deploymentTask, valheimTask, cutoverTask, motionTask, journalTask);
        var jobs = _jobs.List();
        var active = jobs.FirstOrDefault(job => job.State is
            "queued" or "leased" or "running" or "waiting_dependency" or "waiting_human" or "cancelling");
        return new
        {
            schema_version = 1,
            event_type = "workbench.projection",
            generated_utc = DateTimeOffset.UtcNow,
            installation,
            profile = new { effective = profile, launch_override = Environment.GetEnvironmentVariable("LUMBERJACKS_WORKBENCH_PROFILE") ?? "Explore" },
            source = new
            {
                companion_version = CompanionVersion.Value,
                bootstrap_release = CompanionVersion.BootstrapRelease,
                source_revision = CompanionVersion.SourceRevision,
                source_branch = CompanionVersion.SourceBranch,
                source_dirty = CompanionVersion.SourceDirty,
                image = CompanionVersion.Image,
                gateway_url = GatewayClient.GatewayUrl,
            },
            privacy = new
            {
                browser_binding = "loopback_only",
                public_support = "explicit_redacted_export_only",
                local_live_display = new[] { "accepted_public_player_names" },
                exclusion_scope = "support_export_and_remote_bodies",
                excluded_by_default = new[] { "player_names", "steam_ids", "coordinates", "free_text", "secrets", "raw_remote_bodies" },
            },
            capabilities = _registry.For(profile, runnerReady, RollbackReady()),
            jobs,
            topology = Topology(profile),
            live = WorkbenchLiveStatus.Build(
                await deploymentTask,
                await valheimTask,
                await cutoverTask,
                await motionTask,
                await journalTask,
                active,
                GatewayClient.GatewayUrl),
        };
    }

    public object Capabilities() => new
    {
        schema_version = 1,
        effective_profile = EffectiveProfile,
        capabilities = _registry.For(EffectiveProfile, IsRunnerReady(), RollbackReady()),
    };

    public object Topology(string? profile = null)
    {
        profile ??= EffectiveProfile;
        var install = _locator.Find();
        var heartbeat = _store.ReadHeartbeat();
        var heartbeatReady = heartbeat is not null && heartbeat.ObservedUtc > DateTimeOffset.UtcNow.AddSeconds(-45);
        var gatewayTarget = ConfiguredGatewayTarget();
        var gatewayTitle = gatewayTarget switch
        {
            "local" => "Local Gateway",
            "P7" => "P7 Gateway",
            _ => "Remote Gateway",
        };
        var nodes = new List<WorkbenchNode>
        {
            new("workbench", "Workbench", "ready", "local", "human cockpit", "none", DateTimeOffset.UtcNow, "companion"),
            new("docker", "Docker", heartbeatReady ? "ready" : "waiting_dependency", "local", "container engine", "none", heartbeat?.ObservedUtc, "runner_heartbeat"),
            new("runner", "Host runner", heartbeatReady ? "ready" : "offline", "local", "allow-listed Windows execution", "none", heartbeat?.ObservedUtc, "runner_heartbeat"),
            new("valheim", "Valheim", install is null ? "not_configured" : ValheimLocator.IsRunning() ? "working" : "ready", "local", install is null ? "Valheim not found" : "local install", "none", DateTimeOffset.UtcNow, "valheim_locator"),
            new("dev-mcp", "Baseline Dev MCP", profile is "Dev" or "Lab" ? "ready" : "excluded", "local", profile is "Dev" or "Lab" ? "development/lab control plane" : "absent from this profile", "none", DateTimeOffset.UtcNow, "launch_profile"),
            new("gateway", gatewayTitle, heartbeat?.GatewayState ?? "waiting_dependency", gatewayTarget, "release, telemetry, and control edge", "none", heartbeat?.ObservedUtc, "configured_gateway"),
            new("am4", "AM4 server", heartbeat?.Am4State ?? "waiting_dependency", "AM4", "dedicated Valheim server", "watch", heartbeat?.ObservedUtc, "runner_heartbeat"),
            new("omen", "OMEN client", heartbeat?.OmenState ?? "waiting_dependency", "OMEN", "primary rendered client", "watch", heartbeat?.ObservedUtc, "runner_heartbeat"),
            new("i5", "i5 client", heartbeat?.I5State ?? "waiting_dependency", "i5", "second rendered client", "watch", heartbeat?.ObservedUtc, "runner_heartbeat"),
            new("p7", "P7/GCP", gatewayTarget == "P7" ? heartbeat?.GatewayState ?? "waiting_dependency" : "excluded", "P7", "promotion/release edge", "none", gatewayTarget == "P7" ? heartbeat?.ObservedUtc : DateTimeOffset.UtcNow, gatewayTarget == "P7" ? "configured_gateway" : "profile_boundary"),
        };
        var active = _jobs.List().FirstOrDefault(job => job.State is "queued" or "leased" or "running" or "waiting_dependency" or "waiting_human" or "cancelling");
        if (active is not null)
        {
            var targets = (_registry.Find(active.CapabilityId)?.EligibleTargets ?? new[] { active.Target })
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var waitingForHuman = active.State == "waiting_human";
            nodes = nodes.Select(node =>
            {
                var participates = node.Id == "workbench" ||
                    (!waitingForHuman && (node.Id == "runner" || targets.Contains(node.Id) || targets.Contains(node.Target)));
                return participates
                    ? node with { ActiveJobPhase = active.State, ActiveJobId = active.JobId }
                    : node;
            }).ToList();
        }
        return new { schema_version = 1, generated_utc = DateTimeOffset.UtcNow, active_job = active is null ? null : new { active.JobId, active.CapabilityId, active.State, active.Target }, nodes };
    }

    static string ConfiguredGatewayTarget()
    {
        if (!Uri.TryCreate(GatewayClient.GatewayUrl, UriKind.Absolute, out var uri)) return "remote";
        if (uri.IsLoopback || uri.Host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("gateway", StringComparison.OrdinalIgnoreCase)) return "local";
        return uri.Host.Equals("comfy-p7.duckdns.org", StringComparison.OrdinalIgnoreCase) ? "P7" : "remote";
    }

    public (bool Ok, string? Error, WorkbenchInstallation? Value) Claim(string? label)
    {
        if (!string.IsNullOrWhiteSpace(label) && (label.Length > 80 || label.Any(c => c is '<' or '>' or '\r' or '\n')))
            return (false, "owner_label_invalid", null);
        var installation = Installation;
        if (installation.Claimed) return (true, null, installation);
        installation.Claimed = true;
        installation.OwnerLabel = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        installation.ClaimedUtc = DateTimeOffset.UtcNow;
        installation.LastError = null;
        _store.WriteInstallation(installation);
        return (true, null, installation);
    }

    public (bool Ok, string? Error, WorkbenchJob? Job) Start(string capabilityId, string? target, JsonElement? inputs)
    {
        var capability = _registry.Find(capabilityId);
        if (capability is null) return (false, "capability_not_found", null);
        var profile = EffectiveProfile;
        if (!capability.EligibleProfiles.Contains(profile, StringComparer.OrdinalIgnoreCase))
            return (false, "profile_not_eligible", null);
        if (capability.Runner == "host_runner" && !IsRunnerReady())
            return (false, "runner_unavailable", null);
        if (capability.Id == "operate.mod.rollback" && !RollbackReady())
            return (false, "rollback_not_available", null);
        if (!Installation.Claimed && !capability.ReadOnly)
            return (false, "installation_claim_required", null);
        var resolvedTarget = string.IsNullOrWhiteSpace(target) ? capability.EligibleTargets[0] : target.Trim();
        if (!capability.EligibleTargets.Contains(resolvedTarget, StringComparer.OrdinalIgnoreCase))
            return (false, "target_not_eligible", null);
        var requestInputs = inputs is { ValueKind: not JsonValueKind.Undefined } value ? value : EmptyObject();
        if (requestInputs.ValueKind != JsonValueKind.Object) return (false, "inputs_object_required", null);
        if (requestInputs.GetRawText().Length > 65536) return (false, "inputs_too_large", null);
        if (!TryNormalizeInputs(capability.Id, requestInputs, out var safeInputs, out var inputError))
            return (false, inputError, null);
        var job = _jobs.Create(capability.Id, capability.Title, profile, resolvedTarget, safeInputs);

        if (capability.Id == "explore.system.inspect")
        {
            var result = new
            {
                profile,
                topology = Topology(profile),
                capability_count = _registry.For(profile, IsRunnerReady(), RollbackReady()).Count,
                freshness = "local_projection",
            };
            job = _jobs.Complete(job, "passed", result);
        }
        else if (capability.Id == "recover.snapshot.create")
        {
            var companionState = new CompanionStateStore();
            var snapshot = WorkbenchCatalog.Snapshot(WorkbenchCatalog.Read(), companionState.Read(), _locator.Find());
            var path = WorkbenchCatalog.WriteSnapshot(companionState, snapshot);
            job = _jobs.Complete(job, "passed", new { snapshot_name = Path.GetFileName(path), evidence_boundary = "redacted_workbench_snapshot" });
        }
        else if (capability.Id == "explore.evidence.list")
        {
            var evidence = _jobs.List(100).Select(x => new { x.JobId, x.CapabilityId, x.State, x.Verdict, x.ReasonCode, x.CreatedUtc, x.UpdatedUtc }).ToList();
            job = _jobs.Complete(job, "passed", new { evidence, evidence_boundary = "local_receipt_index" }, "evidence_index_ready");
        }
        return (true, null, job);
    }

    public WorkbenchJob? Cancel(string jobId) => _jobs.Cancel(jobId);
    public WorkbenchJob? ClaimNext(string runnerId) => _jobs.ClaimNext(runnerId);
    public WorkbenchJob? UpdateState(string jobId, string runnerId, string state, string reason) =>
        _jobs.Read(jobId) is { } job && RunnerOwns(job, runnerId) ? _jobs.UpdateState(jobId, state, reason) : null;
    public WorkbenchJob? CompleteFromRunner(string jobId, string runnerId, string verdict, JsonElement? result, string? reasonCode) =>
        _jobs.Read(jobId) is { } job && RunnerOwns(job, runnerId) && job.State is "leased" or "running" or "cancelling"
            ? _jobs.Complete(job, verdict, result is { ValueKind: not JsonValueKind.Undefined } value ? value : new { }, reasonCode)
            : null;
    public WorkbenchJob? WaitForHuman(string jobId, string runnerId, string reason, JsonElement? result = null) =>
        _jobs.Read(jobId) is { } job && RunnerOwns(job, runnerId) ? _jobs.WaitForHuman(jobId, reason, result) : null;
    public WorkbenchJob? CompleteObservation(string jobId, WorkbenchHumanObservation observation) => _jobs.CompleteObservation(jobId, observation);
    public WorkbenchJob? AddArtifact(string jobId, string runnerId, WorkbenchArtifact artifact) =>
        _jobs.Read(jobId) is { } job && RunnerOwns(job, runnerId) ? _jobs.AddArtifact(jobId, artifact) : null;
    public void WriteHeartbeat(WorkbenchRunnerHeartbeat heartbeat) => _store.WriteHeartbeat(heartbeat);
    public WorkbenchJob? ReadJob(string jobId) => _jobs.Read(jobId);
    public string? ReceiptPath(string jobId) => _jobs.ReceiptPath(jobId);

    public bool RunnerAuthenticated(HttpRequest request)
    {
        var token = request.Headers["X-Workbench-Runner-Token"].ToString();
        var expected = _store.RunnerToken();
        return !string.IsNullOrWhiteSpace(token) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected));
    }

    public bool BrowserMutationAllowed(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (!string.IsNullOrWhiteSpace(origin) && origin is not ("http://127.0.0.1:8080" or "http://localhost:8080")) return false;
        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite.Equals("cross-site", StringComparison.OrdinalIgnoreCase)) return false;
        var token = request.Headers["X-Workbench-Token"].ToString();
        var expected = _store.BrowserToken();
        return !string.IsNullOrWhiteSpace(token) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected));
    }

    public string BrowserToken() => _store.BrowserToken();

    bool IsRunnerReady() => _store.ReadHeartbeat() is { } heartbeat && heartbeat.ObservedUtc > DateTimeOffset.UtcNow.AddSeconds(-45);
    bool RollbackReady() => _companionState.Read().installed?.transaction_schema_version == 1;

    static JsonElement EmptyObject() => JsonDocument.Parse("{}").RootElement.Clone();

    static bool TryNormalizeInputs(string capabilityId, JsonElement input, out JsonElement normalized, out string error)
    {
        normalized = EmptyObject();
        error = "";
        var properties = input.EnumerateObject().ToList();
        switch (capabilityId)
        {
            case "recover.recreate.verify":
                if (properties.Count != 1 || !input.TryGetProperty("confirm_recreate", out var recreate) || recreate.ValueKind != JsonValueKind.True)
                {
                    error = "typed_recreate_confirmation_required";
                    return false;
                }
                normalized = JsonSerializer.SerializeToElement(new { confirm_recreate = true }, Json.Options);
                return true;
            case "operate.mod.install":
            case "operate.mod.rollback":
                if (properties.Count != 1 || !input.TryGetProperty("game_closed_confirmed", out var closed) || closed.ValueKind != JsonValueKind.True)
                {
                    error = "game_closed_confirmation_required";
                    return false;
                }
                normalized = JsonSerializer.SerializeToElement(new { game_closed_confirmed = true }, Json.Options);
                return true;
            case "operate.transport.capture":
                if (properties.Count == 0) return true;
                if (properties.Count != 1 || !input.TryGetProperty("duration_seconds", out var duration) ||
                    duration.ValueKind != JsonValueKind.Number || !duration.TryGetInt32(out var seconds) || seconds is < 5 or > 180)
                {
                    error = "capture_duration_out_of_range";
                    return false;
                }
                normalized = JsonSerializer.SerializeToElement(new { duration_seconds = seconds }, Json.Options);
                return true;
            default:
                if (properties.Count != 0)
                {
                    error = "input_field_not_allowed";
                    return false;
                }
                return true;
        }
    }

    static bool RunnerOwns(WorkbenchJob job, string runnerId) =>
        !string.IsNullOrWhiteSpace(runnerId) &&
        (string.IsNullOrWhiteSpace(job.RunnerId) || string.Equals(job.RunnerId, runnerId, StringComparison.Ordinal));

    static string NormalizeProfile(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "admin" => "Admin",
        "dev" => "Dev",
        "lab" => "Lab",
        "production" or "prod" => "Production",
        _ => "Explore",
    };
}

static class WorkbenchEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/workbench", async (WorkbenchService service, CancellationToken cancellationToken) =>
            Results.Json(await service.ProjectionAsync(cancellationToken), Json.Options));
        app.MapGet("/api/v1/workbench/installation", (WorkbenchService service) => Results.Json(service.Installation, Json.Options));
        app.MapGet("/api/v1/workbench/security", (WorkbenchService service) => Results.Ok(new { schema_version = 1, browser_token = service.BrowserToken() }));
        app.MapGet("/api/v1/workbench/capabilities", (WorkbenchService service) => Results.Json(service.Capabilities(), Json.Options));
        app.MapGet("/api/v1/workbench/topology", (WorkbenchService service) => Results.Json(service.Topology(), Json.Options));
        app.MapGet("/api/v1/workbench/jobs", (WorkbenchJobStore jobs) => Results.Json(new { schema_version = 1, jobs = jobs.List() }, Json.Options));
        app.MapGet("/api/v1/workbench/jobs/{jobId}", (string jobId, WorkbenchService service) =>
            service.ReadJob(jobId) is { } job ? Results.Json(job, Json.Options) : Results.NotFound(new { error = "job_not_found" }));
        app.MapGet("/api/v1/workbench/jobs/{jobId}/receipt", (string jobId, WorkbenchService service) =>
        {
            var path = service.ReceiptPath(jobId);
            return path is null ? Results.NotFound(new { error = "receipt_not_found" }) : Results.File(path, "application/json", Path.GetFileName(path));
        });
        app.MapGet("/api/v1/workbench/jobs/{jobId}/events", (string jobId, WorkbenchService service, WorkbenchJobStore jobs) =>
        {
            var job = service.ReadJob(jobId);
            if (job is null) return Results.NotFound(new { error = "job_not_found" });
            var eventsPath = jobs.EventsPath(jobId);
            if (!File.Exists(eventsPath)) return Results.Ok(new { schema_version = 1, events = Array.Empty<object>() });
            var rawText = File.ReadAllText(eventsPath).Trim();
            var rawEvents = string.IsNullOrWhiteSpace(rawText)
                ? new List<string>()
                : Regex.Split(rawText, @"(?<=\})\s+(?=\{)").Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
            var events = rawEvents.Select(line =>
            {
                try { return JsonDocument.Parse(line).RootElement.Clone(); }
                catch { return (JsonElement?)null; }
            }).Where(x => x.HasValue).Select(x => x!.Value).ToList();
            return Results.Json(new { schema_version = 1, events }, Json.Options);
        });
        app.MapPost("/api/v1/workbench/installation/claim", (HttpRequest request, ClaimRequest? body, WorkbenchService service) =>
        {
            if (!service.BrowserMutationAllowed(request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var result = service.Claim(body?.Label);
            return result.Ok ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
        });
        app.MapPost("/api/v1/workbench/capabilities/{capabilityId}/jobs", (string capabilityId, HttpRequest request, WorkbenchJobRequest? body, WorkbenchService service) =>
        {
            if (!service.BrowserMutationAllowed(request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var result = service.Start(capabilityId, body?.Target, body?.Inputs);
            return result.Ok ? Results.Accepted($"/api/v1/workbench/jobs/{result.Job!.JobId}", result.Job) : Results.BadRequest(new { error = result.Error });
        });
        app.MapPost("/api/v1/workbench/jobs/{jobId}/cancel", (string jobId, HttpRequest request, WorkbenchService service) =>
        {
            if (!service.BrowserMutationAllowed(request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var job = service.Cancel(jobId);
            return job is null ? Results.Conflict(new { error = "job_not_cancellable" }) : Results.Ok(job);
        });
        app.MapPost("/api/v1/workbench/jobs/{jobId}/observation", (string jobId, HttpRequest request, WorkbenchHumanObservation? body, WorkbenchService service) =>
        {
            if (!service.BrowserMutationAllowed(request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (body is null) return Results.BadRequest(new { error = "observation_required" });
            if (!WorkbenchHumanObservation.IsValid(body, out var error)) return Results.BadRequest(new { error });
            var job = service.CompleteObservation(jobId, body);
            return job is null ? Results.Conflict(new { error = "job_not_waiting_for_human" }) : Results.Ok(job);
        });
        app.MapPost("/api/v1/workbench/runner/heartbeat", (HttpRequest request, WorkbenchRunnerHeartbeat? body, WorkbenchService service) =>
        {
            if (!service.RunnerAuthenticated(request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (body is null) return Results.BadRequest(new { error = "heartbeat_required" });
            body.ObservedUtc = DateTimeOffset.UtcNow;
            service.WriteHeartbeat(body);
            return Results.Ok(new { ok = true, observed_utc = body.ObservedUtc });
        });
        app.MapGet("/api/v1/workbench/runner/jobs/next", (HttpRequest request, WorkbenchService service) =>
        {
            if (!service.RunnerAuthenticated(request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var runnerId = request.Headers["X-Workbench-Runner-Id"].ToString();
            return Results.Ok(new { job = service.ClaimNext(string.IsNullOrWhiteSpace(runnerId) ? "runner" : runnerId) });
        });
        app.MapPost("/api/v1/workbench/runner/jobs/{jobId}/events", (string jobId, HttpRequest request, WorkbenchRunnerEventRequest? body, WorkbenchService service) =>
        {
            if (!service.RunnerAuthenticated(request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (body is null) return Results.BadRequest(new { error = "event_required" });
            var runnerId = request.Headers["X-Workbench-Runner-Id"].ToString();
            return service.UpdateState(jobId, runnerId, body.State, body.ReasonCode) is { } job ? Results.Ok(job) : Results.NotFound(new { error = "job_not_found_or_state_invalid" });
        });
        app.MapPost("/api/v1/workbench/runner/jobs/{jobId}/waiting-human", (string jobId, HttpRequest request, WorkbenchRunnerWaitingHumanRequest? body, WorkbenchService service) =>
        {
            if (!service.RunnerAuthenticated(request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var reason = string.IsNullOrWhiteSpace(body?.ReasonCode) ? "human_observation_required" : body.ReasonCode;
            var runnerId = request.Headers["X-Workbench-Runner-Id"].ToString();
            return service.WaitForHuman(jobId, runnerId, reason, body?.Result) is { } job ? Results.Ok(job) : Results.Conflict(new { error = "job_not_waiting_transitionable" });
        });
        app.MapPost("/api/v1/workbench/runner/jobs/{jobId}/complete", (string jobId, HttpRequest request, WorkbenchRunnerCompleteRequest? body, WorkbenchService service) =>
        {
            if (!service.RunnerAuthenticated(request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (body is null || body.Verdict is not ("passed" or "failed")) return Results.BadRequest(new { error = "verdict_required" });
            var runnerId = request.Headers["X-Workbench-Runner-Id"].ToString();
            return service.CompleteFromRunner(jobId, runnerId, body.Verdict, body.Result, body.ReasonCode) is { } job ? Results.Ok(job) : Results.NotFound(new { error = "job_not_found_or_runner_mismatch" });
        });
        app.MapPost("/api/v1/workbench/runner/jobs/{jobId}/artifacts", (string jobId, HttpRequest request, WorkbenchArtifact? body, WorkbenchService service) =>
        {
            if (!service.RunnerAuthenticated(request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (body is null) return Results.BadRequest(new { error = "artifact_required" });
            if (!WorkbenchArtifact.IsValid(body, out var error)) return Results.BadRequest(new { error });
            var runnerId = request.Headers["X-Workbench-Runner-Id"].ToString();
            return service.AddArtifact(jobId, runnerId, body) is { } job ? Results.Ok(job) : Results.NotFound(new { error = "job_not_found_or_runner_mismatch" });
        });
    }
}

sealed class WorkbenchInstallation
{
    public int SchemaVersion { get; set; } = 1;
    public string InstallationId { get; set; } = "wb-" + Guid.NewGuid().ToString("N");
    public bool Claimed { get; set; }
    public string? OwnerLabel { get; set; }
    public DateTimeOffset? ClaimedUtc { get; set; }
    public string? LastError { get; set; }
}

sealed record WorkbenchCapability(
    string Id,
    string Title,
    string Intent,
    string SideEffect,
    string HumanTouch,
    IReadOnlyList<string> EligibleProfiles,
    IReadOnlyList<string> EligibleTargets,
    bool ReadOnly,
    string Runner,
    string InputSchema,
    string PrivacyClass,
    bool SupportsCancellation);

sealed class WorkbenchCapabilityView
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Intent { get; set; } = "";
    public string SideEffect { get; set; } = "";
    public string HumanTouch { get; set; } = "";
    public IReadOnlyList<string> EligibleProfiles { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> EligibleTargets { get; set; } = Array.Empty<string>();
    public bool ReadOnly { get; set; }
    public string Runner { get; set; } = "";
    public string InputSchema { get; set; } = "{}";
    public string PrivacyClass { get; set; } = "private_local";
    public bool SupportsCancellation { get; set; }
    public bool Eligible { get; set; }
    public string? ReasonCode { get; set; }
}

sealed class WorkbenchJob
{
    public int SchemaVersion { get; set; } = 1;
    public string JobId { get; set; } = "";
    public string CapabilityId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Profile { get; set; } = "";
    public string Target { get; set; } = "";
    public JsonElement Inputs { get; set; }
    public string State { get; set; } = "queued";
    public string? Verdict { get; set; }
    public string? ReasonCode { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public string? ReceiptPath { get; set; }
    public string? RunnerId { get; set; }
    public DateTimeOffset? LeaseExpiresUtc { get; set; }
    public JsonElement? MachineResult { get; set; }
    public List<WorkbenchArtifact> Artifacts { get; set; } = new();
}

sealed class WorkbenchJobEvent
{
    public long Sequence { get; set; }
    public string JobId { get; set; } = "";
    public string State { get; set; } = "";
    public string ReasonCode { get; set; } = "";
    public DateTimeOffset ObservedUtc { get; set; }
}

sealed class WorkbenchReceipt
{
    public int SchemaVersion { get; set; }
    public string EventType { get; set; } = "";
    public string JobId { get; set; } = "";
    public string CapabilityId { get; set; } = "";
    public string Profile { get; set; } = "";
    public string Target { get; set; } = "";
    public string Verdict { get; set; } = "";
    public string? ReasonCode { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public object? Result { get; set; }
    public object? Source { get; set; }
    public string EvidenceBoundary { get; set; } = "";
    public IReadOnlyList<WorkbenchArtifact> Artifacts { get; set; } = Array.Empty<WorkbenchArtifact>();
}

sealed class WorkbenchArtifact
{
    public string Name { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public string PrivacyClass { get; set; } = "private_local";

    public static bool IsValid(WorkbenchArtifact value, out string error)
    {
        if (string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 160 || value.Name.Any(ch => ch is '/' or '\\' or ':' or '<' or '>' or '\r' or '\n')) { error = "artifact_name_invalid"; return false; }
        if (value.Sha256.Length != 64 || !value.Sha256.All(Uri.IsHexDigit)) { error = "artifact_sha256_invalid"; return false; }
        if (value.SizeBytes < 0 || value.SizeBytes > 4L * 1024 * 1024 * 1024) { error = "artifact_size_invalid"; return false; }
        if (value.PrivacyClass is not ("public_safe" or "private_local" or "private_run")) { error = "artifact_privacy_class_invalid"; return false; }
        value.Name = value.Name.Trim();
        value.Sha256 = value.Sha256.ToLowerInvariant();
        error = "";
        return true;
    }
}

sealed class WorkbenchRunnerHeartbeat
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset ObservedUtc { get; set; }
    public string RunnerVersion { get; set; } = "";
    public string? GatewayState { get; set; }
    public string? Am4State { get; set; }
    public string? OmenState { get; set; }
    public string? I5State { get; set; }
    public bool DockerReady { get; set; }
    public string? SourceRevision { get; set; }
}

sealed record ClaimRequest(string? Label);
sealed record WorkbenchJobRequest(string? Target, JsonElement? Inputs);
sealed record WorkbenchRunnerEventRequest(string State, string ReasonCode);
sealed record WorkbenchRunnerWaitingHumanRequest(string? ReasonCode, JsonElement? Result);
sealed record WorkbenchRunnerCompleteRequest(string Verdict, JsonElement? Result, string? ReasonCode);

sealed class WorkbenchHumanObservation
{
    public string Outcome { get; set; } = "";
    public string RoleFollowing { get; set; } = "";
    public string Quality { get; set; } = "";
    public string? OperatorNote { get; set; }
    public bool? CleanupConfirmed { get; set; }

    public static bool IsValid(WorkbenchHumanObservation value, out string error)
    {
        var outcome = value.Outcome.Trim().ToLowerInvariant();
        var role = value.RoleFollowing.Trim().ToLowerInvariant();
        var quality = value.Quality.Trim().ToLowerInvariant();
        if (outcome is not ("pass" or "fail")) { error = "observation_outcome_invalid"; return false; }
        if (role is not ("followed" or "mixed" or "did_not_follow")) { error = "observation_role_following_invalid"; return false; }
        if (quality is not ("smooth" or "mixed" or "rough")) { error = "observation_quality_invalid"; return false; }
        if (value.OperatorNote?.Length > 500) { error = "observation_note_too_large"; return false; }
        if (value.CleanupConfirmed != true) { error = "observation_cleanup_confirmation_required"; return false; }
        value.Outcome = outcome;
        value.RoleFollowing = role;
        value.Quality = quality;
        value.OperatorNote = string.IsNullOrWhiteSpace(value.OperatorNote) ? null : value.OperatorNote.Trim();
        error = "";
        return true;
    }
}

sealed record WorkbenchNode(
    string Id,
    string Title,
    string State,
    string Target,
    string Role,
    string HumanTouch,
    DateTimeOffset? ObservedUtc,
    string Source)
{
    public string? ActiveJobPhase { get; init; }
    public string? ActiveJobId { get; init; }
}
