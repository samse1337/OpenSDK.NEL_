using System.Net;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Text;
using System.Threading;
using Codexus.Cipher.Entities;
using Codexus.Cipher.Entities.WPFLauncher.NetGame;
using Codexus.Cipher.Protocol;
using Codexus.Cipher.Protocol.Registers;
using Codexus.Development.SDK.Entities;
using Codexus.Development.SDK.Manager;
using Codexus.Development.SDK.Utils;
using Codexus.Game.Launcher.Services.Java;
using Codexus.Game.Launcher.Utils;
using Codexus.Interceptors;
using Codexus.OpenSDK;
using Codexus.OpenSDK.Entities.X19;
using Codexus.OpenSDK.Entities.Yggdrasil;
using Codexus.OpenSDK.Generator;
using Codexus.OpenSDK.Http;
using Codexus.OpenSDK.Yggdrasil;
using OpenSDK.NEL;
using OpenSDK.NEL.Entities;
using Serilog;
using System.Collections.Concurrent;
using System.Linq;

ConfigureLogger();

await InitializeSystemComponentsAsync();

AppState.Services = await CreateServices();
await AppState.Services.X19.InitializeDeviceAsync();

await StartWebServerAsync();
await Task.Delay(Timeout.Infinite);

static void ConfigureLogger()
{
    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Information()
        .WriteTo.Console()
        .CreateLogger();
}

static async Task InitializeSystemComponentsAsync()
{
    Interceptor.EnsureLoaded();
    PacketManager.Instance.EnsureRegistered();
    PluginManager.Instance.EnsureUninstall();
    PluginManager.Instance.LoadPlugins("plugins");
    await Task.CompletedTask;
}

static async Task<Services> CreateServices()
{
    var api = new WebNexusApi("");
    var register = new Channel4399Register();
    var c4399 = new C4399();
    var x19 = new X19();

    var yggdrasil = new StandardYggdrasil(new YggdrasilData
    {
        LauncherVersion = x19.GameVersion,
        Channel = "netease",
        CrcSalt = await ComputeCrcSalt()
    });

    return new Services(api, register, c4399, x19, yggdrasil);
}

static async Task<(X19AuthenticationOtp, string)> LoginAsync(Services services)
{
    var mode = ReadOption("请选择登录模式：1. Cookie 登录  2. 随机 4399 登录", ["1", "2"]);

    return mode switch
    {
        "1" => await LoginWithCookieAsync(services.X19),
        _ => throw new ArgumentException($"不支持的登录模式: {mode}")
    };
}

static async Task<(X19AuthenticationOtp, string)> LoginWithCookieAsync(X19 x19)
{
    var cookie = ReadText("输入您的 Cookie: ");
    return await x19.ContinueAsync(cookie);
}

static async Task<(X19AuthenticationOtp, string)> LoginWith4399Async(Services services)
{
    const int maxRetries = 3;

    for (var attempt = 1; attempt <= maxRetries; attempt++)
        try
        {
            Log.Information("正在调用接口获取账户... (尝试 {Count}/{Max})", attempt, maxRetries);

            var user = await services.Register.RegisterAsync(
                services.Api.ComputeCaptchaAsync,
                () => new IdCard
                {
                    Name = Channel4399Register.GenerateChineseName(),
                    IdNumber = Channel4399Register.GenerateRandomIdCard()
                });

            var json = await services.C4399.LoginWithPasswordAsync(user.Account, user.Password);
            return await services.X19.ContinueAsync(json);
        }
        catch (Exception e)
        {
            Log.Error(e, "调用接口获取账户或登录时发生异常 (尝试 {Count}/{Max})", attempt, maxRetries);

            if (attempt == maxRetries)
            {
                Log.Error("达到最大重试次数，登录失败");
                throw;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

    throw new InvalidOperationException("登录失败");
}

static async Task<EntityNetGameItem> SelectServerAsync(X19AuthenticationOtp authOtp)
{
    while (true)
    {
        var serverName = ReadText("搜索服务器: ");
        var servers = await authOtp.Api<EntityNetGameKeyword, Entities<EntityNetGameItem>>(
            "/item/query/search-by-keyword",
            new EntityNetGameKeyword { Keyword = serverName });

        if (servers.Data.Length == 0)
        {
            Log.Warning("未找到服务器，请重新搜索。");
            continue;
        }

        Log.Information("找到以下服务器，请选择要游玩的服务器：");
        for (var i = 0; i < servers.Data.Length; i++)
        {
            var server = servers.Data[i];
            Log.Information("{Index}. {ServerName} (ID: {ServerId})", i + 1, server.Name, server.EntityId);
        }

        var choice = ReadNumberInRange(1, servers.Data.Length, "请输入服务器编号");
        return servers.Data[choice - 1];
    }
}

static async Task ManageServerAsync(X19AuthenticationOtp authOtp, Services services, EntityNetGameItem selectedServer)
{
    while (true)
    {
        var roles = await GetServerRolesAsync(authOtp, selectedServer);
        DisplayRoles(roles);

        var operation = ReadOption("请选择操作：1. 启动代理  2. 添加随机角色  3. 添加指定角色  4. 返回服务器选择", ["1", "2", "3", "4"]);

        switch (operation)
        {
            case "1":
                if (await StartProxyAsync(authOtp, services, selectedServer, roles)) return;

                break;

            case "2":
                await CreateRandomCharacterAsync(authOtp, selectedServer);
                break;

            case "3":
                await CreateNamedCharacterAsync(authOtp, selectedServer);
                break;

            case "4":
                return;

            default:
                Log.Warning("不支持的操作: {Operation}", operation);
                break;
        }
    }
}

static async Task<EntityGameCharacter[]> GetServerRolesAsync(X19AuthenticationOtp authOtp, EntityNetGameItem server)
{
    var roles = await authOtp.Api<EntityQueryGameCharacters, Entities<EntityGameCharacter>>(
        "/game-character/query/user-game-characters",
        new EntityQueryGameCharacters
        {
            GameId = server.EntityId,
            UserId = authOtp.EntityId
        });

    return roles.Data;
}

static void DisplayRoles(EntityGameCharacter[] roles)
{
    if (roles.Length > 0)
    {
        Log.Information("当前角色列表：");
        for (var i = 0; i < roles.Length; i++)
        {
            var character = roles[i];
            Log.Information("{Index}. {CharacterName} (ID: {CharacterGameId})", i + 1, character.Name,
                character.GameId);
        }
    }
    else
    {
        Log.Information("没有找到角色。");
    }
}

static async Task<bool> StartProxyAsync(X19AuthenticationOtp authOtp, Services services,
    EntityNetGameItem selectedServer, EntityGameCharacter[] roles)
{
    if (roles.Length == 0)
    {
        Log.Warning("没有可用角色来启动代理。");
        return false;
    }

    var roleChoice = ReadNumberInRange(1, roles.Length, "请选择角色序号启动代理");
    var selectedCharacter = roles[roleChoice - 1];

    Log.Information("已选择角色 {CharacterName} (ID: {CharacterGameId}) 启动代理",
        selectedCharacter.Name, selectedCharacter.GameId);

    try
    {
        var details = await authOtp.Api<EntityQueryNetGameDetailRequest, Entity<EntityQueryNetGameDetailItem>>(
            "/item-details/get_v2",
            new EntityQueryNetGameDetailRequest { ItemId = selectedServer.EntityId });

        var address = await authOtp.Api<EntityAddressRequest, Entity<EntityNetGameServerAddress>>(
            "/item-address/get",
            new EntityAddressRequest { ItemId = selectedServer.EntityId });

        var version = details.Data!.McVersionList[0];
        var gameVersion = GameVersionUtil.GetEnumFromGameVersion(version.Name);

        var serverModInfo = await InstallerService.InstallGameMods(
            authOtp.EntityId,
            authOtp.Token,
            gameVersion,
            new WPFLauncher(),
            selectedServer.EntityId,
            false);

        var mods = JsonSerializer.Serialize(serverModInfo);

        CreateProxyInterceptor(authOtp, services.Yggdrasil, selectedServer, selectedCharacter, version, address.Data!,
            mods);

        await X19.InterconnectionApi.GameStartAsync(authOtp.EntityId, authOtp.Token, selectedServer.EntityId);
        Log.Information("代理服务器已创建并启动。");

        await Task.Delay(Timeout.Infinite);
        return true;
    }
    catch (Exception ex)
    {
        Log.Error(ex, "启动代理时发生错误");
        return false;
    }
}

static void CreateProxyInterceptor(
    X19AuthenticationOtp authOtp,
    StandardYggdrasil yggdrasil,
    EntityNetGameItem server,
    EntityGameCharacter character,
    dynamic version,
    EntityNetGameServerAddress address,
    string mods)
{
    Interceptor.CreateInterceptor(
        new EntitySocks5 { Enabled = false },
        mods,
        server.EntityId,
        server.Name,
        version.Name,
        address.Ip,
        address.Port,
        character.Name,
        authOtp.EntityId,
        authOtp.Token,
        (Action<string>)YggdrasilCallback);
    return;

    void YggdrasilCallback(string serverId)
    {
        Log.Information("Server ID: {Certification}", serverId);

        var signal = new SemaphoreSlim(0);
        _ = Task.Run(async () =>
        {
            try
            {
                var success = await yggdrasil.JoinServerAsync(new GameProfile
                {
                    GameId = server.EntityId,
                    GameVersion = version.Name,
                    BootstrapMd5 = "2A7A476411A1687A56DC6848829C1AE4",
                    DatFileMd5 = "D285CBF97D9BA30D3C445DBF1C342634",
                    Mods = JsonSerializer.Deserialize<ModList>(mods)!,
                    User = new UserProfile { UserId = int.Parse(authOtp.EntityId), UserToken = authOtp.Token }
                }, serverId);

                if (success.IsSuccess)
                    Log.Information("消息认证成功");
                else
                    Log.Error("消息认证失败: {Error}", success.Error);
            }
            catch (Exception e)
            {
                Log.Error(e, "认证过程中发生异常");
            }
            finally
            {
                signal.Release();
            }
        });

        signal.Wait();
    }
}

static async Task CreateRandomCharacterAsync(X19AuthenticationOtp authOtp, EntityNetGameItem server)
{
    var randomName = StringGenerator.GenerateRandomString(12, false);
    await CreateCharacterAsync(authOtp, server, randomName);
    Log.Information("已创建随机角色: {Name}", randomName);
}

static async Task CreateNamedCharacterAsync(X19AuthenticationOtp authOtp, EntityNetGameItem server)
{
    var roleName = ReadText("角色名称: ");

    if (string.IsNullOrWhiteSpace(roleName))
    {
        Log.Warning("角色名称不能为空");
        return;
    }

    await CreateCharacterAsync(authOtp, server, roleName);
    Log.Information("已创建角色: {Name}", roleName);
}

static async Task CreateCharacterAsync(X19AuthenticationOtp authOtp, EntityNetGameItem server, string name)
{
    try
    {
        await authOtp.Api<EntityCreateCharacter, JsonElement>(
            "/game-character",
            new EntityCreateCharacter
            {
                GameId = server.EntityId,
                UserId = authOtp.EntityId,
                Name = name
            });
    }
    catch (Exception ex)
    {
        Log.Error(ex, "创建角色失败");
        throw;
    }
}

static async Task<string> ComputeCrcSalt()
{
    Log.Information("正在计算 CRC Salt...");

    var http = new HttpWrapper("https://service.codexus.today",
        options => { options.WithBearerToken("0e9327a2-d0f8-41d5-8e23-233de1824b9a.pk_053ff2d53503434bb42fe158"); });

    var response = await http.GetAsync("/crc-salt");

    var json = await response.Content.ReadAsStringAsync();
    var entity = JsonSerializer.Deserialize<OpenSdkResponse<CrcSalt>>(json);

    if (entity != null) return entity.Data.Salt;

    Log.Error("无法计算出 CrcSalt");
    return string.Empty;
}

static async Task StartWebServerAsync()
{
    var port = GetPort();
    var listener = new HttpListener();
    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    listener.Prefixes.Add($"http://localhost:{port}/");
    listener.Start();
    _ = Task.Run(async () =>
    {
        while (true)
        {
            var ctx = await listener.GetContextAsync();
            _ = Task.Run(async () =>
            {
                try
                {
                    await ServeContextAsync(ctx);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "请求处理异常");
                }
            });
        }
    });
    Log.Information("WebUI已启动: http://127.0.0.1:{Port}/", port);
    Log.Information("WS已启动服务器: ws://127.0.0.1:{Port}/ws", port);
}

static int GetPort()
{
    var env = Environment.GetEnvironmentVariable("NEL_PORT");
    if (int.TryParse(env, out var p) && p > 0) return p;
    return 8080;
}

static async Task ServeContextAsync(HttpListenerContext context)
{
    var req = context.Request;
    if (req.Url!.AbsolutePath == "/ws")
    {
        if (req.IsWebSocketRequest)
        {
            var wsCtx = await context.AcceptWebSocketAsync(null);
            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleWebSocket(wsCtx.WebSocket);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "WS连接处理异常");
                }
            });
            return;
        }
        context.Response.StatusCode = 400;
        context.Response.Close();
        return;
    }

    var root = Path.Combine(AppContext.BaseDirectory, "resources");
    var path = req.Url.AbsolutePath;
    if (path == "/") path = "/index.html";
    var filePath = Path.GetFullPath(Path.Combine(root, path.TrimStart('/')));
    if (!filePath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
    {
        context.Response.StatusCode = 404;
        context.Response.Close();
        return;
    }
    await WriteFileResponse(context.Response, filePath);
}

static async Task HandleWebSocket(System.Net.WebSockets.WebSocket ws)
{
    var buffer = new byte[4096];
    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("connected")), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
    var accountsItems = AppState.Accounts.Select(kv => new { entityId = kv.Key, channel = kv.Value }).ToArray();
    var initMsg = JsonSerializer.Serialize(new { type = "accounts", items = accountsItems });
    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(initMsg)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
    var channelItems = AppState.Channels.Values.Select(ch => new { serverId = ch.ServerId, serverName = ch.ServerName, address = ch.Ip + ":" + ch.Port }).ToArray();
    var chMsg = JsonSerializer.Serialize(new { type = "channels", items = channelItems });
    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(chMsg)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
    while (ws.State == System.Net.WebSockets.WebSocketState.Open)
    {
        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
        {
            await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
            break;
        }
        var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "cookie_login")
            {
                var cookie = root.TryGetProperty("cookie", out var c) ? c.GetString() : null;
                if (string.IsNullOrWhiteSpace(cookie))
                {
                    var err = JsonSerializer.Serialize(new { type = "login_error", message = "cookie为空" });
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(err)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                    continue;
                }
                try
                {
                    var (authOtp, channel) = await AppState.Services!.X19.ContinueAsync(cookie);
                    Log.Information("Cookie登录成功: {Id}, {Channel}", authOtp.EntityId, channel);
                    AppState.Accounts[authOtp.EntityId] = channel;
                    AppState.Auths[authOtp.EntityId] = authOtp;
                    AppState.SelectedAccountId = authOtp.EntityId;
                    var ok = JsonSerializer.Serialize(new { type = "login_success", entityId = authOtp.EntityId, channel });
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(ok)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Cookie登录失败");
                    var err = JsonSerializer.Serialize(new { type = "login_error", message = "登录失败" });
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(err)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                }
                continue;
            }
            if (type == "login_4399")
            {
                var account = root.TryGetProperty("account", out var acc) ? acc.GetString() : null;
                var password = root.TryGetProperty("password", out var pwd) ? pwd.GetString() : null;
                if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(password))
                {
                    var err = JsonSerializer.Serialize(new { type = "login_error", message = "账号或密码为空" });
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(err)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                    continue;
                }
                try
                {
                    var json = await AppState.Services!.C4399.LoginWithPasswordAsync(account, password);
                    var (authOtp, channel) = await AppState.Services!.X19.ContinueAsync(json);
                    AppState.Accounts[authOtp.EntityId] = channel;
                    AppState.Auths[authOtp.EntityId] = authOtp;
                    AppState.SelectedAccountId = authOtp.EntityId;
                    var ok = JsonSerializer.Serialize(new { type = "login_success", entityId = authOtp.EntityId, channel });
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(ok)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    var needCaptcha = ex.Message != null && ex.Message.Contains("Captcha", StringComparison.OrdinalIgnoreCase);
                    if (needCaptcha)
                    {
                        var sid = Guid.NewGuid().ToString("N");
                        AppState.PendingCaptchas[sid] = (account!, password!);
                        var cap = JsonSerializer.Serialize(new { type = "captcha_required", account, password, sessionId = sid });
                        await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(cap)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    else
                    {
                        Log.Error(ex, "4399登录失败");
                        var err = JsonSerializer.Serialize(new { type = "login_error", message = "登录失败" });
                        await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(err)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                }
                continue;
            }
            if (type == "login_4399_with_captcha")
            {
                var sessionId = root.TryGetProperty("sessionId", out var sidp) ? sidp.GetString() : null;
                var captcha = root.TryGetProperty("captcha", out var capp) ? capp.GetString() : null;
                if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(captcha))
                {
                    var err = JsonSerializer.Serialize(new { type = "login_error", message = "验证码会话或值为空" });
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(err)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                    continue;
                }
                if (!AppState.PendingCaptchas.TryGetValue(sessionId, out var creds))
                {
                    var err = JsonSerializer.Serialize(new { type = "login_error", message = "验证码会话无效" });
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(err)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                    continue;
                }
                try
                {
                    var json = await AppState.Services!.C4399.LoginWithPasswordAsync(creds.account, creds.password, sessionId, captcha);
                    var cont = await AppState.Services!.X19.ContinueAsync(json);
                    var authOtp = cont.Item1;
                    var channel = cont.Item2;
                    AppState.Accounts[authOtp.EntityId] = channel;
                    AppState.Auths[authOtp.EntityId] = authOtp;
                    AppState.SelectedAccountId = authOtp.EntityId;
                    AppState.PendingCaptchas.TryRemove(sessionId, out _);
                    var ok = JsonSerializer.Serialize(new { type = "login_success", entityId = authOtp.EntityId, channel });
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(ok)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "携带验证码登录失败");
                    var err = JsonSerializer.Serialize(new { type = "login_error", message = "登录失败" });
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(err)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                }
                continue;
            }
            if (type == "delete_account")
            {
                var id = root.TryGetProperty("entityId", out var idProp) ? idProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(id))
                {
                    var err = JsonSerializer.Serialize(new { type = "delete_error", message = "entityId为空" });
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(err)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                    continue;
                }
                if (AppState.Accounts.TryRemove(id, out _))
                {
                    Log.Information("已删除账号: {Id}", id);
                    if (AppState.SelectedAccountId == id) AppState.SelectedAccountId = null;
                }
                else
                {
                    Log.Warning("删除账号失败，未找到: {Id}", id);
                }
                var accountsItems2 = AppState.Accounts.Select(kv => new { entityId = kv.Key, channel = kv.Value }).ToArray();
                var msg = JsonSerializer.Serialize(new { type = "accounts", items = accountsItems2 });
                await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                continue;
            }
            if (type == "list_accounts")
            {
                var accItems = AppState.Accounts.Select(kv => new { entityId = kv.Key, channel = kv.Value }).ToArray();
                var msg = JsonSerializer.Serialize(new { type = "accounts", items = accItems });
                await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                continue;
            }
            if (type == "select_account")
            {
                var id = root.TryGetProperty("entityId", out var idProp2) ? idProp2.GetString() : null;
                if (string.IsNullOrWhiteSpace(id) || !AppState.Auths.ContainsKey(id))
                {
                    var notLogin2 = JsonSerializer.Serialize(new { type = "notlogin" });
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(notLogin2)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                    continue;
                }
                AppState.SelectedAccountId = id;
                var okSel = JsonSerializer.Serialize(new { type = "selected_account", entityId = id });
                await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(okSel)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                continue;
            }
            if (type == "list_servers")
            {
                var keyword = root.TryGetProperty("keyword", out var k) ? k.GetString() : string.Empty;
                var sel = AppState.SelectedAccountId;
                if (string.IsNullOrEmpty(sel) || !AppState.Auths.TryGetValue(sel, out var auth))
                {
                    var notLogin = JsonSerializer.Serialize(new { type = "notlogin" });
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(notLogin)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                    continue;
                }
                try
                {
                    var servers = await auth.Api<EntityNetGameKeyword, Entities<EntityNetGameItem>>(
                        "/item/query/search-by-keyword",
                        new EntityNetGameKeyword { Keyword = keyword ?? string.Empty });
                    Log.Information("服务器搜索: 关键字={Keyword}, 数量={Count}", keyword, servers.Data?.Length ?? 0);
                    var items = servers.Data.Select(s => new { entityId = s.EntityId, name = s.Name }).ToArray();
                    var msg = JsonSerializer.Serialize(new { type = "servers", items });
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "获取服务器列表失败");
                    var err = JsonSerializer.Serialize(new { type = "servers_error", message = "获取失败" });
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(err)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                }
                continue;
            }
            if (type == "open_server")
            {
                var serverId = root.TryGetProperty("serverId", out var sid) ? sid.GetString() : null;
                var serverName = root.TryGetProperty("serverName", out var sname) ? sname.GetString() : string.Empty;
                var sel = AppState.SelectedAccountId;
                if (string.IsNullOrEmpty(sel) || !AppState.Auths.TryGetValue(sel, out var auth))
                {
                    var notLogin = JsonSerializer.Serialize(new { type = "notlogin" });
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(notLogin)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(serverId))
                {
                    var err = JsonSerializer.Serialize(new { type = "server_roles_error", message = "参数错误" });
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(err)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                    continue;
                }
                var roles = await GetServerRolesByIdAsync(auth, serverId);
                var items = roles.Select(r => new { id = r.GameId, name = r.Name }).ToArray();
                var msg = JsonSerializer.Serialize(new { type = "server_roles", items, serverId, serverName });
                await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                continue;
            }
            if (type == "create_role_random")
            {
                var serverId = root.TryGetProperty("serverId", out var sid) ? sid.GetString() : null;
                var sel = AppState.SelectedAccountId;
                if (string.IsNullOrEmpty(sel) || !AppState.Auths.TryGetValue(sel, out var auth))
                {
                    var notLogin = JsonSerializer.Serialize(new { type = "notlogin" });
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(notLogin)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(serverId))
                {
                    var err = JsonSerializer.Serialize(new { type = "server_roles_error", message = "参数错误" });
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(err)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                    continue;
                }
                var name = StringGenerator.GenerateRandomString(12, false);
                await CreateCharacterByIdAsync(auth, serverId, name);
                var roles = await GetServerRolesByIdAsync(auth, serverId);
                var items = roles.Select(r => new { id = r.GameId, name = r.Name }).ToArray();
                var msg = JsonSerializer.Serialize(new { type = "server_roles", items, serverId, createdName = name });
                await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                continue;
            }
            if (type == "create_role_named")
            {
                var serverId = root.TryGetProperty("serverId", out var sid) ? sid.GetString() : null;
                var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
                var sel = AppState.SelectedAccountId;
                if (string.IsNullOrEmpty(sel) || !AppState.Auths.TryGetValue(sel, out var auth))
                {
                    var notLogin = JsonSerializer.Serialize(new { type = "notlogin" });
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(notLogin)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(serverId) || string.IsNullOrWhiteSpace(name))
                {
                    var err = JsonSerializer.Serialize(new { type = "server_roles_error", message = "参数错误" });
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(err)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                    continue;
                }
                await CreateCharacterByIdAsync(auth, serverId, name);
                var roles = await GetServerRolesByIdAsync(auth, serverId);
                var items = roles.Select(r => new { id = r.GameId, name = r.Name }).ToArray();
                var msg = JsonSerializer.Serialize(new { type = "server_roles", items, serverId, createdName = name });
                await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                continue;
            }
            if (type == "start_proxy")
            {
                var serverId = root.TryGetProperty("serverId", out var sid) ? sid.GetString() : null;
                var serverName = root.TryGetProperty("serverName", out var sname) ? sname.GetString() : string.Empty;
                var roleId = root.TryGetProperty("roleId", out var rid) ? rid.GetString() : null;
                var sel = AppState.SelectedAccountId;
                if (string.IsNullOrEmpty(sel) || !AppState.Auths.TryGetValue(sel, out var auth))
                {
                    var notLogin = JsonSerializer.Serialize(new { type = "notlogin" });
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(notLogin)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(serverId) || string.IsNullOrWhiteSpace(roleId))
                {
                    var err = JsonSerializer.Serialize(new { type = "start_error", message = "参数错误" });
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(err)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                    continue;
                }
                try
                {
                    var roles = await GetServerRolesByIdAsync(auth, serverId);
                    var selected = roles.FirstOrDefault(r => r.GameId == roleId);
                    if (selected == null)
                    {
                        var err = JsonSerializer.Serialize(new { type = "start_error", message = "角色不存在" });
                        await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(err)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                        continue;
                    }
                    var address = await auth.Api<EntityAddressRequest, Entity<EntityNetGameServerAddress>>(
                        "/item-address/get",
                        new EntityAddressRequest { ItemId = serverId });
                    var cts = new CancellationTokenSource();
                    AppState.Channels[serverId!] = new ChannelInfo
                    {
                        ServerId = serverId!,
                        ServerName = serverName,
                        Ip = address.Data!.Ip,
                        Port = address.Data!.Port,
                        RoleName = selected.Name,
                        Cts = cts
                    };
                    _ = Task.Run(async () =>
                    {
                        await StartProxyWithRoleByIdAsync(auth, AppState.Services!, serverId!, serverName, selected, cts.Token);
                    });
                    var items3 = AppState.Channels.Values.Select(ch => new { serverId = ch.ServerId, serverName = ch.ServerName, address = ch.Ip + ":" + ch.Port }).ToArray();
                    var ok = JsonSerializer.Serialize(new { type = "channels", items = items3 });
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(ok)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "启动代理失败");
                    var err = JsonSerializer.Serialize(new { type = "start_error", message = "启动失败" });
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(err)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                }
                continue;
            }
            if (type == "list_channels")
            {
                var items4 = AppState.Channels.Values.Select(ch => new { serverId = ch.ServerId, serverName = ch.ServerName, address = ch.Ip + ":" + ch.Port }).ToArray();
                var msg = JsonSerializer.Serialize(new { type = "channels", items = items4 });
                await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                continue;
            }
            if (type == "close_channel")
            {
                var serverId2 = root.TryGetProperty("serverId", out var sid2) ? sid2.GetString() : null;
                if (!string.IsNullOrWhiteSpace(serverId2) && AppState.Channels.TryRemove(serverId2, out var ch)) ch.Cts.Cancel();
                var items5 = AppState.Channels.Values.Select(cc => new { serverId = cc.ServerId, serverName = cc.ServerName, address = cc.Ip + ":" + cc.Port }).ToArray();
                var msg2 = JsonSerializer.Serialize(new { type = "channels", items = items5 });
                await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg2)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                continue;
            }
            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch
        {
            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }
}

static async Task WriteFileResponse(HttpListenerResponse resp, string filePath)
{
    var content = await File.ReadAllBytesAsync(filePath);
    resp.ContentType = GetMimeType(filePath);
    resp.ContentLength64 = content.Length;
    await resp.OutputStream.WriteAsync(content, 0, content.Length);
    resp.Close();
}

static string GetMimeType(string path)
{
    var ext = Path.GetExtension(path).ToLowerInvariant();
    if (ext == ".html") return "text/html";
    if (ext == ".css") return "text/css";
    if (ext == ".js") return "application/javascript";
    if (ext == ".png") return "image/png";
    if (ext == ".jpg" || ext == ".jpeg") return "image/jpeg";
    if (ext == ".svg") return "image/svg+xml";
    if (ext == ".ico") return "image/x-icon";
    return "application/octet-stream";
}

static string ReadOption(string prompt, string[] validInputs)
{
    while (true)
    {
        Log.Information(prompt);
        Console.Write("> ");
        var input = Console.ReadLine()?.Trim();

        if (!string.IsNullOrEmpty(input) && validInputs.Contains(input)) return input;

        Log.Warning("输入无效，请重新输入。");
    }
}


static string ReadText(string prompt)
{
    Log.Information(prompt);
    Console.Write("> ");
    return Console.ReadLine()?.Trim() ?? string.Empty;
}

static int ReadNumberInRange(int min, int max, string prompt)
{
    while (true)
    {
        Log.Information("{Prompt} ({Min}-{Max}): ", prompt, min, max);
        Console.Write("> ");
        var input = Console.ReadLine();

        if (int.TryParse(input, out var number) && number >= min && number <= max) return number;

        Log.Warning("输入无效，请输入 {Min} 到 {Max} 之间的数字。", min, max);
    }
}

 

static async Task CreateCharacterByIdAsync(X19AuthenticationOtp authOtp, string serverId, string name)
{
    await authOtp.Api<EntityCreateCharacter, JsonElement>(
        "/game-character",
        new EntityCreateCharacter
        {
            GameId = serverId,
            UserId = authOtp.EntityId,
            Name = name
        });
}

static async Task<EntityGameCharacter[]> GetServerRolesByIdAsync(X19AuthenticationOtp authOtp, string serverId)
{
    var roles = await authOtp.Api<EntityQueryGameCharacters, Entities<EntityGameCharacter>>(
        "/game-character/query/user-game-characters",
        new EntityQueryGameCharacters
        {
            GameId = serverId,
            UserId = authOtp.EntityId
        });

    return roles.Data;
}

static async Task<bool> StartProxyWithRoleByIdAsync(X19AuthenticationOtp authOtp, Services services, string serverId, string serverName, EntityGameCharacter selectedCharacter, CancellationToken ct)
{
    try
    {
        var details = await authOtp.Api<EntityQueryNetGameDetailRequest, Entity<EntityQueryNetGameDetailItem>>(
            "/item-details/get_v2",
            new EntityQueryNetGameDetailRequest { ItemId = serverId });

        var address = await authOtp.Api<EntityAddressRequest, Entity<EntityNetGameServerAddress>>(
            "/item-address/get",
            new EntityAddressRequest { ItemId = serverId });

        var version = details.Data!.McVersionList[0];
        var gameVersion = GameVersionUtil.GetEnumFromGameVersion(version.Name);

        var serverModInfo = await InstallerService.InstallGameMods(
            authOtp.EntityId,
            authOtp.Token,
            gameVersion,
            new WPFLauncher(),
            serverId,
            false);

        var mods = JsonSerializer.Serialize(serverModInfo);

        CreateProxyInterceptor(authOtp, services.Yggdrasil, new EntityNetGameItem { EntityId = serverId, Name = serverName }, selectedCharacter, version, address.Data!, mods);

        await X19.InterconnectionApi.GameStartAsync(authOtp.EntityId, authOtp.Token, serverId);
        Log.Information("代理服务器已创建并启动。");

        await Task.Delay(Timeout.Infinite, ct);
        return true;
    }
    catch (Exception ex)
    {
        Log.Error(ex, "启动代理时发生错误");
        return false;
    }
}

internal record Services(
    WebNexusApi Api,
    Channel4399Register Register,
    C4399 C4399,
    X19 X19,
    StandardYggdrasil Yggdrasil
);

internal static class AppState
{
    public static Services? Services;
    public static ConcurrentDictionary<string, string> Accounts { get; } = new();
    public static ConcurrentDictionary<string, X19AuthenticationOtp> Auths { get; } = new();
    public static string? SelectedAccountId;
    public static ConcurrentDictionary<string, ChannelInfo> Channels { get; } = new();
    public static ConcurrentDictionary<string, (string account, string password)> PendingCaptchas { get; } = new();
}

internal class ChannelInfo
{
    public string ServerId { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public string Ip { get; set; } = string.Empty;
    public int Port { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public CancellationTokenSource Cts { get; set; } = new CancellationTokenSource();
}