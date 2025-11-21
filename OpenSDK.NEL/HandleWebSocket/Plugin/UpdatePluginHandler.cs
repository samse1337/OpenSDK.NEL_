namespace OpenSDK.NEL.HandleWebSocket.Plugin;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;
using Codexus.Development.SDK.Manager;
using OpenSDK.NEL.type;
using Serilog;

internal class UpdatePluginHandler : IWsHandler
{
    public string Type => "update_plugin";
    public async Task ProcessAsync(System.Net.WebSockets.WebSocket ws, JsonElement root)
    {
        var payloadEl = root.TryGetProperty("payload", out var p) ? p : default;
        if (payloadEl.ValueKind == JsonValueKind.String)
        {
            try { payloadEl = JsonDocument.Parse(payloadEl.GetString()!).RootElement; } catch { }
        }
        var id = payloadEl.ValueKind == JsonValueKind.Object && payloadEl.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var oldVer = payloadEl.ValueKind == JsonValueKind.Object && payloadEl.TryGetProperty("old", out var oldEl) ? oldEl.GetString() : null;
        var infoEl = payloadEl.ValueKind == JsonValueKind.Object && payloadEl.TryGetProperty("info", out var info) ? info : default;
        if (infoEl.ValueKind == JsonValueKind.String)
        {
            try { infoEl = JsonDocument.Parse(infoEl.GetString()!).RootElement; } catch { }
        }
        if (!string.IsNullOrWhiteSpace(id))
        {
            if (PluginManager.Instance.HasPlugin(id))
            {
                var paths = PluginManager.Instance.GetPluginAndDependencyPaths(id, (s) => PluginManager.Instance.HasPlugin(s));
                Log.Information("更新插件，卸载旧版本 {PluginId}", id);
                PluginManager.Instance.UninstallPluginWithPaths(paths);
                AppState.WaitRestartPlugins[id] = true;
            }
        }
        var msg = JsonSerializer.Serialize(new { type = "installed_plugins_updated" });
        await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)), System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);
        if (infoEl.ValueKind == JsonValueKind.Object)
        {
            var installReq = JsonSerializer.Serialize(new { type = "install_plugin", id, info = infoEl, loadNow = false });
            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(installReq)), System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);
        }
        var items = PluginManager.Instance.Plugins.Values.Select(plugin => new {
            identifier = plugin.Id,
            name = plugin.Name,
            version = plugin.Version,
            description = plugin.Description,
            author = plugin.Author,
            status = plugin.Status
        }).ToArray();
        var listMsg = JsonSerializer.Serialize(new { type = "installed_plugins", items });
        await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(listMsg)), System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);
    }
}
