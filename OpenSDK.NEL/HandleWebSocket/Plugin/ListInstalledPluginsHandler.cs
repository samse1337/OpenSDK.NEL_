namespace OpenSDK.NEL.HandleWebSocket.Plugin;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;
using Codexus.Development.SDK.Manager;
using OpenSDK.NEL.type;

internal class ListInstalledPluginsHandler : IWsHandler
{
    public string Type => "list_installed_plugins";
    public async Task ProcessAsync(System.Net.WebSockets.WebSocket ws, JsonElement root)
    {
        var items = PluginManager.Instance.Plugins.Values.Select(plugin => new {
            identifier = plugin.Id,
            name = plugin.Name,
            version = plugin.Version,
            description = plugin.Description,
            author = plugin.Author,
            status = plugin.Status,
            waitingRestart = AppState.WaitRestartPlugins.ContainsKey(plugin.Id)
        }).ToArray();
        var msg = JsonSerializer.Serialize(new { type = "installed_plugins", items });
        await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)), System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);
    }
}