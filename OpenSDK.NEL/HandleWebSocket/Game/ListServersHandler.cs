namespace OpenSDK.NEL.HandleWebSocket.Game;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text;
using Serilog;
using OpenSDK.NEL.type;
using Codexus.Cipher.Entities.WPFLauncher.NetGame;
using Codexus.Cipher.Entities;

internal class ListServersHandler : IWsHandler
{
    public string Type => "list_servers";
    public async Task ProcessAsync(System.Net.WebSockets.WebSocket ws, JsonElement root)
    {
        var sel = AppState.SelectedAccountId;
        if (string.IsNullOrEmpty(sel) || !AppState.Auths.TryGetValue(sel, out var auth))
        {
            var notLogin = JsonSerializer.Serialize(new { type = "notlogin" });
            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(notLogin)), System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);
            return;
        }
        try
        {
            const int pageSize = 15;
            var offset = 0;
            var servers = await auth.Api<EntityNetGameRequest, Entities<EntityNetGameItem>>(
                "/item/query/available",
                new EntityNetGameRequest
                {
                    AvailableMcVersions = Array.Empty<string>(),
                    ItemType = 1,
                    Length = pageSize,
                    Offset = offset,
                    MasterTypeId = "2",
                    SecondaryTypeId = ""
                });
            
            if(AppState.Debug) Log.Information("服务器列表: 数量={Count}", servers.Data?.Length ?? 0);
            var items = servers.Data.Select(s => new { entityId = s.EntityId, name = s.Name }).ToArray();
            var msg = JsonSerializer.Serialize(new { type = "servers", items });
            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)), System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "获取服务器列表失败");
            var err = JsonSerializer.Serialize(new { type = "servers_error", message = "获取失败" });
            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(err)), System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);
        }
    }
}