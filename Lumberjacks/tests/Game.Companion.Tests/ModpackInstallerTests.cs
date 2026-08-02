using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lumberjacks.Companion;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Game.Companion.Tests;

public sealed class ModpackInstallerTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "lumberjacks-companion-test-" + Guid.NewGuid().ToString("N"));
    readonly string _data;
    readonly string _valheim;
    readonly Dictionary<string, string?> _priorEnvironment = new();

    public ModpackInstallerTests()
    {
        _data = Path.Combine(_root, "data");
        _valheim = Path.Combine(_root, "Valheim");
        Directory.CreateDirectory(_data);
        Directory.CreateDirectory(_valheim);
        File.WriteAllBytes(Path.Combine(_valheim, "valheim.exe"), []);
        WriteRelative("BepInEx/config/djcdevelopment.valheim.comfynetworksense.cfg",
            "lumberjacksEnrollmentId = fixture-enrollment\nlumberjacksClientAccessKey = fixture-key\n");
        SetEnvironment("LUMBERJACKS_COMPANION_DATA", _data);
        SetEnvironment("LUMBERJACKS_VALHEIM_PATH", _valheim);
        SetEnvironment("LUMBERJACKS_COMPANION_GATEWAY_URL", "http://fixture-gateway");
    }

    [Fact]
    public async Task InstallThenRollbackRestoresBytesRemovesCreatedFilesAndRestoresPriorIdentity()
    {
        WriteRelative("BepInEx/plugins/existing.dll", "old bytes");
        var store = SeedPriorInstall();
        var package = Package(
            ("Valheim/BepInEx/plugins/existing.dll", "new bytes"),
            ("Valheim/BepInEx/plugins/created.dll", "created bytes"));
        var installer = Installer(store);

        var installed = await installer.InstallAsync(Gateway(package), CancellationToken.None);

        Assert.True(installed.ok);
        Assert.Equal("new bytes", ReadRelative("BepInEx/plugins/existing.dll"));
        Assert.Equal("created bytes", ReadRelative("BepInEx/plugins/created.dll"));
        var installedState = store.Read().installed;
        Assert.NotNull(installedState);
        Assert.Equal("candidate", installedState!.release);
        Assert.Equal(1, installedState.transaction_schema_version);
        Assert.Equal(["BepInEx/plugins/created.dll"], installedState.created_files);
        Assert.Equal("prior", installedState.previous?.release);

        var rolledBack = installer.RollbackLatest();

        Assert.True(rolledBack.ok);
        Assert.Equal("rolled_back", rolledBack.result);
        Assert.Equal("old bytes", ReadRelative("BepInEx/plugins/existing.dll"));
        Assert.False(File.Exists(Relative("BepInEx/plugins/created.dll")));
        Assert.Equal("prior", store.Read().installed?.release);
        Assert.Equal("prior", rolledBack.installed?.release);
    }

    [Theory]
    [InlineData("outside.txt", "package_entry_outside_valheim")]
    [InlineData("Valheim/BepInEx/config/djcdevelopment.valheim.comfynetworksense.cfg", "package_personalized_config_forbidden")]
    [InlineData("Valheim/../escaped.dll", "package_entry_outside_valheim")]
    public async Task UnsafePackageFailsBeforeChangingExistingFile(string unsafeEntry, string expectedResult)
    {
        WriteRelative("BepInEx/plugins/existing.dll", "old bytes");
        var store = SeedPriorInstall();
        var package = Package(
            ("Valheim/BepInEx/plugins/existing.dll", "new bytes"),
            (unsafeEntry, "unsafe"));
        var installer = Installer(store);

        var result = await installer.InstallAsync(Gateway(package), CancellationToken.None);

        Assert.False(result.ok);
        Assert.Equal(expectedResult, result.result);
        Assert.Equal("old bytes", ReadRelative("BepInEx/plugins/existing.dll"));
        Assert.Equal("prior", store.Read().installed?.release);
    }

    [Fact]
    public async Task HashMismatchFailsBeforeCreatingBackupOrChangingTarget()
    {
        WriteRelative("BepInEx/plugins/existing.dll", "old bytes");
        var store = SeedPriorInstall();
        var package = Package(("Valheim/BepInEx/plugins/existing.dll", "new bytes"));
        var installer = Installer(store);

        var result = await installer.InstallAsync(Gateway(package, manifestHash: new string('0', 64)), CancellationToken.None);

        Assert.False(result.ok);
        Assert.Equal("package_hash_mismatch", result.result);
        Assert.Equal("old bytes", ReadRelative("BepInEx/plugins/existing.dll"));
        Assert.Equal("prior", store.Read().installed?.release);
        Assert.Equal(["prior"], Directory.EnumerateDirectories(Path.Combine(_data, "backups"))
            .Select(Path.GetFileName));
    }

    [Fact]
    public async Task DuplicatePayloadTargetFailsBeforeChangingExistingFile()
    {
        WriteRelative("BepInEx/plugins/existing.dll", "old bytes");
        var store = SeedPriorInstall();
        var package = Package(
            ("Valheim/BepInEx/plugins/existing.dll", "new bytes"),
            ("valheim/bepinex/PLUGINS/existing.dll", "duplicate bytes"));

        var result = await Installer(store).InstallAsync(Gateway(package), CancellationToken.None);

        Assert.False(result.ok);
        Assert.Equal("package_duplicate_entry", result.result);
        Assert.Equal("old bytes", ReadRelative("BepInEx/plugins/existing.dll"));
        Assert.Equal("prior", store.Read().installed?.release);
    }

    [Fact]
    public async Task MetadataOnlyPackageFailsBeforeCreatingBackup()
    {
        var store = SeedPriorInstall();
        var package = Package(("README.txt", "metadata only"));

        var result = await Installer(store).InstallAsync(Gateway(package), CancellationToken.None);

        Assert.False(result.ok);
        Assert.Equal("package_payload_empty", result.result);
        Assert.Equal(["prior"], Directory.EnumerateDirectories(Path.Combine(_data, "backups"))
            .Select(Path.GetFileName));
    }

    [Fact]
    public async Task StateWriteFailureAutomaticallyRestoresAppliedFiles()
    {
        WriteRelative("BepInEx/plugins/existing.dll", "old bytes");
        var store = new CompanionStateStore();
        Directory.CreateDirectory(Path.Combine(_data, "companion-state.json"));
        var package = Package(
            ("Valheim/BepInEx/plugins/existing.dll", "new bytes"),
            ("Valheim/BepInEx/plugins/created.dll", "created bytes"));

        var result = await Installer(store).InstallAsync(Gateway(package), CancellationToken.None);

        Assert.False(result.ok);
        Assert.Equal("install_state_write_failed", result.result);
        Assert.Equal("old bytes", ReadRelative("BepInEx/plugins/existing.dll"));
        Assert.False(File.Exists(Relative("BepInEx/plugins/created.dll")));
    }

    [Fact]
    public async Task ConcurrentOperationFailsClosedWhileInstallIsInProgress()
    {
        var store = SeedPriorInstall();
        var package = Package(("Valheim/BepInEx/plugins/created.dll", "created bytes"));
        var handler = new BlockingGatewayHandler(package);
        var installer = Installer(store);

        var installTask = installer.InstallAsync(
            new GatewayClient(new HttpClient(handler)), CancellationToken.None);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var concurrent = installer.RollbackLatest();

        Assert.False(concurrent.ok);
        Assert.Equal("modpack_operation_in_progress", concurrent.result);
        handler.Release.TrySetResult();
        Assert.True((await installTask).ok);
    }

    [Fact]
    public void LegacyRollbackFailsClosedWithoutChangingBytesOrIdentity()
    {
        WriteRelative("BepInEx/plugins/existing.dll", "new bytes");
        var backupRoot = Path.Combine(_data, "backups", "legacy");
        Directory.CreateDirectory(Path.Combine(backupRoot, "BepInEx", "plugins"));
        File.WriteAllText(Path.Combine(backupRoot, "BepInEx", "plugins", "existing.dll"), "old bytes");
        var store = new CompanionStateStore();
        var state = store.Read();
        state.installed = new InstalledRelease("legacy", "mod", "hash", DateTime.UtcNow,
            backupRoot, ["BepInEx/plugins/existing.dll"]);
        store.Write(state);

        var result = Installer(store).RollbackLatest();

        Assert.False(result.ok);
        Assert.Equal("rollback_not_reversible_legacy_state", result.result);
        Assert.Equal("new bytes", ReadRelative("BepInEx/plugins/existing.dll"));
        Assert.Equal("legacy", store.Read().installed?.release);
    }

    [Fact]
    public void RegistryDisablesRollbackWithoutReversibleTransactionState()
    {
        var registry = new WorkbenchRegistry();

        var unavailable = registry.For("Lab", runnerReady: true, rollbackReady: false)
            .Single(capability => capability.Id == "operate.mod.rollback");
        var available = registry.For("Lab", runnerReady: true, rollbackReady: true)
            .Single(capability => capability.Id == "operate.mod.rollback");

        Assert.False(unavailable.Eligible);
        Assert.Equal("rollback_not_available", unavailable.ReasonCode);
        Assert.True(available.Eligible);
        Assert.Null(available.ReasonCode);
    }

    [Fact]
    public void RollbackRejectsBackupOutsideCompanionState()
    {
        var outsideBackup = Path.Combine(_root, "outside-backup");
        Directory.CreateDirectory(outsideBackup);
        var store = new CompanionStateStore();
        var state = store.Read();
        state.installed = new InstalledRelease("unsafe", "mod", "hash", DateTime.UtcNow,
            outsideBackup, [], transaction_schema_version: 1);
        store.Write(state);

        var result = Installer(store).RollbackLatest();

        Assert.False(result.ok);
        Assert.Equal("rollback_backup_outside_state", result.result);
        Assert.Equal("unsafe", store.Read().installed?.release);
    }

    CompanionStateStore SeedPriorInstall()
    {
        var priorBackup = Path.Combine(_data, "backups", "prior");
        Directory.CreateDirectory(priorBackup);
        var store = new CompanionStateStore();
        var state = store.Read();
        state.installed = new InstalledRelease("prior", "prior-mod", "prior-hash", DateTime.UtcNow,
            priorBackup, []);
        store.Write(state);
        return store;
    }

    static ModpackInstaller Installer(CompanionStateStore store) =>
        new(store, new ValheimLocator());

    static GatewayClient Gateway(byte[] package, string? manifestHash = null)
    {
        var hash = manifestHash ?? Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
        var manifest = JsonSerializer.Serialize(new
        {
            schema_version = 1,
            release = "candidate",
            mod_release = "candidate-mod",
            package = new { kind = "fixture", sha256 = hash, size_bytes = package.Length },
        });
        return new GatewayClient(new HttpClient(new FixtureGatewayHandler(manifest, package)));
    }

    static byte[] Package(params (string Path, string Body)[] entries)
    {
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Path);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(item.Body);
            }
        }
        return bytes.ToArray();
    }

    string Relative(string relative) => Path.Combine(_valheim, relative.Replace('/', Path.DirectorySeparatorChar));

    string ReadRelative(string relative) => File.ReadAllText(Relative(relative));

    void WriteRelative(string relative, string content)
    {
        var path = Relative(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    void SetEnvironment(string name, string value)
    {
        _priorEnvironment[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        foreach (var item in _priorEnvironment) Environment.SetEnvironmentVariable(item.Key, item.Value);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    sealed class FixtureGatewayHandler(string manifest, byte[] package) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = request.RequestUri!.AbsolutePath.EndsWith("/manifest", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) };
            return Task.FromResult(response);
        }
    }

    sealed class BlockingGatewayHandler(byte[] package) : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/manifest", StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                var hash = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
                var manifest = JsonSerializer.Serialize(new
                {
                    schema_version = 1,
                    release = "candidate",
                    mod_release = "candidate-mod",
                    package = new { kind = "fixture", sha256 = hash, size_bytes = package.Length },
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(manifest, Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) };
        }
    }
}
