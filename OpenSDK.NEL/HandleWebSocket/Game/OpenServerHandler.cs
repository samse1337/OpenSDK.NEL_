using Codexus.Cipher.Entities.WPFLauncher.NetGame;
using OpenSDK.NEL.type;

namespace OpenSDK.NEL.HandleWebSocket.Game;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text;
using Serilog;

internal class OpenServerHandler : IWsHandler
{
    public string Type => "open_server";
    public async Task ProcessAsync(System.Net.WebSockets.WebSocket ws, JsonElement root)
    {
        var serverId = root.TryGetProperty("serverId", out var sid) ? sid.GetString() : null;
        var sel = AppState.SelectedAccountId;
        if (string.IsNullOrEmpty(sel) || !AppState.Auths.TryGetValue(sel, out var auth))
        {
            var notLogin = JsonSerializer.Serialize(new { type = "notlogin" });
            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(notLogin)), System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);
            return;
        }
        if (string.IsNullOrWhiteSpace(serverId))
        {
            var err = JsonSerializer.Serialize(new { type = "server_roles_error", message = "参数错误" });
            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(err)), System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);
            return;
        }
        try
        {
            if(AppState.Debug)Log.Information("打开服务器: serverId={ServerId}, account={AccountId}", serverId, auth.EntityId);
            var roles = await auth.Api<EntityQueryGameCharacters, Codexus.Cipher.Entities.Entities<Codexus.Cipher.Entities.WPFLauncher.NetGame.EntityGameCharacter>>(
                "/game-character/query/user-game-characters",
                new EntityQueryGameCharacters
                {
                    GameId = serverId,
                    UserId = auth.EntityId
                });
            var items = roles.Data.Select(r => new { id = r.Name, name = r.Name }).ToArray();
            var msg = JsonSerializer.Serialize(new { type = "server_roles", items, serverId });
            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)), System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "获取服务器角色失败: serverId={ServerId}", serverId);
            var err = JsonSerializer.Serialize(new { type = "server_roles_error", message = "获取失败" });
            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(err)), System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);
        }
    }
}