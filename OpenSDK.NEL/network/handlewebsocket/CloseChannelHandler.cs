namespace OpenSDK.NEL.HandleWebSocket;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text;
using OpenSDK.NEL;
using OpenSDK.NEL.Manager;

internal class CloseChannelHandler : IWsHandler
{
    public async Task ProcessAsync(System.Net.WebSockets.WebSocket ws, JsonElement root)
    {
        var serverId2 = root.TryGetProperty("serverId", out var sid2) ? sid2.GetString() : null;
        if (string.IsNullOrWhiteSpace(serverId2))
        {
            Serilog.Log.Warning("关闭通道失败，serverId为空");
        }
        else
        {
            Serilog.Log.Information("准备关闭通道: {ServerId}", serverId2);
            var ok = GameManager.Instance.Close(serverId2);
            if (ok) Serilog.Log.Information("通道已关闭: {ServerId}", serverId2);
            else Serilog.Log.Warning("关闭通道失败，未找到: {ServerId}", serverId2);
        }
        var msg = JsonSerializer.Serialize(new { type = "channels_updated" });
        await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)), System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);
    }
}