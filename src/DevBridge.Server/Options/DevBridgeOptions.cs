namespace DevBridge.Options;

public sealed class DevBridgeOptions
{
    public const string SectionName = "DevBridge";
    public string PublicBaseUrl { get; set; } = string.Empty;
    public string DeveloperAccessKey { get; set; } = string.Empty;
    public int PairingLifetimeMinutes { get; set; } = 5;
    public int SessionLifetimeHours { get; set; } = 8;
    public int MaximumApkMegabytes { get; set; } = 500;
    public string ArtifactPath { get; set; } = "Data/Artifacts";
}
