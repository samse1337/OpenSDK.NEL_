namespace OpenSDK.NEL.HandleWebSocket.Plugin;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.IO;
using Codexus.Development.SDK.Manager;
using OpenSDK.NEL.type;
using Serilog;

internal class InstallPluginHandler : IWsHandler
{
    public string Type => "install_plugin";
    public async Task ProcessAsync(System.Net.WebSockets.WebSocket ws, JsonElement root)
    {
        var infoEl = root.TryGetProperty("info", out var inf) ? inf : default;
        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var loadNow = root.TryGetProperty("loadNow", out var ln) && ln.ValueKind == JsonValueKind.True;
        if (infoEl.ValueKind == JsonValueKind.String)
        {
            try { infoEl = JsonDocument.Parse(infoEl.GetString()!).RootElement; } catch { }
        }
        if (string.IsNullOrWhiteSpace(id)) return;
        if (infoEl.ValueKind != JsonValueKind.Object)
        {
            try
            {
                var detailUrl = "https://api.codexus.today/api/components/get/detail?id=" + Uri.EscapeDataString(id!);
                Log.Information("拉取插件详情: {Url}", detailUrl);
                using var hc = new HttpClient();
                var detailJson = await hc.GetStringAsync(detailUrl);
                using var detailDoc = JsonDocument.Parse(detailJson);
                infoEl = detailDoc.RootElement;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "获取插件详情失败: {PluginId}", id);
                var err = JsonSerializer.Serialize(new { type = "install_plugin_error", id, message = "获取详情失败" });
                await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(err)), System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);
                return;
            }
        }
        var itemsList = new List<JsonElement>();
        if (infoEl.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray()) itemsList.Add(el);
        }
        if (itemsList.Count == 0)
        {
            Log.Warning("安装插件缺少下载信息: {PluginId}", id);
            var err = JsonSerializer.Serialize(new { type = "install_plugin_error", id, message = "缺少安装信息" });
            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(err)), System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);
            return;
        }
        var client = new HttpClient();
        var total = itemsList.Count;
        var done = 0;
        foreach (var it in itemsList)
        {
            var pid = it.TryGetProperty("id", out var p) ? p.GetString() : null;
            var url = it.TryGetProperty("downloadUrl", out var u) ? u.GetString() : null;
            var hash = it.TryGetProperty("fileHash", out var h) ? h.GetString() : null;
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(hash)) continue;
            Log.Information("安装插件 {PluginId}", pid ?? "");
            var bytes = await client.GetByteArrayAsync(url);
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, hash + ".ug");
            await File.WriteAllBytesAsync(path, bytes);
            done++;
            var progress = total > 0 ? (int)(done * 100.0 / total) : 100;
            var progMsg = JsonSerializer.Serialize(new { type = "report_install_plugin_progress", id, progress, status = done >= total });
            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(progMsg)), System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);
        }
        if (loadNow)
        {
            PluginManager.Instance.LoadPlugins("plugins");
            if (!string.IsNullOrWhiteSpace(id)) AppState.WaitRestartPlugins.TryRemove(id, out _);
        }
        var upd = JsonSerializer.Serialize(new { type = "installed_plugins_updated" });
        await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(upd)), System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);
    }
}