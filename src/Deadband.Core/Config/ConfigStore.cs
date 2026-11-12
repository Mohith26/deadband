using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Deadband.Core.Config;

public sealed record ConfigVersionInfo(int Version, string Sha256, string Author, string Message, DateTimeOffset CreatedUtc, int? RollbackOf);

public sealed class ConfigValidationException(IReadOnlyList<ValidationIssue> issues)
    : Exception($"configuration rejected: {issues.Count} issue(s)")
{
    public IReadOnlyList<ValidationIssue> Issues { get; } = issues;
}

/// <summary>
/// Append-only, file-backed configuration history. Every publish writes an immutable vNNNN.json plus a manifest
/// entry with its SHA-256; rollback publishes a copy of an old version as a new version, so history is never rewritten.
/// </summary>
public sealed class ConfigStore
{
    private readonly string _dir;
    private readonly ISet<string>? _knownTags;
    private readonly object _gate = new();
    private Manifest _manifest;

    private sealed record Manifest(int Active, List<ConfigVersionInfo> Versions);

    public ConfigStore(string directory, ISet<string>? knownTags = null)
    {
        _dir = directory;
        _knownTags = knownTags;
        Directory.CreateDirectory(_dir);
        var mf = ManifestPath;
        _manifest = File.Exists(mf)
            ? JsonSerializer.Deserialize<Manifest>(File.ReadAllText(mf), AlarmConfig.Json)!
            : new Manifest(0, new List<ConfigVersionInfo>());
    }

    private string ManifestPath => Path.Combine(_dir, "manifest.json");
    private string VersionPath(int v) => Path.Combine(_dir, $"v{v:0000}.json");

    public int ActiveVersion { get { lock (_gate) return _manifest.Active; } }
    public IReadOnlyList<ConfigVersionInfo> Versions { get { lock (_gate) return _manifest.Versions.ToList(); } }
    public event Action<int, AlarmConfig>? Activated;

    public AlarmConfig Get(int version)
    {
        lock (_gate)
        {
            var info = _manifest.Versions.FirstOrDefault(v => v.Version == version)
                ?? throw new KeyNotFoundException($"config version {version} not found");
            var text = File.ReadAllText(VersionPath(version));
            if (Sha(text) != info.Sha256) throw new InvalidDataException($"config v{version} failed its integrity check (file edited outside the store)");
            return AlarmConfig.FromJson(text);
        }
    }

    public AlarmConfig GetActive() => Get(ActiveVersion);

    public ConfigVersionInfo Publish(AlarmConfig config, string author, string message, int? rollbackOf = null)
    {
        var issues = ConfigValidator.Validate(config, _knownTags);
        if (issues.Count > 0) throw new ConfigValidationException(issues);
        ConfigVersionInfo info;
        lock (_gate)
        {
            var v = _manifest.Versions.Count == 0 ? 1 : _manifest.Versions.Max(x => x.Version) + 1;
            var text = config.ToJson();
            var path = VersionPath(v);
            if (File.Exists(path)) throw new IOException($"{path} already exists; versions are immutable");
            File.WriteAllText(path, text);
            info = new ConfigVersionInfo(v, Sha(text), author, message, DateTimeOffset.UtcNow, rollbackOf);
            _manifest = new Manifest(v, _manifest.Versions.Append(info).ToList());
            WriteManifest();
        }
        Activated?.Invoke(info.Version, config);
        return info;
    }

    public ConfigVersionInfo Rollback(int toVersion, string author, string? reason = null)
    {
        var old = Get(toVersion);
        return Publish(old, author, reason ?? $"rollback to v{toVersion}", toVersion);
    }

    private void WriteManifest()
    {
        var tmp = ManifestPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_manifest, AlarmConfig.Json));
        File.Move(tmp, ManifestPath, overwrite: true);
    }

    public static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
