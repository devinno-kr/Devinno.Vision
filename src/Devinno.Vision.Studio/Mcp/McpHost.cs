using Devinno.Vision.Studio.Session;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Devinno.Vision.Studio.Mcp;

/// <summary>같은 프로세스 안의 HTTP MCP 서버 (127.0.0.1 만). 주소: http://127.0.0.1:{port}/mcp</summary>
public sealed class McpHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private McpHost(WebApplication app, Uri endpoint)
    {
        _app = app;
        Endpoint = endpoint;
    }

    /// <summary>MCP 주소.</summary>
    public Uri Endpoint { get; }

    /// <summary>서버를 시작한다. port 0 = 빈 포트 아무거나.</summary>
    /// <exception cref="IOException">포트를 쓸 수 없다 (이미 사용 중 등).</exception>
    public static async Task<McpHost> StartAsync(StudioSession session, int port, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ApplicationName = typeof(McpHost).Assembly.GetName().Name });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, port));
        builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = "Devinno Vision Studio", Title = "Devinno Vision Studio", Version = "0.1.0" };
                o.ServerInstructions = StudioTools.Instructions;
            })
            .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.StatefulForInitializeClients);
        foreach (var tool in StudioTools.Create(new StudioTools(session)))
            builder.Services.AddSingleton(tool);

        var app = builder.Build();
        app.MapMcp("/mcp");
        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new McpHost(app, new Uri(new Uri(address.Replace("[::1]", "127.0.0.1")), "/mcp"));
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));   // 붙어 있는 클라이언트를 오래 기다리지 않는다
        try
        {
            await _app.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
