using OpenSDK.NEL.type;

namespace OpenSDK.NEL.HandleWebSocket.Game;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text;

internal class QueryGameSessionHandler : IWsHandler
{
    public string Type => "query_game_session";
    public async Task ProcessAsync(System.Net.WebSockets.WebSocket ws, JsonElement root)
    {
        var list = AppState.Channels.Values.Select(ch => new {
            Id = "interceptor-" + ch.Identifier,
            ServerName = ch.ServerName,
            CharacterName = ch.RoleName,
            ServerVersion = string.Empty,
            StatusText = "Running",
            ProgressValue = 0,
            Type = "Interceptor",
            LocalAddress = "127.0.0.1:" + ch.LocalPort,
            Identifier = ch.Identifier.ToString()
        }).ToArray();
        var msg = JsonSerializer.Serialize(new { type = "query_game_session", items = list });
        await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)), System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);
    }
}