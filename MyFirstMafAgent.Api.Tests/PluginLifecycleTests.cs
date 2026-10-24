using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Application.Plugins;
using MyFirstMafAgent.Api.Options;
using MyFirstMafAgent.Api.Plugins;
using MyFirstMafAgent.Plugins.Sdk;
using Xunit;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>JSON Schema 子集校验器：只要关键字语义正确，足够挡住用户配置错误。</summary>
public sealed class JsonSchemaLiteTests
{
    [Fact]
    public void Missing_required_property_is_reported()
    {
        var schema = JsonNode.Parse("""{"type":"object","required":["token"],"properties":{"token":{"type":"string"}}}""");
        var errors = JsonSchemaLite.Validate(schema, JsonNode.Parse("""{"other":1}"""));

        Assert.Contains(errors, error => error.Contains("token") && error.Contains("必填"));
    }

    [Fact]
    public void Type_mismatch_is_reported()
    {
        var schema = JsonNode.Parse("""{"type":"object","properties":{"count":{"type":"integer"}}}""");
        var errors = JsonSchemaLite.Validate(schema, JsonNode.Parse("""{"count":"abc"}"""));

        Assert.Single(errors);
        Assert.Contains("integer", errors[0]);
    }

    [Fact]
    public void Additional_properties_are_rejected_when_schema_is_closed()
    {
        var schema = JsonNode.Parse(
            """{"type":"object","properties":{"a":{"type":"string"}},"additionalProperties":false}""");
        var errors = JsonSchemaLite.Validate(schema, JsonNode.Parse("""{"a":"x","b":"y"}"""));

        Assert.Contains(errors, error => error.Contains("$.b"));
    }

    [Fact]
    public void Enum_constraints_are_enforced()
    {
        var schema = JsonNode.Parse("""{"type":"object","properties":{"mode":{"enum":["fast","deep"]}}}""");
        var errors = JsonSchemaLite.Validate(schema, JsonNode.Parse("""{"mode":"slow"}"""));

        Assert.Single(errors);
    }

    [Fact]
    public void Numeric_bounds_and_string_pattern_are_enforced()
    {
        var schema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "limit": { "type": "integer", "minimum": 1, "maximum": 10 },
            "endpoint": { "type": "string", "pattern": "^https://" }
          }
        }
        """);

        var errors = JsonSchemaLite.Validate(schema, JsonNode.Parse("""{"limit":99,"endpoint":"ftp://x"}"""));

        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void Nested_objects_and_arrays_validate()
    {
        var schema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "servers": {
              "type": "array",
              "minItems": 1,
              "items": { "type": "object", "required": ["url"], "properties": { "url": { "type": "string" } } }
            }
          }
        }
        """);

        Assert.Empty(JsonSchemaLite.Validate(schema, JsonNode.Parse("""{"servers":[{"url":"https://a"}]}""")));

        var missingUrl = JsonSchemaLite.Validate(schema, JsonNode.Parse("""{"servers":[{}]}"""));
        Assert.Contains(missingUrl, error => error.Contains("servers[0].url"));

        var emptyArray = JsonSchemaLite.Validate(schema, JsonNode.Parse("""{"servers":[]}"""));
        Assert.Contains(emptyArray, error => error.Contains("至少需要 1 项"));
    }

    [Fact]
    public void Broken_schema_text_is_reported_without_throwing()
    {
        var errors = JsonSchemaLite.Validate("{not json", JsonNode.Parse("""{}"""));

        Assert.Single(errors);
        Assert.Contains("合法的 JSON", errors[0]);
    }

    [Fact]
    public void Empty_schema_constrains_nothing()
    {
        Assert.Empty(JsonSchemaLite.Validate((string?)null, JsonNode.Parse("""{"anything":true}""")));
    }
}

/// <summary>权限与副作用闸门的完整矩阵。</summary>
public sealed class PluginAccessPolicyTests
{
    [Fact]
    public void Plugin_without_permissions_starts()
    {
        var decision = PluginAccessPolicy.CheckStart([], [], autoGrant: false);

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public void Missing_grants_block_start_when_auto_grant_is_off()
    {
        var decision = PluginAccessPolicy.CheckStart(["network", "file-read"], ["network"], autoGrant: false);

        Assert.False(decision.IsAllowed);
        Assert.Equal(["file-read"], decision.MissingItems);
        Assert.Contains("file-read", decision.Reason);
    }

    [Fact]
    public void Auto_grant_skips_the_gate()
    {
        Assert.True(PluginAccessPolicy.CheckStart(["network"], [], autoGrant: true).IsAllowed);
    }

    [Fact]
    public void Unknown_permission_is_rejected()
    {
        var decision = PluginAccessPolicy.CheckStart(["root-everything"], [], autoGrant: true);

        Assert.False(decision.IsAllowed);
        Assert.Contains("root-everything", decision.MissingItems);
    }

    [Fact]
    public void Grant_request_cannot_exceed_declared_permissions()
    {
        var errors = PluginAccessPolicy.ValidateGrantRequest(["network"], ["network", "file-write", "telepathy"]);

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, error => error.Contains("file-write"));
        Assert.Contains(errors, error => error.Contains("telepathy"));
    }

    [Fact]
    public void Read_only_tools_are_model_callable_even_with_strict_policy()
    {
        Assert.True(PluginAccessPolicy.IsModelCallable(PluginCapabilityKinds.AgentTool, PluginSideEffectLevels.Read, true));
    }

    [Fact]
    public void Side_effecting_tools_are_not_model_callable_under_strict_policy()
    {
        Assert.False(PluginAccessPolicy.IsModelCallable(PluginCapabilityKinds.AgentTool, PluginSideEffectLevels.Destructive, true));
        Assert.False(PluginAccessPolicy.IsModelCallable(PluginCapabilityKinds.AgentTool, PluginSideEffectLevels.WriteExternal, true));
        Assert.True(PluginAccessPolicy.IsModelCallable(PluginCapabilityKinds.AgentTool, PluginSideEffectLevels.Destructive, false));
    }

    [Fact]
    public void Platform_connectors_are_never_model_callable()
    {
        Assert.False(PluginAccessPolicy.IsModelCallable(
            PluginCapabilityKinds.PlatformConnector,
            PluginSideEffectLevels.Read,
            false));
    }

    [Fact]
    public void Destructive_invocation_needs_explicit_confirmation()
    {
        var capability = new PluginCapability { Id = "notes.delete", SideEffect = PluginSideEffectLevels.Destructive };

        Assert.False(PluginAccessPolicy.CheckInvocation(
            capability, confirmed: false, PluginActors.Api, requireConfirmationForSideEffects: true).IsAllowed);

        Assert.True(PluginAccessPolicy.CheckInvocation(
            capability, confirmed: true, PluginActors.Api, requireConfirmationForSideEffects: true).IsAllowed);
    }

    [Fact]
    public void Agent_calls_are_always_unconfirmed()
    {
        var capability = new PluginCapability { Id = "notes.publish", SideEffect = PluginSideEffectLevels.WriteExternal };

        var decision = PluginAccessPolicy.CheckInvocation(
            capability, confirmed: false, PluginActors.Agent, requireConfirmationForSideEffects: true);

        Assert.False(decision.IsAllowed);
        Assert.Contains("模型不能直接调用", decision.Reason);
    }

    [Fact]
    public void Read_capability_never_needs_confirmation()
    {
        var capability = new PluginCapability { Id = "notes.search", SideEffect = PluginSideEffectLevels.Read };

        Assert.True(PluginAccessPolicy.CheckInvocation(
            capability, confirmed: false, PluginActors.Agent, requireConfirmationForSideEffects: true).IsAllowed);
    }
}

/// <summary>包摘要与签名验证。</summary>
public sealed class PluginIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maf-integrity-" + Guid.NewGuid().ToString("N"));

    public PluginIntegrityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Digest_is_stable_and_ignores_manifest_and_bookkeeping()
    {
        var package = Path.Combine(_root, "pkg");
        Directory.CreateDirectory(Path.Combine(package, "lib"));
        await File.WriteAllTextAsync(Path.Combine(package, "manifest.json"), """{"id":"com.example.a"}""");
        await File.WriteAllTextAsync(Path.Combine(package, "plugin.exe"), "binary");
        await File.WriteAllTextAsync(Path.Combine(package, "lib", "helper.dll"), "lib");

        var first = await PluginIntegrityVerifier.ComputePackageDigestAsync(package, CancellationToken.None);

        // 写入摘要与运行时文件不应改变摘要。
        await File.WriteAllTextAsync(Path.Combine(package, "active.json"), """{"version":"1.0.0"}""");
        Directory.CreateDirectory(Path.Combine(package, ".data"));
        await File.WriteAllTextAsync(Path.Combine(package, ".data", "cache.txt"), "cache");

        var second = await PluginIntegrityVerifier.ComputePackageDigestAsync(package, CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
        Assert.Equal(first.ToLowerInvariant(), first);
    }

    [Fact]
    public async Task Digest_changes_when_payload_changes()
    {
        var package = Path.Combine(_root, "pkg2");
        Directory.CreateDirectory(package);
        await File.WriteAllTextAsync(Path.Combine(package, "plugin.exe"), "v1");

        var before = await PluginIntegrityVerifier.ComputePackageDigestAsync(package, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(package, "plugin.exe"), "v2");
        var after = await PluginIntegrityVerifier.ComputePackageDigestAsync(package, CancellationToken.None);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task Enforce_policy_rejects_missing_digest()
    {
        var package = CreatePackage("pkg3");
        var verifier = CreateVerifier("enforce");

        var report = await verifier.VerifyAsync(package, new PluginManifest { Id = "com.example.a" }, CancellationToken.None);

        Assert.False(report.IsAcceptable);
        Assert.Equal(PluginIntegrityReport.StatusNotProvided, report.Status);
    }

    [Fact]
    public async Task Warn_policy_accepts_mismatch_but_reports_it()
    {
        var package = CreatePackage("pkg4");
        var verifier = CreateVerifier("warn");
        var manifest = new PluginManifest
        {
            Id = "com.example.a",
            Integrity = new PluginIntegrity { Sha256 = new string('0', 64) }
        };

        var report = await verifier.VerifyAsync(package, manifest, CancellationToken.None);

        Assert.True(report.IsAcceptable);
        Assert.NotEmpty(report.Warnings);
        Assert.Equal(PluginIntegrityReport.StatusDigestMismatch, report.Status);
    }

    [Fact]
    public async Task Matching_digest_verifies()
    {
        var package = CreatePackage("pkg5");
        var digest = await PluginIntegrityVerifier.ComputePackageDigestAsync(package, CancellationToken.None);
        var verifier = CreateVerifier("warn");

        var report = await verifier.VerifyAsync(
            package,
            new PluginManifest { Id = "com.example.a", Integrity = new PluginIntegrity { Sha256 = digest } },
            CancellationToken.None);

        Assert.True(report.IsAcceptable);
        Assert.Empty(report.Warnings);
        Assert.Equal(PluginIntegrityReport.StatusVerified, report.Status);
        Assert.Equal(digest, report.PackageDigest);
    }

    [Fact]
    public async Task Ecdsa_signature_over_the_digest_verifies()
    {
        var package = CreatePackage("pkg6");
        var digest = await PluginIntegrityVerifier.ComputePackageDigestAsync(package, CancellationToken.None);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(digest), HashAlgorithmName.SHA256));

        var verifier = CreateVerifier("enforce", key.ExportSubjectPublicKeyInfoPem());

        var report = await verifier.VerifyAsync(
            package,
            new PluginManifest
            {
                Id = "com.example.trusted",
                Integrity = new PluginIntegrity { Sha256 = digest, Signature = signature }
            },
            CancellationToken.None);

        Assert.True(report.IsAcceptable);
        Assert.Equal(PluginIntegrityReport.StatusVerified, report.Status);
    }

    [Fact]
    public async Task Signature_from_untrusted_publisher_is_rejected_under_enforce()
    {
        var package = CreatePackage("pkg7");
        var digest = await PluginIntegrityVerifier.ComputePackageDigestAsync(package, CancellationToken.None);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(digest), HashAlgorithmName.SHA256));

        // 受信任的是另一个发布者的公钥。
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = CreateVerifier("enforce", other.ExportSubjectPublicKeyInfoPem());

        var report = await verifier.VerifyAsync(
            package,
            new PluginManifest
            {
                Id = "com.example.trusted",
                Integrity = new PluginIntegrity { Sha256 = digest, Signature = signature }
            },
            CancellationToken.None);

        Assert.False(report.IsAcceptable);
        Assert.Contains(report.Errors, error => error.Contains("签名"));
    }

    [Fact]
    public async Task Tampered_payload_fails_enforce_policy()
    {
        var package = CreatePackage("pkg8");
        var digest = await PluginIntegrityVerifier.ComputePackageDigestAsync(package, CancellationToken.None);

        await File.WriteAllTextAsync(Path.Combine(package, "plugin.exe"), "tampered");

        var report = await CreateVerifier("warn").VerifyAsync(
            package,
            new PluginManifest { Id = "com.example.a", Integrity = new PluginIntegrity { Sha256 = digest } },
            CancellationToken.None);

        Assert.Equal(PluginIntegrityReport.StatusDigestMismatch, report.Status);
        Assert.NotEmpty(report.Warnings);
    }

    [Fact]
    public void Trusted_key_matches_by_domain_segment()
    {
        var verifier = CreateVerifier("warn", "dummy", "com.example");

        Assert.NotNull(verifier.FindKey("com.example.notes"));
        Assert.NotNull(verifier.FindKey("com.example"));
        Assert.Null(verifier.FindKey("com.exampleevil.notes"));
        Assert.Null(verifier.FindKey("org.other.notes"));
    }

    private string CreatePackage(string name)
    {
        var package = Path.Combine(_root, name);
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "manifest.json"), """{"id":"com.example.a"}""");
        File.WriteAllText(Path.Combine(package, "plugin.exe"), "payload-" + name);
        return package;
    }

    private static PluginIntegrityVerifier CreateVerifier(
        string policy,
        string? publicKeyPem = null,
        string keyPluginId = "com.example")
    {
        var options = new PluginOptions { IntegrityPolicy = policy };

        if (publicKeyPem is not null)
        {
            options.TrustedPublisherKeys.Add(new PluginTrustedKey
            {
                PluginId = keyPluginId,
                Algorithm = "ecdsa-p256",
                PublicKeyPem = publicKeyPem
            });
        }

        return new PluginIntegrityVerifier(
            Microsoft.Extensions.Options.Options.Create(options),
            TestTrustedKeys.FromOptions(options),
            NullLogger<PluginIntegrityVerifier>.Instance);
    }
}

/// <summary>安装、激活、回滚、卸载与版本布局。</summary>
public sealed class PluginInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maf-installer-" + Guid.NewGuid().ToString("N"));

    public PluginInstallerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Installing_a_directory_creates_version_layout_and_active_pointer()
    {
        var installer = CreateInstaller();
        var package = CreatePackage("1.0.0");

        var result = await installer.InstallFromDirectoryAsync(package, CancellationToken.None);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal("com.example.test", result.PluginId);
        Assert.Equal("1.0.0", result.Version);

        var pluginRoot = Path.Combine(_root, "plugins", "com.example.test");
        Assert.True(File.Exists(Path.Combine(pluginRoot, "versions", "1.0.0", "manifest.json")));
        Assert.True(File.Exists(Path.Combine(pluginRoot, "versions", "1.0.0", "plugin.exe")));
        Assert.Equal("1.0.0", PluginManifestLoader.ReadActiveVersion(pluginRoot));
    }

    [Fact]
    public async Task Upgrading_switches_active_and_keeps_previous_for_rollback()
    {
        var installer = CreateInstaller();
        await installer.InstallFromDirectoryAsync(CreatePackage("1.0.0"), CancellationToken.None);
        await installer.InstallFromDirectoryAsync(CreatePackage("1.1.0"), CancellationToken.None);

        var pluginRoot = Path.Combine(_root, "plugins", "com.example.test");
        Assert.Equal("1.1.0", PluginManifestLoader.ReadActiveVersion(pluginRoot));
        Assert.Equal("1.0.0", installer.ReadPrevious("com.example.test"));
        Assert.Equal(
            ["1.1.0", "1.0.0"],
            PluginManifestLoader.GetInstalledVersions(pluginRoot));
    }

    [Fact]
    public async Task Rollback_swaps_the_pointer_and_can_roll_forward_again()
    {
        var installer = CreateInstaller();
        await installer.InstallFromDirectoryAsync(CreatePackage("1.0.0"), CancellationToken.None);
        await installer.InstallFromDirectoryAsync(CreatePackage("1.1.0"), CancellationToken.None);

        var rolled = await installer.RollbackAsync("com.example.test", CancellationToken.None);

        Assert.True(rolled.Success, string.Join("; ", rolled.Errors));
        Assert.Equal("1.1.0", rolled.FromVersion);
        Assert.Equal("1.0.0", rolled.ToVersion);

        var pluginRoot = Path.Combine(_root, "plugins", "com.example.test");
        Assert.Equal("1.0.0", PluginManifestLoader.ReadActiveVersion(pluginRoot));
        Assert.Equal("1.1.0", installer.ReadPrevious("com.example.test"));

        var forward = await installer.RollbackAsync("com.example.test", CancellationToken.None);
        Assert.True(forward.Success);
        Assert.Equal("1.1.0", PluginManifestLoader.ReadActiveVersion(pluginRoot));
    }

    [Fact]
    public async Task Rollback_without_history_fails_cleanly()
    {
        var installer = CreateInstaller();
        await installer.InstallFromDirectoryAsync(CreatePackage("1.0.0"), CancellationToken.None);

        var rolled = await installer.RollbackAsync("com.example.test", CancellationToken.None);

        Assert.False(rolled.Success);
        Assert.Equal("1.0.0", PluginManifestLoader.ReadActiveVersion(
            Path.Combine(_root, "plugins", "com.example.test")));
    }

    [Fact]
    public async Task Installing_a_zip_with_a_wrapper_folder_works()
    {
        var installer = CreateInstaller();
        var package = CreatePackage("2.0.0");
        var zipPath = Path.Combine(_root, "package.zip");

        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.EnumerateFiles(package))
                archive.CreateEntryFromFile(file, "wrapped/" + Path.GetFileName(file));
        }

        await using var stream = File.OpenRead(zipPath);
        var result = await installer.InstallAsync(stream, CancellationToken.None);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal("2.0.0", result.Version);
    }

    [Fact]
    public async Task Zip_with_path_traversal_is_rejected()
    {
        var installer = CreateInstaller();
        var zipPath = Path.Combine(_root, "evil.zip");

        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../escape.txt");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("pwned");
        }

        await using var stream = File.OpenRead(zipPath);
        var result = await installer.InstallAsync(stream, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("非法路径"));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }

    [Fact]
    public async Task Uninstall_removes_versions_and_data()
    {
        var installer = CreateInstaller();
        await installer.InstallFromDirectoryAsync(CreatePackage("1.0.0"), CancellationToken.None);

        var dataDirectory = Path.Combine(_root, "plugins", ".data", "com.example.test");
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "state.json"), "{}");

        await installer.UninstallAsync("com.example.test", CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(_root, "plugins", "com.example.test")));
        Assert.False(Directory.Exists(dataDirectory));
    }

    [Fact]
    public async Task Old_versions_are_pruned_but_one_previous_is_kept()
    {
        var installer = CreateInstaller(keepVersions: 2);
        await installer.InstallFromDirectoryAsync(CreatePackage("1.0.0"), CancellationToken.None);
        await installer.InstallFromDirectoryAsync(CreatePackage("1.1.0"), CancellationToken.None);
        await installer.InstallFromDirectoryAsync(CreatePackage("1.2.0"), CancellationToken.None);

        var versions = PluginManifestLoader.GetInstalledVersions(
            Path.Combine(_root, "plugins", "com.example.test"));

        Assert.Equal(["1.2.0", "1.1.0"], versions);
    }

    [Fact]
    public async Task Package_without_manifest_is_rejected()
    {
        var installer = CreateInstaller();
        var package = Path.Combine(_root, "empty");
        Directory.CreateDirectory(package);

        var result = await installer.InstallFromDirectoryAsync(package, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("manifest.json"));
    }

    private PluginInstaller CreateInstaller(int keepVersions = 3)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new PluginOptions
        {
            RootPath = Path.Combine(_root, "plugins"),
            IntegrityPolicy = "warn",
            KeepVersions = keepVersions
        });

        return new PluginInstaller(
            options,
            new PluginPaths(options.Value.RootPath),
            new PluginManifestLoader(NullLogger<PluginManifestLoader>.Instance),
            new PluginIntegrityVerifier(
                options,
                TestTrustedKeys.FromOptions(options.Value),
                NullLogger<PluginIntegrityVerifier>.Instance),
            NullLogger<PluginInstaller>.Instance);
    }

    /// <summary>写出一个最小可用包：manifest + 入口文件。</summary>
    private string CreatePackage(string version, string id = "com.example.test")
    {
        var directory = Path.Combine(_root, "src-" + version + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(directory);

        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["id"] = id,
            ["name"] = "Test Plugin",
            ["version"] = version,
            ["publisher"] = "Example",
            ["entry"] = new JsonObject { ["command"] = "plugin.exe", ["arguments"] = new JsonArray() },
            ["runtime"] = "dotnet",
            ["protocol"] = new JsonObject { ["minVersion"] = "1.0.0", ["maxVersion"] = "1.0.999" },
            ["capabilities"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "test.echo",
                    ["kind"] = "agent-tool",
                    ["name"] = "Echo",
                    ["description"] = "echo",
                    ["parameters"] = """{"type":"object","properties":{}}""",
                    ["permissions"] = new JsonArray(),
                    ["sideEffect"] = "read"
                }
            },
            ["limits"] = new JsonObject
            {
                ["maxMemoryMb"] = 256,
                ["maxConcurrentInvocations"] = 2,
                ["invocationTimeoutMs"] = 15000,
                ["maxRequestBytes"] = 262144,
                ["maxRetries"] = 0
            }
        };

        File.WriteAllText(Path.Combine(directory, "manifest.json"), manifest.ToJsonString());
        File.WriteAllText(Path.Combine(directory, "plugin.exe"), "stub-" + version);
        return directory;
    }
}

/// <summary>版本布局解析：versions/{v} + active.json，并兼容阶段 3 的扁平布局。</summary>
public sealed class PluginVersionLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maf-layout-" + Guid.NewGuid().ToString("N"));
    private readonly PluginManifestLoader _loader = new(NullLogger<PluginManifestLoader>.Instance);

    public PluginVersionLayoutTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Versioned_layout_resolves_the_active_version()
    {
        var pluginRoot = WriteVersionedPlugin("1.0.0", "1.1.0", active: "1.0.0");

        var load = _loader.LoadPlugin(pluginRoot);

        Assert.True(load.IsUsable, string.Join("; ", load.Validation.Errors));
        Assert.Equal("1.0.0", load.ActiveVersion);
        Assert.Equal(["1.1.0", "1.0.0"], load.InstalledVersions);
        Assert.EndsWith(Path.Combine("1.0.0"), load.Directory);
    }

    [Fact]
    public void Missing_active_pointer_falls_back_to_newest_version()
    {
        var pluginRoot = WriteVersionedPlugin("1.0.0", "1.9.0", active: null);

        var load = _loader.LoadPlugin(pluginRoot);

        Assert.Equal("1.9.0", load.ActiveVersion);
    }

    [Fact]
    public void Broken_active_pointer_falls_back_to_newest_version()
    {
        var pluginRoot = WriteVersionedPlugin("1.0.0", "1.1.0", active: "9.9.9");

        var load = _loader.LoadPlugin(pluginRoot);

        Assert.Equal("1.1.0", load.ActiveVersion);
    }

    [Fact]
    public void Flat_layout_still_loads()
    {
        var pluginRoot = Path.Combine(_root, "flat", "com.example.flat");
        Directory.CreateDirectory(pluginRoot);
        WriteManifest(pluginRoot, "1.0.0", "com.example.flat");
        File.WriteAllText(Path.Combine(pluginRoot, "plugin.exe"), "stub");

        var load = _loader.LoadPlugin(pluginRoot);

        Assert.True(load.IsUsable);
        Assert.Null(load.ActiveVersion);
        Assert.Empty(load.InstalledVersions);
    }

    [Fact]
    public void Scan_skips_dot_directories()
    {
        var root = Path.Combine(_root, "scan");
        Directory.CreateDirectory(Path.Combine(root, ".staging"));
        WriteVersionedPlugin("1.0.0", null, active: "1.0.0", root: root);

        var loads = _loader.Scan(root);

        Assert.Single(loads);
        Assert.Equal("com.example.test", loads[0].Id);
    }

    private string WriteVersionedPlugin(string version, string? secondVersion, string? active, string? root = null)
    {
        var pluginRoot = Path.Combine(root ?? _root, "com.example.test");
        foreach (var candidate in new[] { version, secondVersion })
        {
            if (candidate is null) continue;

            var directory = Path.Combine(pluginRoot, "versions", candidate);
            Directory.CreateDirectory(directory);
            WriteManifest(directory, candidate, "com.example.test");
            File.WriteAllText(Path.Combine(directory, "plugin.exe"), "stub-" + candidate);
        }

        if (active is not null)
        {
            Directory.CreateDirectory(pluginRoot);
            File.WriteAllText(
                Path.Combine(pluginRoot, "active.json"),
                $$"""{"version":"{{active}}"}""");
        }

        return pluginRoot;
    }

    private static void WriteManifest(string directory, string version, string id)
    {
        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["id"] = id,
            ["name"] = "Layout Plugin",
            ["version"] = version,
            ["publisher"] = "Example",
            ["entry"] = new JsonObject { ["command"] = "plugin.exe" },
            ["protocol"] = new JsonObject { ["minVersion"] = "1.0.0", ["maxVersion"] = "1.0.999" },
            ["capabilities"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "test.echo",
                    ["kind"] = "agent-tool",
                    ["name"] = "Echo",
                    ["parameters"] = """{"type":"object"}""",
                    ["sideEffect"] = "read"
                }
            }
        };

        File.WriteAllText(Path.Combine(directory, "manifest.json"), manifest.ToJsonString());
    }
}

/// <summary>配置校验与审计脱敏。</summary>
public sealed class PluginConfigAndAuditTests
{
    private static PluginManifest ConfigurableManifest(string schema) => new()
    {
        Id = "com.example.config",
        Version = "1.0.0",
        Config = new PluginConfigContract { Schema = schema, Required = ["token"] }
    };

    [Fact]
    public void Missing_required_config_is_reported()
    {
        var manifest = ConfigurableManifest("""{"type":"object","properties":{"token":{"type":"string"}}}""");

        var errors = PluginManager.ValidateConfig(manifest, string.Empty);

        Assert.Single(errors);
        Assert.Contains("token", errors[0]);
    }

    [Fact]
    public void Invalid_json_is_reported()
    {
        var errors = PluginManager.ValidateConfig(ConfigurableManifest("{}"), "{oops");

        Assert.Single(errors);
        Assert.Contains("JSON", errors[0]);
    }

    [Fact]
    public void Schema_violation_is_reported()
    {
        var manifest = ConfigurableManifest(
            """{"type":"object","properties":{"token":{"type":"string","minLength":8}}}""");

        var errors = PluginManager.ValidateConfig(manifest, """{"token":"short"}""");

        Assert.Contains(errors, error => error.Contains("长度"));
    }

    [Fact]
    public void Valid_config_passes()
    {
        var manifest = ConfigurableManifest(
            """{"type":"object","properties":{"token":{"type":"string","minLength":4}}}""");

        Assert.Empty(PluginManager.ValidateConfig(manifest, """{"token":"abcd1234"}"""));
    }

    [Fact]
    public void Non_object_config_is_rejected()
    {
        var errors = PluginManager.ValidateConfig(ConfigurableManifest("{}"), """["nope"]""");

        Assert.Single(errors);
        Assert.Contains("JSON 对象", errors[0]);
    }

    [Fact]
    public void Audit_summary_redacts_secrets()
    {
        var summary = PluginAuditWriter.Summarize(JsonNode.Parse("""
        {
          "query": "hello",
          "apiKey": "sk-live-123",
          "nested": { "access_token": "abc", "keep": 1 }
        }
        """));

        Assert.NotNull(summary);
        Assert.Contains("hello", summary);
        Assert.DoesNotContain("sk-live-123", summary);
        Assert.DoesNotContain("abc", summary);
        Assert.Contains("***", summary);
    }

    [Fact]
    public void Audit_detail_is_truncated()
    {
        var longText = new string('x', PluginAuditWriter.MaxDetailLength * 2);

        var composed = PluginAuditWriter.Compose(longText, null);

        Assert.NotNull(composed);
        Assert.True(composed!.Length <= PluginAuditWriter.MaxDetailLength + 16);
        Assert.EndsWith("(truncated)", composed);
    }

    [Fact]
    public void Audit_compose_returns_null_when_nothing_to_say()
    {
        Assert.Null(PluginAuditWriter.Compose(null, null));
        Assert.Null(PluginAuditWriter.Compose("  ", null));
    }
}
