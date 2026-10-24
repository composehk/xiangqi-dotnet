using System.Reflection;

namespace Xiangqi.Server;

/// <summary>Build stamp (short git SHA + UTC build time) embedded at build/publish time. See StampBuildInfo in the csproj.</summary>
public sealed record BuildInfo(string Version, string BuiltAt)
{
    public const string Unknown = "unknown";

    public static BuildInfo FromAssembly(Assembly assembly)
    {
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string? builtAt = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuiltAt")?.Value;
        return new BuildInfo(ParseSha(informational), string.IsNullOrWhiteSpace(builtAt) ? Unknown : builtAt!);
    }

    /// <summary>"1.0.0+abc123def456" → "abc123def456"; no/blank revision → "unknown".</summary>
    public static string ParseSha(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
            return Unknown;
        int plus = informationalVersion.IndexOf('+');
        if (plus < 0 || plus == informationalVersion.Length - 1)
            return Unknown;
        string sha = informationalVersion[(plus + 1)..].Trim();
        // Keep only a plausible hex revision (defends the health endpoint against odd metadata).
        return sha.Length is >= 7 and <= 64 && sha.All(Uri.IsHexDigit) ? sha[..Math.Min(sha.Length, 12)].ToLowerInvariant() : Unknown;
    }
}
