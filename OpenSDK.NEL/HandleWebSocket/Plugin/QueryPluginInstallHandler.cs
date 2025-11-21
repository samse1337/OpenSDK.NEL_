namespace OpenSDK.NEL.HandleWebSocket.Plugin;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Codexus.Development.SDK.Manager;

internal class QueryPluginInstallHandler : IWsHandler
{
    public string Type => "query_plugin_install";
    public async Task ProcessAsync(System.Net.WebSockets.WebSocket ws, JsonElement root)
    {
        var pluginId = root.TryGetProperty("pluginId", out var idEl) ? idEl.GetString() : null;
        var pluginVersion = root.TryGetProperty("pluginVersion", out var verEl) ? verEl.GetString() : null;
        var isInstalled = false;
        var installedVersion = string.Empty;
        if (!string.IsNullOrWhiteSpace(pluginId) && PluginManager.Instance.HasPlugin(pluginId))
        {
            var p = PluginManager.Instance.GetPlugin(pluginId);
            isInstalled = true;
            installedVersion = p.Version;
        }
        var msg = JsonSerializer.Serialize(new { type = "query_plugin_install", pluginId, pluginVersion, pluginIsInstalled = isInstalled, pluginInstalledVersion = installedVersion });
        await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)), System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);
    }
}