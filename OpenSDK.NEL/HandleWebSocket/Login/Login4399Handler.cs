using Codexus.OpenSDK;
using OpenSDK.NEL.type;

namespace OpenSDK.NEL.HandleWebSocket.Login;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text;
using Serilog;
using OpenSDK.NEL;
using Codexus.OpenSDK.Exceptions;
using Codexus.OpenSDK.Entities.X19;

internal class Login4399Handler : IWsHandler
{
    public string Type => "login_4399";
    public async Task ProcessAsync(System.Net.WebSockets.WebSocket ws, JsonElement root)
    {
        var account = root.TryGetProperty("account", out var acc) ? acc.GetString() : null;
        var password = root.TryGetProperty("password", out var pwd) ? pwd.GetString() : null;
        var sessionId = root.TryGetProperty("sessionId", out var sid) ? sid.GetString() : null;
        var captcha = root.TryGetProperty("captcha", out var cap) ? cap.GetString() : null;
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(password))
        {
            var err = JsonSerializer.Serialize(new { type = "login_error", message = "账号或密码为空" });
            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(err)), System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);
            return;
        }
        try
        {
            await AppState.Services!.X19.InitializeDeviceAsync();
            string json;
            if (!string.IsNullOrWhiteSpace(sessionId) && !string.IsNullOrWhiteSpace(captcha))
            {
                json = await AppState.Services!.C4399.LoginWithPasswordAsync(account!, password!, sessionId!, captcha!);
            }
            else
            {
                json = await AppState.Services!.C4399.LoginWithPasswordAsync(account!, password!);
            }
            var cont = await AppState.Services!.X19.ContinueAsync(json);
            var authOtp = cont.Item1;
            var channel = cont.Item2;
            await X19.InterconnectionApi.LoginStart(authOtp.EntityId, authOtp.Token);
            AppState.Accounts[authOtp.EntityId] = channel;
            AppState.Auths[authOtp.EntityId] = authOtp;
            AppState.SelectedAccountId = authOtp.EntityId;
            var ok = JsonSerializer.Serialize(new { type = "Success_login", entityId = authOtp.EntityId, channel });
            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(ok)), System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);
        }
        catch (Exception ex) when (ex.Data.Contains("captcha_url") && ex.Data.Contains("session_id"))
        {
            var capUrl = ex.Data["captcha_url"]?.ToString();
            var sidVal = ex.Data["session_id"]?.ToString();
            var payload = JsonSerializer.Serialize(new { type = "captcha_required", account, password, captchaUrl = capUrl, sessionId = sidVal });
            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(payload)), System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);
        }
        catch (VerifyException)
        {
            var payload = JsonSerializer.Serialize(new { type = "captcha_required", account, password });
            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(payload)), System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "4399登录失败");
            var err = JsonSerializer.Serialize(new { type = "login_error", message = ex.Message ?? "登录失败" });
            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(err)), System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None);
        }
    }
}