using Devinno.Vision.Studio.Mcp;
using Devinno.Vision.Studio.Session;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Devinno.Vision.Studio.Tests;

public class McpTests
{
    private static readonly string[] ToolNames =
    [
        "new_project", "open_project", "save_project", "get_project", "set_base_image", "set_calibration",
        "add_check", "remove_check", "move_check", "set_roi", "update_check", "teach_base", "run_image",
        "add_test_set", "run_test", "get_failures", "view_test_item",
    ];

    private sealed class Server : IAsyncDisposable
    {
        public required McpHost Host { get; init; }
        public required McpClient Client { get; init; }

        public static async Task<Server> StartAsync(StudioSession? session = null)
        {
            var host = await McpHost.StartAsync(session ?? new StudioSession(), port: 0);
            var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = host.Endpoint }));
            return new Server { Host = host, Client = client };
        }

        public async Task<CallToolResult> Call(string tool, Dictionary<string, object?>? args = null)
            => await Client.CallToolAsync(tool, args ?? []);

        public async Task<string> Ok(string tool, Dictionary<string, object?>? args = null)
        {
            var r = await Call(tool, args);
            var text = Text(r);
            Assert.True(r.IsError != true, $"{tool}: {text}");
            return text;
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Host.DisposeAsync();
        }
    }

    private static string Text(CallToolResult r) => string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text));

    // "… id 1a2b3c4d)" → 1a2b3c4d
    private static string IdOf(string text)
    {
        var i = text.LastIndexOf("id ", StringComparison.Ordinal);
        Assert.True(i >= 0, text);
        return text[(i + 3)..].TrimEnd(')', '.', ' ');
    }

    [Fact]
    public async Task Tools_are_listed()
    {
        await using var s = await Server.StartAsync();

        var tools = await s.Client.ListToolsAsync();

        Assert.Equal(ToolNames.Order(), tools.Select(t => t.Name).Order());
        Assert.StartsWith("http://127.0.0.1:", s.Host.Endpoint.ToString());
    }

    [Fact]
    public async Task Bracket_over_mcp_passes_all_18()
    {
        using var dir = new TempDir();
        await using var s = await Server.StartAsync();
        const string hole = """{"Blob":{"Mode":"Fixed","Threshold":80,"Invert":true,"MinArea":300},"Min":1,"Max":1,"Code":"HOLE"}""";

        await s.Ok("new_project", new() { ["folder"] = dir.Path, ["name"] = "브래킷" });
        await s.Ok("set_base_image", new() { ["path"] = Samples.Bracket("01_good.bmp") });
        var align = IdOf(await s.Ok("add_check", new() { ["kind"] = "BlobAlign", ["name"] = "정렬" }));
        await s.Ok("update_check", new() { ["id"] = align, ["properties_json"] = """{"Blob":{"MinArea":20000}}""" });
        await s.Ok("teach_base");
        foreach (var (name, x) in new[] { ("왼쪽 구멍", 194), ("오른쪽 구멍", 417) })
        {
            var id = IdOf(await s.Ok("add_check", new() { ["kind"] = "BlobCount", ["name"] = name }));
            await s.Ok("set_roi", new() { ["id"] = id, ["x"] = x, ["y"] = 251, ["width"] = 60, ["height"] = 60 });
            await s.Ok("update_check", new() { ["id"] = id, ["properties_json"] = hole });
        }
        var tab = IdOf(await s.Ok("add_check", new() { ["kind"] = "PixelCount", ["name"] = "상단 변색" }));
        await s.Ok("set_roi", new() { ["id"] = tab, ["x"] = 268, ["y"] = 192, ["width"] = 134, ["height"] = 36 });
        await s.Ok("update_check", new() { ["id"] = tab, ["properties_json"] = """{"Low":0,"High":100,"Max":200,"Code":"DISCOLOR"}""" });
        var len = IdOf(await s.Ok("add_check", new() { ["kind"] = "Length", ["name"] = "길이" }));
        await s.Ok("set_roi", new() { ["id"] = len, ["x"] = 100, ["y"] = 305, ["width"] = 470, ["height"] = 30 });
        await s.Ok("update_check", new() { ["id"] = len, ["properties_json"] = """{"Polarity":"BrightObject","MinContrast":8,"Nominal":411,"Plus":6,"Minus":6,"Code":"LENGTH"}""" });
        await s.Ok("add_test_set", new() { ["folder"] = Samples.BracketDir, ["rule"] = "FileName" });

        var report = await s.Ok("run_test", new() { ["set_index"] = 0 });

        Assert.Contains("18/18", report);
        Assert.Contains("missinghole", report);
        var project = await s.Ok("get_project");
        Assert.Contains("\"Kind\": \"Length\"", project);
        Assert.Contains("없음", await s.Ok("get_failures"));
    }

    [Fact]
    public async Task Run_image_and_view_test_item_return_images()
    {
        using var dir = new TempDir();
        var session = new StudioSession();
        BracketStudioTests.BuildBracket(session, dir.Path);
        await using var s = await Server.StartAsync(session);

        var run = await s.Call("run_image", new() { ["path"] = Samples.Bracket("02_missinghole.bmp") });
        await s.Ok("run_test", new() { ["set_index"] = 0 });
        var view = await s.Call("view_test_item", new() { ["index"] = 1 });

        Assert.NotEqual(true, run.IsError);
        Assert.Contains("NG", Text(run));
        Assert.Equal("image/png", Assert.Single(run.Content.OfType<ImageContentBlock>()).MimeType);
        Assert.NotEqual(true, view.IsError);
        Assert.Equal("image/png", Assert.Single(view.Content.OfType<ImageContentBlock>()).MimeType);
    }

    [Fact]
    public async Task Bad_input_is_an_error_result_not_a_crash()
    {
        using var dir = new TempDir();
        await using var s = await Server.StartAsync();

        var noProject = await s.Call("add_check", new() { ["kind"] = "BlobCount", ["name"] = "a" });
        await s.Ok("new_project", new() { ["folder"] = dir.Path, ["name"] = "p" });
        var roi = await s.Call("set_roi", new() { ["id"] = "없는id", ["x"] = 0, ["y"] = 0, ["width"] = 10, ["height"] = 10 });
        var json = await s.Call("update_check", new() { ["id"] = "x", ["properties_json"] = "{not json" });
        var rule = await s.Call("add_test_set", new() { ["folder"] = dir.Path, ["rule"] = "Sideways" });
        var view = await s.Call("view_test_item", new() { ["index"] = 0 });

        Assert.True(noProject.IsError);
        Assert.Contains("프로젝트를 먼저", Text(noProject));
        Assert.True(roi.IsError);
        Assert.Contains("검사를 찾을 수 없습니다", Text(roi));
        Assert.True(json.IsError);
        Assert.True(rule.IsError);
        Assert.Contains("FileName", Text(rule));
        Assert.True(view.IsError);
    }

    [Fact]
    public async Task Busy_port_fails_to_start()
    {
        await using var first = await McpHost.StartAsync(new StudioSession(), port: 0);

        await Assert.ThrowsAnyAsync<IOException>(() => McpHost.StartAsync(new StudioSession(), first.Endpoint.Port));
    }
}
