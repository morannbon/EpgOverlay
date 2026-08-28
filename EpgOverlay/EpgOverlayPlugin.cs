using System.Net;
using System.Text;
using EpgOverlay.Contracts;
using EpgOverlay.Parsing;
using TvAIrPlugin;
using TvAIrPlugin.Events;
using TvAIrPlugin.Pickers;
using TvAIrPlugin.Runtime;
using TvAIrPlugin.Storage;

namespace EpgOverlay;

public sealed class EpgOverlayRuntimePlugin : ITvAirRuntimeCapabilityPlugin, ITvAirRuntimeUiPlugin, ITvAirRuntimeLifecyclePlugin
{
    public TvAirPluginRuntimeDescriptor Descriptor { get; } = new()
    {
        PluginId = EpgOverlayContract.PluginId,
        DisplayName = EpgOverlayContract.PluginName,
        Version = EpgOverlayContract.Version,
        SdkContractVersion = TvAIrPluginSdkContract.SdkVersion,
        RequiredCapabilities = new[]
        {
            TvAirRuntimeCapabilities.StorageRead,
            TvAirRuntimeCapabilities.StorageWrite,
            TvAirRuntimeCapabilities.BridgeEvents
        },
        RequiredPermissions = new[]
        {
            PluginPermission.ShowUi,
            PluginPermission.OpenToolWindow,
            PluginPermission.ReadChannels,
            PluginPermission.UseActionApi,
            PluginPermission.UseWindowApi,
            PluginPermission.UseSafeEvent,
            PluginPermission.UsePathPicker,
            PluginPermission.WriteProgramGuideProjection,
            PluginPermission.ReadPluginStorage,
            PluginPermission.WritePluginStorage
        },
        Windows = new[]
        {
            new TvAIrPlugin.Windows.PluginWindowDefinition
            {
                WindowDefinitionId = "main", Title = EpgOverlayContract.WindowTitle,
                // InitialSize and MinimumSize describe the usable content area.
                // The host owns conversion to the outer window size, including frame and DPI metrics.
                InitialSize = new TvAIrPlugin.Windows.PluginWindowSize(760, 228),
                MinimumSize = new TvAIrPlugin.Windows.PluginWindowSize(680, 228),
                SizeReference = TvAIrPlugin.Windows.PluginWindowSizeReference.ContentArea,
                ShowInTaskbar = false, RememberPlacement = true,
                HorizontalScrollPolicy = TvAIrPlugin.Windows.PluginWindowAxisScrollPolicy.Hidden,
                VerticalScrollPolicy = TvAIrPlugin.Windows.PluginWindowAxisScrollPolicy.Hidden
            }
        },
        Surfaces = new[]
        {
            new TvAIrPlugin.Surfaces.PluginSurfaceDefinition
            {
                SurfaceDefinitionId = "main.web", Kind = TvAIrPlugin.Surfaces.PluginSurfaceKind.Web,
                EntryPoint = EpgOverlayContract.RouteSegment
            }
        },
        UiDefinitions = new[]
        {
            new RuntimeUiDefinition
            {
                UiDefinitionId = "main", Route = EpgOverlayContract.RouteSegment,
                Kind = RuntimeUiKind.ToolWindow, WindowDefinitionId = "main", SurfaceDefinitionId = "main.web"
            }
        },
        MenuActions = new[]
        {
            new PluginMenuActionDefinition
            {
                ActionId = "open",
                Label = EpgOverlayContract.PluginName,
                Kind = PluginMenuActionKind.ToolWindow,
                Priority = 400,
                Route = EpgOverlayContract.RouteSegment,
                WindowDefinitionId = "main",
                SurfaceDefinitionId = "main.web",
                ShowInTaskbar = false
            }
        },
        Lifecycle = new PluginLifecycleDefinition()
    };

    private readonly EpgOverlayRenderer _ui = new();

    public void Initialize(ITvAirPluginRuntimeContext context)
        => EpgOverlayRuntime.Initialize(context);

    public string RenderHtml(RuntimeUiRenderContext context)
        => _ui.RenderHtml(context);

    public Task<RuntimeUiActionResult> HandleActionAsync(RuntimeUiActionContext context, CancellationToken cancellationToken)
        => _ui.HandleActionAsync(context, cancellationToken);

    public void OnStart()
        => EpgOverlayRuntime.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

    public void OnStop()
        => EpgOverlayRuntime.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
}

internal sealed class EpgOverlayRenderer
{
    public string RenderHtml(RuntimeUiRenderContext context)
    {
        if (!EpgOverlayRuntime.IsReady)
        {
            return RenderShell("EpgOverlay", "<section class=\"panel\"><div class=\"data-box\">初期化が完了していません。</div></section>", context);
        }

        var state = EpgOverlayRuntime.LoadState();
        var result = EpgOverlayRuntime.ReadLastResult();
        var actionEndpoint = context.ActionEndpoint;
        var windowEndpoint = FirstNonEmpty(context.WindowEndpoint, context.WindowRoute);
        var actionToken = context.ActionToken;
        var windowToken = FirstNonEmpty(context.WindowToken, context.ActionToken);
        var contextPluginId = FirstNonEmpty(context.PluginId, EpgOverlayContract.ManifestPluginId);
        var contextRouteSegment = FirstNonEmpty(context.Route, EpgOverlayContract.RouteSegment);
        var windowId = context.CurrentWindowId;

        var savedEnabled = state.Enabled ? "true" : "false";
        var draft = EpgOverlayRuntime.ReadDraft(context.CurrentWindowId, state);

        var body = new StringBuilder();
        body.AppendLine("<div class=\"panel\">");

        body.AppendLine("<form method=\"post\" action=\"" + H(actionEndpoint) + "\">");
        body.AppendLine(Hidden("pluginId", contextPluginId));
        body.AppendLine(Hidden("route", contextRouteSegment));
        body.AppendLine(Hidden("routeSegment", contextRouteSegment));
        body.AppendLine(Hidden("actionToken", actionToken));
        body.AppendLine(Hidden("windowId", windowId));
        body.AppendLine(Hidden("responseMode", "refreshWindow"));
        body.AppendLine(Hidden("refreshTarget", "content"));
        body.AppendLine(Hidden("preserveScroll", "true"));
        var enabledTrueChecked = draft.Enabled ? " checked" : string.Empty;
        var enabledFalseChecked = draft.Enabled ? string.Empty : " checked";
        body.AppendLine("<table class=\"edit-table\" cellspacing=\"0\" cellpadding=\"0\"><tr><td class=\"toggle-cell\"><span class=\"toggle-radio\"><label class=\"toggle-option toggle-on\"><input type=\"radio\" name=\"enabled\" value=\"true\"" + enabledTrueChecked + "><span>有効</span></label><label class=\"toggle-option toggle-off\"><input type=\"radio\" name=\"enabled\" value=\"false\"" + enabledFalseChecked + "><span>無効</span></label></span></td><td class=\"path-cell\"><input class=\"path-input\" id=\"epgDataPath\" name=\"epgDataPath\" value=\"" + H(draft.Path) + "\" autocomplete=\"off\"></td><td class=\"browse-cell\"><button class=\"browse-button\" type=\"submit\" name=\"action\" value=\"epgoverlay.pickEpgDataPath\">参照</button></td></tr></table>");
        body.AppendLine(RenderResult(result, savedEnabled, draft.Enabled));
        body.AppendLine("<div class=\"footer action-footer\"><button class=\"secondary\" type=\"submit\" name=\"action\" value=\"epgoverlay.readEpgData\">確認</button><span class=\"right-buttons\"><button class=\"primary\" type=\"submit\" name=\"action\" value=\"epgoverlay.saveSettings\">保存</button></span></div>");
        body.AppendLine("</form>");

        if (!string.IsNullOrWhiteSpace(windowEndpoint))
        {
            body.AppendLine("<form class=\"close-form\" method=\"post\" action=\"" + H(windowEndpoint) + "\">");
            body.AppendLine(Hidden("pluginId", contextPluginId));
            body.AppendLine(Hidden("route", contextRouteSegment));
            body.AppendLine(Hidden("routeSegment", contextRouteSegment));
            body.AppendLine(Hidden("action", "closeWindow"));
            body.AppendLine(Hidden("windowId", windowId));
            body.AppendLine(Hidden("windowToken", windowToken));
            body.AppendLine(Hidden("token", windowToken));
            body.AppendLine(Hidden("responseMode", "hostHandled"));
            body.AppendLine("<button class=\"secondary\" type=\"submit\">閉じる</button>");
            body.AppendLine("</form>");
        }
        else
        {
            body.AppendLine("<div class=\"close-form\"><button class=\"secondary\" type=\"button\" onclick=\"window.close();\">閉じる</button></div>");
        }

        body.AppendLine("</div>");
        return RenderShell("EpgOverlay", body.ToString(), context);
    }

    public async Task<RuntimeUiActionResult> HandleActionAsync(RuntimeUiActionContext request, CancellationToken cancellationToken)
    {
        if (!EpgOverlayRuntime.IsReady)
        {
            return new RuntimeUiActionResult { Succeeded = false, Message = "初期化が完了していません。" };
        }

        var action = request.ActionName;
        if (string.IsNullOrWhiteSpace(action) && request.Payload.TryGetValue("action", out var payloadAction)) action = payloadAction;
        action = action?.Trim() ?? string.Empty;

        var path = ReadPayload(request, "epgDataPath").Trim();
        var enabledText = ReadPayload(request, "enabled");
        var enabled = ParseStrictEnabled(enabledText);

        if (string.Equals(action, "epgoverlay.pickEpgDataPath", StringComparison.OrdinalIgnoreCase))
        {
            await EpgOverlayRuntime.PickEpgDataPathAsync(request.CurrentWindowId, path, enabledText, cancellationToken).ConfigureAwait(false);
            return RefreshResult();
        }

        if (string.Equals(action, "epgoverlay.readEpgData", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                EpgOverlayRuntime.StoreValidationFailure(request.CurrentWindowId, enabledText, path, "EPGデータのパスが未入力です。", "パスを入力してから確認してください。");
                return RefreshResult();
            }

            EpgOverlayRuntime.ReadAndStoreOnly(request.CurrentWindowId, path, enabledText, cancellationToken);
            return RefreshResult();
        }

        if (string.Equals(action, "epgoverlay.saveSettings", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                EpgOverlayRuntime.StoreValidationFailure(request.CurrentWindowId, enabledText, path, "EPGデータのパスが未入力です。", "パスを入力してから保存してください。");
                return RefreshResult();
            }

            if (enabled)
            {
                EpgOverlayRuntime.SaveEnabledSettingsAndProjection(request.CurrentWindowId, path, cancellationToken);
            }
            else
            {
                EpgOverlayRuntime.SaveDisabledSettingsAndClear(request.CurrentWindowId, path, cancellationToken);
            }

            return RefreshResult();
        }

        return RuntimeUiActionResult.Fail("UnsupportedAction", "この操作には対応していません。");
    }

    private static RuntimeUiActionResult RefreshResult()
        => new()
        {
            Succeeded = true,
            RefreshRequested = true,
            RefreshTarget = "content",
            PreserveScroll = true
        };

    private static string RenderResult(EpgOverlayResult result, string savedEnabled, bool draftEnabled)
    {
        if (result.TryGet("userMessage", out var userMessage) && !string.IsNullOrWhiteSpace(userMessage))
        {
            var userAction = result.Get("userAction", string.Empty);
            return $@"
<div class=""data-box message-box"" role=""alert""><strong>{H(userMessage)}</strong><span>{H(userAction)}</span></div>";
        }

        var projection = result.Get("projection", savedEnabled == "false" ? "cleared" : "-");
        var accepted = result.Get("accepted", projection == "cleared" ? "0" : "-");
        return $@"
<div class=""data-box""><div class=""raw-grid""><div><span>enabled</span><strong>{H(savedEnabled)}</strong></div><div><span>draftEnabled</span><strong>{H(draftEnabled ? "true" : "false")}</strong></div><div><span>operation</span><strong>{H(result.Get("operation", "-"))}</strong></div><div><span>status</span><strong>{H(result.Get("status", "-"))}</strong></div><div><span>projection</span><strong>{H(projection)}</strong></div><div><span>accepted</span><strong>{H(accepted)}</strong></div><div><span>services</span><strong>{H(result.Get("services", "-"))}</strong></div><div><span>events</span><strong>{H(result.Get("events", "-"))}</strong></div><div><span>titles</span><strong>{H(result.Get("titles", "-"))}</strong></div><div><span>genres</span><strong>{H(result.Get("genres", "-"))}</strong></div><div><span>readAt</span><strong>{H(result.Get("readAt", "-"))}</strong></div></div></div>";
    }

    private static string RenderShell(string title, string body, RuntimeUiRenderContext context)
    {
        // ThemeContract v2 is the Runtime UI color source of truth. The plugin owns layout only;
        // semantic colors for normal, selected, primary and secondary states come from the host.
        // HostEffectiveTheme is used only as a compatibility fallback when ThemeContract v2 is unavailable.
        var themeCss = BuildThemeCss(context);

        return """
<!doctype html>
<html lang="ja">
<head>
<meta charset="utf-8">
<title>__TITLE__</title>
<style>
html, body.epgoverlay-root { margin:0; padding:0; width:100%; height:100%; overflow:hidden; font-family:"Segoe UI","Yu Gothic UI",Meiryo,sans-serif; font-size:13px; }
.epgoverlay-root, .epgoverlay-root * { box-sizing:border-box; }
.epgoverlay-shell { position:fixed; left:0; top:0; right:0; bottom:0; margin:0; padding:0; overflow:hidden; }
.panel { position:absolute; left:0; top:0; right:0; bottom:0; margin:0; border:1px solid; border-radius:8px; padding:8px 8px 46px; box-shadow:0 1px 2px rgba(0,0,0,.08); }
.edit-table { width:100%; table-layout:fixed; border-collapse:collapse; margin:0; }
.toggle-cell { width:112px; padding:0 8px 0 0; vertical-align:middle; }
.path-cell { padding:0 8px 0 0; }
.browse-cell { width:92px; padding:0; }
.path-input { width:100%; height:32px; box-sizing:border-box; padding:5px 8px; border:1px solid; border-radius:4px; font-family:Consolas,"Yu Gothic UI",monospace; }
.toggle-radio { display:block; width:104px; height:30px; padding:3px; border:1px solid; border-radius:15px; box-sizing:border-box; white-space:nowrap; }
.toggle-option { float:left; display:block; width:48px; height:22px; margin:0; padding:0; cursor:pointer; overflow:hidden; }
.toggle-option input { position:absolute; left:-9999px; top:auto; width:1px; height:1px; overflow:hidden; }
.toggle-option span { display:block; height:22px; line-height:22px; text-align:center; border-radius:11px; font-size:12px; }
.data-box { margin-top:7px; padding:9px; height:118px; border:1px solid; border-radius:6px; box-sizing:border-box; overflow:hidden; }
.message-box { display:flex; flex-direction:column; justify-content:center; align-items:center; text-align:center; }
.message-box strong { display:block; font-size:14px; line-height:22px; }
.message-box span { display:block; margin-top:4px; line-height:18px; }
.raw-grid { overflow:hidden; }
.raw-grid div { float:left; width:20%; margin:0 0 5px; padding-right:8px; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
.raw-grid span { display:block; font-size:11px; line-height:13px; }
.raw-grid strong { display:block; font-weight:600; line-height:17px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.footer { position:absolute; left:8px; right:72px; bottom:8px; padding-top:7px; border-top:1px solid; }
.right-buttons { float:right; }
.close-form { position:absolute; right:8px; bottom:8px; margin:0; padding-top:7px; }
.epgoverlay-root button { min-width:88px; height:30px; margin:0; padding:0 12px; font-family:"Segoe UI","Yu Gothic UI",Meiryo,sans-serif; cursor:pointer; }
.epgoverlay-root button.browse-button { display:block; width:100%; min-width:0; height:32px; line-height:30px; padding:0; text-align:center; border:1px solid; border-radius:4px; box-sizing:border-box; }
.path-input:focus, .epgoverlay-root button.browse-button:focus, .epgoverlay-root button:focus { outline:2px solid; outline-offset:1px; }
.epgoverlay-root button.secondary { border:1px solid; border-radius:4px; }
.epgoverlay-root button.primary { border:1px solid; border-radius:4px; font-weight:600; }
__THEME_CSS__
</style>
</head>
<body class="epgoverlay-root"><div class="epgoverlay-shell">__BODY__</div></body>
</html>
""".Replace("__TITLE__", H(title)).Replace("__BODY__", body).Replace("__THEME_CSS__", themeCss);
    }

    private static string BuildThemeCss(RuntimeUiRenderContext context)
    {
        var theme = context.ThemeContract;
        if (theme.TryGetValue("contractVersion", out var version) && string.Equals(version?.Trim(), "2", StringComparison.Ordinal))
        {
            string Role(string key, string compatibilityKey)
            {
                if (theme.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value.Trim();
                if (theme.TryGetValue(compatibilityKey, out var compatibilityValue) && !string.IsNullOrWhiteSpace(compatibilityValue)) return compatibilityValue.Trim();
                return "inherit";
            }

            var pageBackground = Role("pageBackground", "surfaceBackground");
            var surfaceBackground = Role("surfaceBackground", "pageBackground");
            var subtleBackground = Role("subtleBackground", "surfaceBackground");
            var inputBackground = Role("inputBackground", "surfaceBackground");
            var text = Role("text", "controlText");
            var mutedText = Role("mutedText", "text");
            var border = Role("border", "controlBorder");
            var focus = Role("focus", "accent");
            var controlBackground = Role("controlBackground", "surfaceBackground");
            var controlText = Role("controlText", "text");
            var controlBorder = Role("controlBorder", "border");
            var controlHoverBackground = Role("controlHoverBackground", "controlBackground");
            var controlHoverText = Role("controlHoverText", "controlText");
            var controlHoverBorder = Role("controlHoverBorder", "controlBorder");
            var selectedBackground = Role("selectedBackground", "accent");
            var selectedText = Role("selectedText", "accentText");
            var selectedBorder = Role("selectedBorder", "accent");
            var selectedHoverBackground = Role("selectedHoverBackground", "selectedBackground");
            var selectedHoverText = Role("selectedHoverText", "selectedText");
            var selectedHoverBorder = Role("selectedHoverBorder", "selectedBorder");
            var primaryBackground = Role("primaryActionBackground", "accent");
            var primaryText = Role("primaryActionText", "accentText");
            var primaryBorder = Role("primaryActionBorder", "accent");
            var primaryHoverBackground = Role("primaryActionHoverBackground", "primaryActionBackground");
            var primaryHoverText = Role("primaryActionHoverText", "primaryActionText");
            var primaryHoverBorder = Role("primaryActionHoverBorder", "primaryActionBorder");
            var secondaryBackground = Role("secondaryActionBackground", "controlBackground");
            var secondaryText = Role("secondaryActionText", "controlText");
            var secondaryBorder = Role("secondaryActionBorder", "controlBorder");
            var secondaryHoverBackground = Role("secondaryActionHoverBackground", "controlHoverBackground");
            var secondaryHoverText = Role("secondaryActionHoverText", "controlHoverText");
            var secondaryHoverBorder = Role("secondaryActionHoverBorder", "controlHoverBorder");

            return $$"""
html, body.epgoverlay-root { background:{{pageBackground}}; color:{{text}}; }
.panel { background:{{surfaceBackground}}; border-color:{{border}}; }
.path-input { border-color:{{controlBorder}}; background:{{inputBackground}}; color:{{text}}; }
.toggle-radio { border-color:{{controlBorder}}; background:{{controlBackground}}; }
.toggle-option span { color:{{controlText}}; background:{{controlBackground}}; }
.toggle-option:hover span { color:{{controlHoverText}}; background:{{controlHoverBackground}}; box-shadow:inset 0 0 0 1px {{controlHoverBorder}}; }
.toggle-option input:checked + span { background:{{selectedBackground}}; color:{{selectedText}}; box-shadow:inset 0 0 0 1px {{selectedBorder}}; }
.toggle-option:hover input:checked + span { background:{{selectedHoverBackground}}; color:{{selectedHoverText}}; box-shadow:inset 0 0 0 1px {{selectedHoverBorder}}; }
.data-box { border-color:{{border}}; background:{{subtleBackground}}; }
.message-box span, .raw-grid span { color:{{mutedText}}; }
.raw-grid strong { color:{{text}}; }
.footer { border-top-color:{{border}}; }
.path-input:focus, .epgoverlay-root button.browse-button:focus, .epgoverlay-root button:focus { outline-color:{{focus}}; }
.epgoverlay-root button.browse-button, .epgoverlay-root button.secondary { background:{{secondaryBackground}}; color:{{secondaryText}}; border-color:{{secondaryBorder}}; }
.epgoverlay-root button.browse-button:hover, .epgoverlay-root button.secondary:hover { background:{{secondaryHoverBackground}}; color:{{secondaryHoverText}}; border-color:{{secondaryHoverBorder}}; }
.epgoverlay-root button.primary { background:{{primaryBackground}}; color:{{primaryText}}; border-color:{{primaryBorder}}; }
.epgoverlay-root button.primary:hover { background:{{primaryHoverBackground}}; color:{{primaryHoverText}}; border-color:{{primaryHoverBorder}}; }
""";
        }

        var isDark = string.Equals(context.HostEffectiveTheme?.Trim(), "dark", StringComparison.OrdinalIgnoreCase);
        return isDark
            ? """
html, body.epgoverlay-root { background:#1b1d21; color:#f3f4f6; }
.panel { background:#24272d; border-color:#454b55; }
.path-input { border-color:#687181; background:#1f2227; color:#f3f4f6; }
.epgoverlay-root button.browse-button { border-color:#687181; background:#2d3138; color:#f3f4f6; }
.toggle-radio { border-color:#687181; background:#30343b; }
.toggle-option span { color:#f3f4f6; }
.toggle-option input:checked + span { background:#6f9cff; color:#fff; }
.toggle-off input:checked + span { background:#6b7280; color:#fff; }
.data-box { border-color:#454b55; background:#202329; }
.message-box span { color:#c3cad5; }
.raw-grid span { color:#aab2c0; }
.raw-grid strong { color:#f3f4f6; }
.footer { border-top-color:#3f444d; }
.path-input:focus, .epgoverlay-root button.browse-button:focus, .epgoverlay-root button:focus { outline-color:#8eb0f7; }
.epgoverlay-root button.secondary { background:#2d3138; color:#f3f4f6; border-color:#687181; }
.epgoverlay-root button.primary { background:#33466f; color:#f3f4f6; border-color:#7896d6; }
"""
            : """
html, body.epgoverlay-root { background:#eef0f4; color:#151515; }
.panel { background:#fff; border-color:#c9ced8; }
.path-input { border-color:#8e96a7; background:#fff; color:#151515; }
.epgoverlay-root button.browse-button { border-color:#8e96a7; background:#f4f5f7; color:#151515; }
.toggle-radio { border-color:#8e96a7; background:#eef1f6; }
.toggle-option span { color:#111827; }
.toggle-option input:checked + span { background:#2f6fec; color:#fff; }
.toggle-off input:checked + span { background:#6b7280; color:#fff; }
.data-box { border-color:#d4d8e1; background:#fafbfc; }
.message-box span { color:#4b5563; }
.raw-grid span { color:#667085; }
.raw-grid strong { color:#202939; }
.footer { border-top-color:#e5e7ed; }
.path-input:focus, .epgoverlay-root button.browse-button:focus, .epgoverlay-root button:focus { outline-color:#8eb0f7; }
.epgoverlay-root button.secondary { background:#f7f7f8; color:#151515; border-color:#8f96a3; }
.epgoverlay-root button.primary { background:#e7eefc; color:#151515; border-color:#6e86bd; }
""";
    }

    private static string Hidden(string name, string value) => "<input type=\"hidden\" name=\"" + H(name) + "\" value=\"" + H(value) + "\">";

    private static string ReadPayload(RuntimeUiActionContext request, params string[] names)
    {
        foreach (var name in names)
        {
            if (request.Payload.TryGetValue(name, out var value)) return value ?? string.Empty;
        }
        return string.Empty;
    }

    private static bool ParseStrictEnabled(string value)
        => string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return string.Empty;
    }

    private static string H(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}

internal static class EpgOverlayRuntime
{
    private static readonly object SyncRoot = new();
    private static readonly EpgOverlayDataReader Reader = new();
    private static readonly List<IDisposable> EventSubscriptions = new();
    private static readonly SemaphoreSlim ProjectionGate = new(1, 1);
    private static ITvAirPluginRuntimeContext? _context;
    private static CancellationTokenSource? _lifecycleCancellation;
    private static readonly Dictionary<string, EpgOverlayDraftState> DraftByWindowId = new(StringComparer.Ordinal);
    private static string _lastResult = string.Empty;
    private static int _started;
    private static long _lifecycleGeneration;
    private static long _refreshRequestVersion;
    private static long _refreshHandledVersion;
    private static bool _refreshWorkerActive;
    private static long _refreshWorkerGeneration;
    private static string _latestRefreshTrigger = string.Empty;
    private static string _lastProjectedSourceRevision = string.Empty;

    public static bool IsReady => _context is not null && Volatile.Read(ref _started) == 1;

    public static void Initialize(ITvAirPluginRuntimeContext context)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));

        // Invalidate and cancel the previous lifecycle first. The gate then waits for any
        // in-flight projection operation to leave its commit section before the context is replaced.
        lock (SyncRoot)
        {
            Interlocked.Increment(ref _lifecycleGeneration);
            CancelLifecycleLocked();
        }

        ProjectionGate.Wait();
        try
        {
            lock (SyncRoot)
            {
                DisposeSubscriptions();
                _context = context;
                DraftByWindowId.Clear();
                _lastResult = string.Empty;
                _latestRefreshTrigger = string.Empty;
                _lastProjectedSourceRevision = string.Empty;
                Volatile.Write(ref _refreshHandledVersion, Volatile.Read(ref _refreshRequestVersion));
                Volatile.Write(ref _started, 0);
            }
        }
        finally
        {
            ProjectionGate.Release();
        }

        // Initialize only establishes the runtime context. Startup work belongs to
        // the host-owned Runtime lifecycle and begins from OnStart/StartAsync.
    }

    public static Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            var context = RequireContext();
            CancellationToken lifecycleToken;
            lock (SyncRoot)
            {
                CancelLifecycleLocked();
                _lifecycleCancellation = new CancellationTokenSource();
                lifecycleToken = _lifecycleCancellation.Token;
                DisposeSubscriptions();
                try
                {
                    SubscribeRefreshTriggers(context);
                }
                catch (Exception ex)
                {
                    DisposeSubscriptions();
                    StoreRefreshFailure(string.Empty, "runtime-subscribe", ex.Message);
                }
            }

            SafeRefreshExternalEvents("runtime-start", lifecycleToken);
        }
        return Task.CompletedTask;
    }

    public static Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Cancel first so queued work becomes obsolete, then wait for the projection gate.
        // Once StopAsync returns, no operation from the previous lifecycle can still commit.
        lock (SyncRoot)
        {
            Interlocked.Increment(ref _lifecycleGeneration);
            CancelLifecycleLocked();
        }

        ProjectionGate.Wait(cancellationToken);
        try
        {
            lock (SyncRoot)
            {
                DisposeSubscriptions();
                DraftByWindowId.Clear();
                _lastResult = string.Empty;
                _latestRefreshTrigger = string.Empty;
                _lastProjectedSourceRevision = string.Empty;
                Volatile.Write(ref _refreshHandledVersion, Volatile.Read(ref _refreshRequestVersion));
                Volatile.Write(ref _started, 0);
            }
        }
        finally
        {
            ProjectionGate.Release();
        }
        return Task.CompletedTask;
    }

    private static void SubscribeRefreshTriggers(ITvAirPluginRuntimeContext context)
    {
        EventSubscriptions.Add(context.Events.Subscribe(nameof(TvAirEventType.ProgramGuideUpdated), evt =>
        {
            if (IsOwnProgramGuideUpdate(evt)) return;
            RequestBackgroundRefresh("program-guide-updated");
        }));
        EventSubscriptions.Add(context.Events.Subscribe(nameof(TvAirEventType.EpgCompleted), _ =>
        {
            RequestBackgroundRefresh("epg-completed");
        }));
        EventSubscriptions.Add(context.Events.Subscribe(nameof(TvAirEventType.RuntimeWindowLifecycleChanged), evt =>
        {
            var lifecycle = (evt.Payload as TvAirEventDto)?.RuntimeWindowLifecycle;
            if (lifecycle is null) return;
            if (!string.Equals(lifecycle.PluginId, EpgOverlayContract.PluginId, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(lifecycle.PluginId, EpgOverlayContract.ManifestPluginId, StringComparison.OrdinalIgnoreCase))
                return;
            if (lifecycle.State is not TvAIrPlugin.Windows.PluginWindowLifecycleState.Closing
                and not TvAIrPlugin.Windows.PluginWindowLifecycleState.Closed)
                return;

            ClearDraft(lifecycle.WindowInstanceId);
        }));
    }

    private static void RequestBackgroundRefresh(string trigger)
    {
        if (Volatile.Read(ref _started) != 1) return;

        lock (SyncRoot)
        {
            if (_lifecycleCancellation is null || _lifecycleCancellation.IsCancellationRequested) return;
            _latestRefreshTrigger = trigger;
            Interlocked.Increment(ref _refreshRequestVersion);
        }

        EnsureBackgroundRefreshWorker();
    }

    private static void EnsureBackgroundRefreshWorker()
    {
        long lifecycleGeneration;
        CancellationToken lifecycleToken;
        lock (SyncRoot)
        {
            if (Volatile.Read(ref _started) != 1
                || _lifecycleCancellation is null
                || _lifecycleCancellation.IsCancellationRequested)
                return;

            lifecycleGeneration = Volatile.Read(ref _lifecycleGeneration);
            if (_refreshWorkerActive && _refreshWorkerGeneration == lifecycleGeneration) return;

            _refreshWorkerActive = true;
            _refreshWorkerGeneration = lifecycleGeneration;
            lifecycleToken = _lifecycleCancellation.Token;
        }

        _ = Task.Run(() => RunBackgroundRefreshWorker(lifecycleGeneration, lifecycleToken));
    }

    private static void RunBackgroundRefreshWorker(long lifecycleGeneration, CancellationToken lifecycleToken)
    {
        try
        {
            while (!lifecycleToken.IsCancellationRequested
                   && lifecycleGeneration == Volatile.Read(ref _lifecycleGeneration)
                   && Volatile.Read(ref _started) == 1)
            {
                var requestedVersion = Volatile.Read(ref _refreshRequestVersion);
                if (requestedVersion <= Volatile.Read(ref _refreshHandledVersion)) break;

                string trigger;
                lock (SyncRoot) trigger = _latestRefreshTrigger;

                SafeRefreshExternalEvents(trigger, lifecycleToken);
                Volatile.Write(ref _refreshHandledVersion, requestedVersion);
            }
        }
        finally
        {
            var shouldRestart = false;
            lock (SyncRoot)
            {
                // An obsolete lifecycle worker must never clear the ownership state of a newer one.
                if (_refreshWorkerGeneration == lifecycleGeneration)
                {
                    _refreshWorkerActive = false;
                    shouldRestart = !lifecycleToken.IsCancellationRequested
                        && lifecycleGeneration == Volatile.Read(ref _lifecycleGeneration)
                        && Volatile.Read(ref _started) == 1
                        && Volatile.Read(ref _refreshRequestVersion) > Volatile.Read(ref _refreshHandledVersion);
                }
            }

            // Close the request/worker-exit race. At most one successor can claim this lifecycle.
            if (shouldRestart) EnsureBackgroundRefreshWorker();
        }
    }

    private static void SafeRefreshExternalEvents(string trigger, CancellationToken cancellationToken)
    {
        try
        {
            RefreshExternalEvents(trigger, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Lifecycle stop/reinitialize cancels obsolete work; cancellation is not a plugin failure.
        }
        catch (Exception ex)
        {
            StoreRefreshFailure(string.Empty, trigger, ex.Message);
        }
    }

    private static CancellationToken GetLifecycleToken()
    {
        lock (SyncRoot) return _lifecycleCancellation?.Token ?? new CancellationToken(true);
    }

    private static CancellationTokenSource CreateOperationCancellation(CancellationToken callerToken)
    {
        var lifecycleToken = GetLifecycleToken();
        return CancellationTokenSource.CreateLinkedTokenSource(callerToken, lifecycleToken);
    }

    private static void CancelLifecycleLocked()
    {
        if (_lifecycleCancellation is null) return;
        try { _lifecycleCancellation.Cancel(); } catch { }
        _lifecycleCancellation.Dispose();
        _lifecycleCancellation = null;
    }

    private static void DisposeSubscriptions()
    {
        foreach (var subscription in EventSubscriptions) subscription.Dispose();
        EventSubscriptions.Clear();
    }

    private static bool IsOwnProgramGuideUpdate(PluginEventEnvelope evt)
        => evt is not null
        && (string.Equals(evt.SourceOwnerId, EpgOverlayContract.PluginId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(evt.SourceOwnerId, EpgOverlayContract.ManifestPluginId, StringComparison.OrdinalIgnoreCase));

    public static EpgOverlayState LoadState()
    {
        var context = RequireContext();
        var raw = ReadString(context.Storage, EpgOverlayContract.SettingsStateKey);
        if (string.IsNullOrWhiteSpace(raw))
            return new EpgOverlayState(false, string.Empty);

        try
        {
            var stored = System.Text.Json.JsonSerializer.Deserialize<StoredSettingsState>(raw);
            return stored is null
                ? new EpgOverlayState(false, string.Empty)
                : new EpgOverlayState(stored.Enabled, stored.EpgDataPath?.Trim() ?? string.Empty);
        }
        catch (System.Text.Json.JsonException)
        {
            return new EpgOverlayState(false, string.Empty);
        }
    }

    public static void SaveSettings(bool enabled, string path)
    {
        var context = RequireContext();
        var normalizedPath = path?.Trim() ?? string.Empty;
        var json = System.Text.Json.JsonSerializer.Serialize(new StoredSettingsState(enabled, normalizedPath));
        WriteValue(context.Storage, EpgOverlayContract.SettingsStateKey, json);
    }

    public static EpgOverlayDraftState ReadDraft(string windowId, EpgOverlayState savedState)
    {
        if (string.IsNullOrWhiteSpace(windowId))
            return new EpgOverlayDraftState(savedState.Enabled, savedState.Path);

        // Rendering is a projection of the current ToolWindow draft, not a commit boundary.
        // Keep the draft until a save succeeds so refreshes caused by Confirm/Picker preserve
        // both the enabled selection and path exactly as the user entered them.
        lock (SyncRoot)
        {
            if (DraftByWindowId.TryGetValue(windowId, out var draft)) return draft;
        }
        return new EpgOverlayDraftState(savedState.Enabled, savedState.Path);
    }

    public static void ClearDraft(string windowId)
    {
        if (string.IsNullOrWhiteSpace(windowId)) return;
        lock (SyncRoot) DraftByWindowId.Remove(windowId);
    }

    private static void StoreDraft(string windowId, bool enabled, string path)
    {
        if (string.IsNullOrWhiteSpace(windowId)) return;
        lock (SyncRoot)
        {
            // Draft lifetime follows the Host-owned Runtime ToolWindow lifecycle.
            // RuntimeWindowLifecycleChanged removes this entry at Closing/Closed.
            DraftByWindowId[windowId] = new EpgOverlayDraftState(enabled, path?.Trim() ?? string.Empty);
        }
    }

    public static bool RefreshExternalEvents(string trigger, CancellationToken cancellationToken = default)
    {
        using var operationCancellation = CreateOperationCancellation(cancellationToken);
        var token = operationCancellation.Token;
        ProjectionGate.Wait(token);
        var lifecycleGeneration = Volatile.Read(ref _lifecycleGeneration);
        try
        {
            token.ThrowIfCancellationRequested();
            if (!IsProjectionOperationCurrent(lifecycleGeneration)) return false;

            var context = RequireReadyContext();
            var state = LoadState();
            if (!state.Enabled)
            {
                if (!IsProjectionOperationCurrent(lifecycleGeneration)) return false;
                return ClearProjectionCore("disabled:" + trigger, context);
            }

            var path = state.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                WriteResult("operation=refresh|status=path-empty|projection=unchanged|accepted=|rejected=");
                return false;
            }

            var sourceRevision = Reader.GetSourceRevision(path);
            if (IsAutomaticRefreshTrigger(trigger)
                && !string.IsNullOrEmpty(sourceRevision)
                && string.Equals(sourceRevision, _lastProjectedSourceRevision, StringComparison.Ordinal))
            {
                return true;
            }

            LocalEpgDataReadResult read;
            try
            {
                read = Reader.Read(path, token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                StoreRefreshFailure(path, trigger, ex.Message);
                return false;
            }

            if (!IsProjectionOperationCurrent(lifecycleGeneration)) return false;
            if (!read.Ok)
            {
                StoreReadResult(read, null, "refresh", "read-failed:" + trigger, "unchanged");
                return false;
            }

            var serviceNames = LoadServiceNames(context);
            if (!IsProjectionOperationCurrent(lifecycleGeneration)) return false;
            if (serviceNames.Count == 0)
            {
                StoreReadResult(read, null, "refresh", "channels-empty:" + trigger, "unchanged");
                return false;
            }

            var events = Reader.CreateExternalEvents(read, EpgOverlayContract.SourceKind, serviceNames);
            TvAirExternalProgramGuideReplaceResultDto result;
            try
            {
                token.ThrowIfCancellationRequested();
                if (!IsProjectionOperationCurrent(lifecycleGeneration)) return false;
                result = context.ExternalProgramSource.ReplaceSnapshot(events);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                StoreRefreshFailure(path, trigger, ex.Message);
                return false;
            }

            if (!IsProjectionOperationCurrent(lifecycleGeneration)) return false;
            var stored = StoreProjectionResult(read, result, "refresh", trigger);
            if (stored) _lastProjectedSourceRevision = read.SourceRevision;
            return stored;
        }
        finally
        {
            ProjectionGate.Release();
        }
    }

    public static bool SaveEnabledSettingsAndProjection(string windowId, string path, CancellationToken cancellationToken)
    {
        using var operationCancellation = CreateOperationCancellation(cancellationToken);
        var token = operationCancellation.Token;
        ProjectionGate.Wait(token);
        var lifecycleGeneration = Volatile.Read(ref _lifecycleGeneration);
        try
        {
            token.ThrowIfCancellationRequested();
            if (!IsProjectionOperationCurrent(lifecycleGeneration)) return false;

            var context = RequireReadyContext();
            var normalizedPath = path?.Trim() ?? string.Empty;
            var previousState = LoadState();
            StoreDraft(windowId, true, normalizedPath);

            LocalEpgDataReadResult read;
            try
            {
                read = Reader.Read(normalizedPath, token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                StoreRefreshFailure(normalizedPath, "save-enabled-read", ex.Message);
                return false;
            }

            if (!IsProjectionOperationCurrent(lifecycleGeneration)) return false;
            if (!read.Ok)
            {
                StoreReadResult(read, null, "save", "read-failed", "unchanged");
                return false;
            }

            var serviceNames = LoadServiceNames(context);
            if (!IsProjectionOperationCurrent(lifecycleGeneration)) return false;
            if (serviceNames.Count == 0)
            {
                StoreReadResult(read, null, "save", "channels-empty", "unchanged");
                return false;
            }

            var events = Reader.CreateExternalEvents(read, EpgOverlayContract.SourceKind, serviceNames);
            if (!IsProjectionOperationCurrent(lifecycleGeneration)) return false;

            try
            {
                SaveSettings(true, normalizedPath);
            }
            catch (Exception ex)
            {
                StoreSettingsFailure("save-enabled", ex.Message);
                return false;
            }

            TvAirExternalProgramGuideReplaceResultDto result;
            try
            {
                token.ThrowIfCancellationRequested();
                if (!IsProjectionOperationCurrent(lifecycleGeneration))
                {
                    RestoreSettingsBestEffort(previousState);
                    return false;
                }
                result = context.ExternalProgramSource.ReplaceSnapshot(events);
            }
            catch (OperationCanceledException)
            {
                RestoreSettingsBestEffort(previousState);
                throw;
            }
            catch (Exception ex)
            {
                var rollback = RestoreSettingsBestEffort(previousState);
                StoreProjectionException("save-enabled", ex.Message, rollback);
                return false;
            }

            if (!IsProjectionOperationCurrent(lifecycleGeneration)) return false;
            if (!result.Accepted)
            {
                var rollback = RestoreSettingsBestEffort(previousState);
                StoreReadResult(read, result, "save",
                    "rejected" + AppendMessage(result.Message) + AppendRollback(rollback), "unchanged");
                return false;
            }

            ClearDraft(windowId);
            _lastProjectedSourceRevision = read.SourceRevision;
            StoreReadResult(read, result, "save",
                result.RejectedCount > 0 ? "registered-partial" + AppendMessage(result.Message) : "registered",
                result.RejectedCount > 0 ? "registered-partial" : "registered");
            return true;
        }
        finally
        {
            ProjectionGate.Release();
        }
    }

    public static bool SaveDisabledSettingsAndClear(string windowId, string path, CancellationToken cancellationToken)
    {
        using var operationCancellation = CreateOperationCancellation(cancellationToken);
        var token = operationCancellation.Token;
        ProjectionGate.Wait(token);
        var lifecycleGeneration = Volatile.Read(ref _lifecycleGeneration);
        try
        {
            token.ThrowIfCancellationRequested();
            if (!IsProjectionOperationCurrent(lifecycleGeneration)) return false;

            var context = RequireReadyContext();
            var normalizedPath = path?.Trim() ?? string.Empty;
            var previousState = LoadState();
            StoreDraft(windowId, false, normalizedPath);

            try
            {
                SaveSettings(false, normalizedPath);
            }
            catch (Exception ex)
            {
                StoreSettingsFailure("save-disabled", ex.Message);
                return false;
            }

            try
            {
                token.ThrowIfCancellationRequested();
                if (!IsProjectionOperationCurrent(lifecycleGeneration))
                {
                    RestoreSettingsBestEffort(previousState);
                    return false;
                }
                context.ExternalProgramSource.ClearSnapshot();
            }
            catch (OperationCanceledException)
            {
                RestoreSettingsBestEffort(previousState);
                throw;
            }
            catch (Exception ex)
            {
                var rollback = RestoreSettingsBestEffort(previousState);
                StoreProjectionException("save-disabled", ex.Message, rollback);
                return false;
            }

            if (!IsProjectionOperationCurrent(lifecycleGeneration)) return false;
            ClearDraft(windowId);
            _lastProjectedSourceRevision = string.Empty;
            WriteResult(string.Join("|", new[]
            {
                "operation=save-disabled",
                "status=saved",
                "projection=cleared",
                "accepted=0",
                "rejected=0"
            }));
            return true;
        }
        finally
        {
            ProjectionGate.Release();
        }
    }

    private static bool IsAutomaticRefreshTrigger(string trigger)
        => string.Equals(trigger, "program-guide-updated", StringComparison.Ordinal)
           || string.Equals(trigger, "epg-completed", StringComparison.Ordinal);

    private static bool ClearProjectionCore(string operation, ITvAirPluginRuntimeContext context)
    {
        try
        {
            context.ExternalProgramSource.ClearSnapshot();
            _lastProjectedSourceRevision = string.Empty;
            WriteResult(string.Join("|", new[]
            {
                "operation=" + Sanitize(operation),
                "status=cleared",
                "projection=cleared",
                "accepted=0",
                "rejected=0"
            }));
            return true;
        }
        catch (Exception ex)
        {
            StoreProjectionException(operation, ex.Message, null);
            return false;
        }
    }

    private static bool IsProjectionOperationCurrent(long lifecycleGeneration)
        => Volatile.Read(ref _started) == 1 && Volatile.Read(ref _lifecycleGeneration) == lifecycleGeneration;

    private static bool StoreProjectionResult(LocalEpgDataReadResult read, TvAirExternalProgramGuideReplaceResultDto result, string operation, string trigger)
    {
        if (!result.Accepted)
        {
            StoreReadResult(read, result, operation, "rejected:" + trigger + AppendMessage(result.Message), "unchanged");
            return false;
        }

        var partial = result.RejectedCount > 0;
        StoreReadResult(read, result, operation,
            (partial ? "registered-partial:" : "registered:") + trigger + AppendMessage(result.Message),
            partial ? "registered-partial" : "registered");
        return true;
    }

    private static bool? RestoreSettingsBestEffort(EpgOverlayState state)
    {
        try
        {
            SaveSettings(state.Enabled, state.Path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string AppendRollback(bool? rollback)
        => rollback is null ? string.Empty : rollback.Value ? ":settings-restored" : ":settings-restore-failed";

    private static string AppendMessage(string? message)
        => string.IsNullOrWhiteSpace(message) ? string.Empty : ":" + Sanitize(message);

    public static void StoreValidationFailure(string windowId, string enabledText, string path, string message, string action)
    {
        var enabled = NormalizeEnabled(enabledText);
        StoreDraft(windowId, enabled, path);
        WriteResult(string.Join("|", new[]
        {
            "operation=validation",
            "status=path-empty",
            "projection=unchanged",
            "userMessage=" + Sanitize(message),
            "userAction=" + Sanitize(action)
        }));
    }

    public static void StoreRefreshFailure(string path, string trigger, string message)
    {
        WriteResult(string.Join("|", new[]
        {
            "operation=refresh",
            "status=exception:" + Sanitize(trigger) + ":" + Sanitize(message),
            "projection=unchanged",
            "accepted=",
            "rejected="
        }));
    }

    private static void StoreSettingsFailure(string operation, string message)
    {
        WriteResult(string.Join("|", new[]
        {
            "operation=" + Sanitize(operation),
            "status=settings-save-failed:" + Sanitize(message),
            "projection=unchanged",
            "accepted=",
            "rejected="
        }));
    }

    private static void StoreProjectionException(string operation, string message, bool? settingsRollback)
    {
        WriteResult(string.Join("|", new[]
        {
            "operation=" + Sanitize(operation),
            "status=projection-exception:" + Sanitize(message) + AppendRollback(settingsRollback),
            "projection=unchanged",
            "accepted=",
            "rejected="
        }));
    }

    public static async Task PickEpgDataPathAsync(string windowId, string currentPath, string enabledText, CancellationToken cancellationToken)
    {
        var enabled = NormalizeEnabled(enabledText);
        StoreDraft(windowId, enabled, currentPath ?? string.Empty);

        var context = RequireReadyContext();
        PluginPathPickerResult picked;
        try
        {
            picked = await context.PathPicker.PickFileAsync(
                new PluginFilePickerRequest
                {
                    Title = "EPGデータファイルを選択",
                    InitialPath = string.IsNullOrWhiteSpace(currentPath) ? null : currentPath.Trim(),
                    OwnerWindowId = string.IsNullOrWhiteSpace(windowId) ? null : windowId,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            WriteResult(string.Join("|", new[]
            {
                "operation=pick-path",
                "status=picker-exception:" + Sanitize(ex.Message),
                "projection=unchanged",
                "userMessage=EPGデータのパスを選択できませんでした。",
                "userAction=パスを直接入力するか、もう一度参照してください。"
            }));
            return;
        }

        if (picked.Cancelled) return;

        if (!picked.Accepted || string.IsNullOrWhiteSpace(picked.SelectedPath))
        {
            WriteResult(string.Join("|", new[]
            {
                "operation=pick-path",
                "status=picker-failed:" + Sanitize(picked.ErrorCode) + AppendMessage(picked.Message),
                "projection=unchanged",
                "userMessage=EPGデータのパスを選択できませんでした。",
                "userAction=パスを直接入力するか、もう一度参照してください。"
            }));
            return;
        }

        StoreDraft(windowId, enabled, picked.SelectedPath.Trim());
    }

    public static void ReadAndStoreOnly(string windowId, string path, string enabledText, CancellationToken cancellationToken)
    {
        var enabled = NormalizeEnabled(enabledText);
        StoreDraft(windowId, enabled, path);
        try
        {
            using var operationCancellation = CreateOperationCancellation(cancellationToken);
            var read = Reader.Read(path ?? string.Empty, operationCancellation.Token);
            StoreReadResult(read, null, "confirm", read.Ok ? "read-ok" : "read-failed", "unchanged");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StoreRefreshFailure(path ?? string.Empty, "confirm", ex.Message);
        }
    }

    public static EpgOverlayResult ReadLastResult()
    {
        string raw;
        lock (SyncRoot) raw = _lastResult;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = part.IndexOf('=');
            if (index <= 0) continue;
            map[part[..index]] = part[(index + 1)..];
        }
        return new EpgOverlayResult(map);
    }

    private static void StoreReadResult(LocalEpgDataReadResult read, TvAirExternalProgramGuideReplaceResultDto? replace, string operation, string status, string projection)
    {
        WriteResult(string.Join("|", new[]
        {
            "operation=" + operation,
            "status=" + Sanitize(status),
            "ok=" + read.Ok,
            "projection=" + projection,
            "services=" + read.ServiceCount,
            "events=" + read.EventCount,
            "titles=" + read.TitleCount,
            "outlines=" + read.OutlineCount,
            "details=" + read.DetailCount,
            "genres=" + read.GenreCodeCount,
            "candidates=" + read.CandidateCount,
            "accepted=" + (replace is null ? string.Empty : replace.AcceptedCount.ToString()),
            "rejected=" + (replace is null ? string.Empty : replace.RejectedCount.ToString()),
            "rejectedInvalid=" + (replace is null ? string.Empty : replace.RejectedInvalidCount.ToString()),
            "rejectedDuplicate=" + (replace is null ? string.Empty : replace.RejectedDuplicateCount.ToString()),
            "changed=" + (replace is null ? string.Empty : replace.Changed.ToString()),
            "readAt=" + read.ReadAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss")
        }));
    }

    private static Dictionary<string, string> LoadServiceNames(ITvAirPluginRuntimeContext context)
    {
        try
        {
            return context.Channels.ListServices(new TvAirServiceQueryDto { Enabled = true })
                .GroupBy(s => ServiceKeyText(s.NetworkId, s.TransportStreamId, s.ServiceId))
                .ToDictionary(g => g.Key, g => g.First().ServiceName, StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static bool NormalizeEnabled(string value)
        => string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private static void WriteResult(string text)
    {
        lock (SyncRoot) _lastResult = text ?? string.Empty;
    }

    private static string Sanitize(object? value)
        => (value?.ToString() ?? string.Empty).Replace('|', '/').Replace("\r", string.Empty).Replace("\n", " ").Trim();

    private static string ServiceKeyText(int networkId, int transportStreamId, int serviceId)
        => $"{networkId}:{transportStreamId}:{serviceId}";

    private static string? ReadString(global::TvAIrPlugin.Storage.ITvAirPluginStorageApi storage, string key)
    {
        var result = storage.Get(EpgOverlayContract.StorageNamespace, key);
        if (!result.Succeeded || result.Value is null || result.Value.Value is null) return null;
        return result.Value.Value switch
        {
            string text => text,
            System.Text.Json.JsonElement json when json.ValueKind == System.Text.Json.JsonValueKind.String => json.GetString(),
            _ => result.Value.Value.ToString()
        };
    }

    private static void WriteValue(global::TvAIrPlugin.Storage.ITvAirPluginStorageApi storage, string key, object? value)
    {
        var result = storage.Set(EpgOverlayContract.StorageNamespace, key, value);
        if (!result.Succeeded)
            throw new InvalidOperationException(result.Error?.Message ?? "設定を保存できませんでした。");
    }

    private static ITvAirPluginRuntimeContext RequireReadyContext()
    {
        var context = RequireContext();
        if (Volatile.Read(ref _started) != 1)
            throw new InvalidOperationException("プラグインは停止中です。");
        return context;
    }

    private static ITvAirPluginRuntimeContext RequireContext()
    {
        var context = _context;
        if (context is null) throw new InvalidOperationException("初期化が完了していません。");
        return context;
    }
}

internal readonly record struct EpgOverlayState(bool Enabled, string Path);
internal readonly record struct EpgOverlayDraftState(bool Enabled, string Path);
internal sealed record StoredSettingsState(bool Enabled, string EpgDataPath);

internal sealed class EpgOverlayResult
{
    private readonly IReadOnlyDictionary<string, string> _values;
    public EpgOverlayResult(IReadOnlyDictionary<string, string> values) => _values = values;
    public bool TryGet(string key, out string value) => _values.TryGetValue(key, out value!);
    public string Get(string key, string fallback) => _values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
}
