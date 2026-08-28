namespace EpgOverlay.Contracts;

internal static class EpgOverlayContract
{
    public const string PluginId = "EpgOverlay";
    public const string ManifestPluginId = "epgoverlay";
    public const string PluginName = "EpgOverlay";
    public const string Version = "1.0.0";
    public const string WindowTitle = PluginName + " " + Version;
    public const string RouteSegment = "epgoverlay";
    public const string SourceKind = "ExternalEpg";
    public const string StorageNamespace = "settings";

    public const string SettingsStateKey = "state";
}
