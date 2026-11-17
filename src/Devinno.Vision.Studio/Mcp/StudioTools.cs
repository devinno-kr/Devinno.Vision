using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Devinno.Vision.Recipes;
using Devinno.Vision.Studio.Session;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Devinno.Vision.Studio.Mcp;

/// <summary>
/// MCP 도구 31개. 모두 <see cref="StudioSession"/> 의 같은 명령을 부른다 (화면과 같은 입구).
/// 실패는 IsError=true 와 고칠 방법이 든 문구로 돌려준다.
/// </summary>
internal sealed class StudioTools(StudioSession session)
{
    public const string Instructions =
        "Devinno Vision Studio builds an image inspection logic (a list of checks) and tests it on image folders. " +
        "Work in this order: new_project (or open_project) -> set_base_image -> add_check (add the BlobAlign check first; it finds the part and the other checks' ROIs follow it) " +
        "-> set_roi / update_check for each check -> teach_base (stores the part position of the base image) -> add_test_set -> run_test " +
        "-> get_failures and view_test_item to see why an image was judged wrongly -> fix ROIs or properties -> run_test again. save_project writes studio.json. " +
        "Coordinates are base-image pixels. Lengths are judged in mm when a calibration is set (set_calibration), otherwise in px. " +
        "Check kinds: BlobAlign (part position), BlobCount (number of blobs in the ROI within Min..Max), PixelCount (pixels with brightness Low..High must be at most Max), " +
        "Brightness (mean brightness within Min..Max), Length (distance between the left and right edges in the ROI, Nominal +Plus/-Minus). " +
        "Use get_project to see every check's id and properties. " +
        "The reference can also (or instead) be a good-part model learned from good images: set_good_folder (a folder whose images are all good parts) -> optionally set_good_roi -> " +
        "train_good_model (takes a while; cancel_training stops it). The model then judges OK/NG first; standard checks can be given the role Reason " +
        "(update_check {\"Role\":\"Reason\"}) so they only name the defect when the part is NG and can no longer turn a good part into NG. " +
        "After changing the alignment or the model region, train again. add_good_image adds a good image that was judged NG. " +
        "Check kind Onnx classifies its ROI with a defect model: set_defect_folder (subfolders good + one per defect kind) -> train_defect_model " +
        "(PyTorch via Python; setup_python installs it once, several hundred MB) or import_onnx (an .onnx with a .json describing Labels/GoodLabel/InputWidth/InputHeight/Normalize). " +
        "It is NG when the probability of not good is above Threshold (0-1) and its NG code is the defect name. " +
        "When the logic is done: save_project, then pack_project writes a .dvpkg for the field app.";

    public static McpServerTool[] Create(StudioTools t) =>
    [
        McpServerTool.Create(t.NewProject, Opt("new_project", "Create a new project", "Create a new, empty project in a folder (the folder is created; it must not already contain studio.json). Refused while the open project has unsaved changes: call save_project first, or pass discard_changes=true only when the user agreed to lose them.", readOnly: false)),
        McpServerTool.Create(t.OpenProject, Opt("open_project", "Open a project", "Open the project saved in a folder (folder/studio.json). Refused while the open project has unsaved changes: call save_project first, or pass discard_changes=true only when the user agreed to lose them.", readOnly: false)),
        McpServerTool.Create(t.SaveProject, Opt("save_project", "Save the project", "Save the open project to its folder (studio.json).", readOnly: false, idempotent: true)),
        McpServerTool.Create(t.GetProject, Opt("get_project", "Show the project", "Return the whole open project as JSON: name, base image, calibration, every check (id, Kind, Name, Roi and kind-specific properties) and test folders.", readOnly: true)),
        McpServerTool.Create(t.SetBaseImage, Opt("set_base_image", "Set the base image", "Copy an image file into the project as the base image. ROIs are drawn in its pixel coordinates and teach_base uses it.", readOnly: false, idempotent: true)),
        McpServerTool.Create(t.SetCalibration, Opt("set_calibration", "Set the calibration", "Set millimetres per pixel so Length checks judge in mm. Omit mm_per_px (or pass null) to remove it; lengths are then judged in px.", readOnly: false, idempotent: true)),
        McpServerTool.Create(t.AddCheck, Opt("add_check", "Add a check", "Add a check. kind is one of BlobAlign, BlobCount, PixelCount, Brightness, Length. Names must be unique. BlobAlign always goes first and only one is allowed. Returns the new check's id.", readOnly: false)),
        McpServerTool.Create(t.RemoveCheck, Opt("remove_check", "Remove a check", "Remove a check by id.", readOnly: false, destructive: true)),
        McpServerTool.Create(t.MoveCheck, Opt("move_check", "Move a check", "Move a check to a position (0 = first). Nothing can move in front of the BlobAlign check.", readOnly: false, idempotent: true)),
        McpServerTool.Create(t.SetRoi, Opt("set_roi", "Set a check's ROI", "Set the region a check looks at, in base-image pixels: x, y = top-left corner, width, height. angle (degrees, clockwise) rotates the rectangle around its centre.", readOnly: false, idempotent: true)),
        McpServerTool.Create(t.UpdateCheck, Opt("update_check", "Change a check's properties", "Change some properties of a check. properties_json is a JSON object with only the keys to change, for example {\"Min\":1,\"Max\":1} or {\"Blob\":{\"Threshold\":80}}. Kind and Id cannot change. Unknown keys are rejected with the list of valid ones.", readOnly: false, idempotent: true)),
        McpServerTool.Create(t.TeachBase, Opt("teach_base", "Teach the base position", "Run the BlobAlign check on the base image and store the part position as the reference. Run it again after changing the BlobAlign settings or the base image.", readOnly: false, idempotent: true)),
        McpServerTool.Create(t.RunImage, Opt("run_image", "Run on one image", "Run the current logic on one image file. Returns the judgment and values, and the result image with ROIs and findings drawn.", readOnly: true)),
        McpServerTool.Create(t.AddTestSet, Opt("add_test_set", "Add a test folder", "Add a folder of test images. rule FileName: the label is the token after the first '_' in the file name (02_missinghole.bmp -> missinghole). rule Folder: each subfolder name is the label (good/, crack/ …). Images labelled 'good' are expected OK, all others NG. With the FileName rule a file without '_' has no label: it runs but is not judged. The project's own folder (or a folder inside or above it) cannot be a test folder.", readOnly: false)),
        McpServerTool.Create(t.RunTest, Opt("run_test", "Run a folder test", "Run the current logic on every image of a test folder (set_index from add_test_set, 0 = first). Returns correct/total and a table per label.", readOnly: true)),
        McpServerTool.Create(t.GetFailures, Opt("get_failures", "List wrong images", "List the images the last run_test judged wrongly, with their index, label, expected and actual result, NG codes and errors. Look at one with view_test_item.", readOnly: true)),
        McpServerTool.Create(t.ViewTestItem, Opt("view_test_item", "View a test image result", "Re-run one image of the last run_test (index from get_failures) and return the result image with ROIs and findings drawn.", readOnly: true)),
        McpServerTool.Create(t.SetGoodFolder, Opt("set_good_folder", "Set the good image folder", "Set the folder of good images for the good-part model. Every image directly in the folder must be a good part. Warns when fewer than 100 images or when it overlaps a test folder.", readOnly: false, idempotent: true)),
        McpServerTool.Create(t.SetGoodRoi, Opt("set_good_roi", "Set the good model region", "Set the region the good-part model looks at (base image pixels; it follows the part when there is an alignment check). Width 0 = whole image. A trained model must then be trained again.", readOnly: false, idempotent: true)),
        McpServerTool.Create(t.TrainGoodModel, Opt("train_good_model", "Train the good-part model", "Learn the good-part model from the good folder: each image is cut out like a run does, most are learned, the rest set the limit (highest score x 1.05). Takes from seconds to minutes; other commands are refused meanwhile.", readOnly: false)),
        McpServerTool.Create(t.CancelTraining, Opt("cancel_training", "Cancel training", "Cancel a running train_good_model. The previous model stays.", readOnly: false)),
        McpServerTool.Create(t.RemoveGoodModel, Opt("remove_good_model", "Remove the good model", "Remove the good-part model from the logic (its files are deleted on save_project). Reason checks then no longer judge; change them to Judge if needed.", readOnly: false, destructive: true)),
        McpServerTool.Create(t.AddGoodImage, Opt("add_good_image", "Add a good image", "Add one good image (for example a good part that run_test judged NG) to the good-part model's training images. Train again to use it.", readOnly: false)),
        McpServerTool.Create(t.SetDefectFolder, Opt("set_defect_folder", "Set an ONNX check's defect folder", "For an Onnx check: a folder with a subfolder 'good' and one subfolder per defect kind (the subfolder name is the label).", readOnly: false, idempotent: true)),
        McpServerTool.Create(t.TrainDefectModel, Opt("train_defect_model", "Train an ONNX check's defect model", "Cut every image of the defect folder like a run does (alignment, then the check's ROI), train a classifier with PyTorch and attach the resulting model.onnx to the check. Takes minutes; cancel_training stops it.", readOnly: false)),
        McpServerTool.Create(t.ImportOnnx, Opt("import_onnx", "Import an ONNX model", "Attach an existing .onnx to an Onnx check. A description file (same name .json, or model.json in the same folder) must give Labels, GoodLabel, InputWidth, InputHeight and Normalize (ImageNet, Unit or Gray). Output: one score per label [1, N].", readOnly: false)),
        McpServerTool.Create(t.PackProject, Opt("pack_project", "Pack the logic", "Write the saved logic as one recipe package (.dvpkg: manifest with SHA-256 of every file, studio.json without test folders, base image, model folders). A field app opens it with VisionRecipe.Load(path). Save first.", readOnly: false, idempotent: true)),
        McpServerTool.Create(t.StartEmulator, Opt("start_emulator", "Start the virtual camera", "Studio becomes a camera: the images of a test folder are streamed over RTSP (rtsp://PC:8554/studio) and a Modbus TCP slave runs on port 502. Write 1 to holding register 100 (D100) for the next image: Studio sets D100 back to 0 and D0 to 1 before answering, slides the next image in, and sets D0 to 0 when the new image is on the stream. The client inspects when D0 is 0. Needs ffmpeg and MediaMTX.", readOnly: false)),
        McpServerTool.Create(t.SetupMediaMtx, Opt("setup_mediamtx", "Install the RTSP server", "Download MediaMTX (the RTSP server start_emulator needs; MIT, 29 MB from github.com/bluenviron/mediamtx) and install it next to Studio after checking its SHA-256. Does nothing when it is already there. ffmpeg is not installed by this. Downloads a program; ask the user first.", readOnly: false, idempotent: true)),
        McpServerTool.Create(t.StopEmulator, Opt("stop_emulator", "Stop the virtual camera", "Stop the RTSP stream and the Modbus slave.", readOnly: false)),
        McpServerTool.Create(t.EmulatorStatus, Opt("emulator_status", "Virtual camera status", "Whether the virtual camera runs, its RTSP address, the current image, whether it is sliding (D0) and the Modbus client count.", readOnly: true)),
        McpServerTool.Create(t.SetupPython, Opt("setup_python", "Set up Python for training", "Create Studio's Python environment and install PyTorch (CPU), onnx, numpy and pillow. Downloads several hundred MB; ask the user first.", readOnly: false)),
    ];

    public const int Count = 31;

    public CallToolResult NewProject(
        [Description("Project folder (absolute path).")] string folder,
        [Description("Logic name.")] string name,
        [Description("Drop unsaved changes of the open project (only with the user's consent).")] bool discard_changes = false) => Of(session.NewProject(folder, name, discard_changes));

    public CallToolResult OpenProject(
        [Description("Project folder containing studio.json.")] string folder,
        [Description("Drop unsaved changes of the open project (only with the user's consent).")] bool discard_changes = false) => Of(session.OpenProject(folder, discard_changes));

    public CallToolResult SaveProject() => Of(session.SaveProject());

    public CallToolResult GetProject() => session.ProjectJson() is { } json ? Text(json) : Error(StudioSession.NoProject);

    public CallToolResult SetBaseImage([Description("Image file path (png, bmp, jpg, tif).")] string path) => Of(session.SetBaseImage(path));

    public CallToolResult SetCalibration([Description("Millimetres per pixel, or null for none.")] double? mm_per_px = null) => Of(session.SetCalibration(mm_per_px));

    public CallToolResult AddCheck([Description("BlobAlign, BlobCount, PixelCount, Brightness or Length.")] string kind, [Description("Unique check name.")] string name) => Of(session.AddCheck(kind, name));

    public CallToolResult RemoveCheck([Description("Check id.")] string id) => Of(session.RemoveCheck(id));

    public CallToolResult MoveCheck([Description("Check id.")] string id, [Description("New position, 0 = first.")] int index) => Of(session.MoveCheck(id, index));

    public CallToolResult SetRoi(
        [Description("Check id.")] string id,
        [Description("Left edge (px).")] double x,
        [Description("Top edge (px).")] double y,
        [Description("Width (px).")] double width,
        [Description("Height (px).")] double height,
        [Description("Rotation in degrees around the centre (optional).")] double? angle = null)
    {
        var roi = angle is { } a && a != 0
            ? VisionRoi.Rotated(x + width / 2, y + height / 2, width, height, a)
            : VisionRoi.Rect(x, y, width, height);
        return Of(session.SetRoi(id, roi));
    }

    public CallToolResult UpdateCheck([Description("Check id.")] string id, [Description("JSON object with the properties to change.")] string properties_json)
    {
        JsonObject? props;
        try
        {
            props = JsonNode.Parse(properties_json) as JsonObject;
        }
        catch (JsonException ex)
        {
            return Error(Texts.T("mcp.props.notJson", ex.Message));
        }
        return props is null ? Error(Texts.T("mcp.props.notObject")) : Of(session.UpdateCheck(id, props));
    }

    public CallToolResult TeachBase() => Of(session.TeachBase());

    public CallToolResult SetGoodFolder([Description("Folder with good images only.")] string folder) => Of(session.SetGoodFolder(folder));

    public CallToolResult SetGoodRoi(
        [Description("Left edge (px).")] double x,
        [Description("Top edge (px).")] double y,
        [Description("Width (px); 0 = whole image.")] double width,
        [Description("Height (px); 0 = whole image.")] double height) => Of(session.SetGoodRoi(width > 0 && height > 0 ? VisionRoi.Rect(x, y, width, height) : default));

    public async Task<CallToolResult> TrainGoodModel() => Of(await session.TrainGoodModelAsync());

    public CallToolResult CancelTraining() => Of(session.CancelTraining());

    public CallToolResult RemoveGoodModel() => Of(session.RemoveGoodModel());

    public CallToolResult AddGoodImage([Description("Image file path.")] string path) => Of(session.AddGoodImage(path));

    public CallToolResult SetDefectFolder([Description("Onnx check id.")] string id, [Description("Folder with subfolders good and one per defect kind.")] string folder) => Of(session.SetDefectFolder(id, folder));

    public async Task<CallToolResult> TrainDefectModel([Description("Onnx check id.")] string id) => Of(await session.TrainDefectModelAsync(id));

    public CallToolResult ImportOnnx([Description("Onnx check id.")] string id, [Description(".onnx file path.")] string path) => Of(session.ImportOnnx(id, path));

    public async Task<CallToolResult> SetupPython() => Of(await session.SetupPythonAsync());

    public CallToolResult StartEmulator(
        [Description("Test folder index, 0 = first.")] int set_index,
        [Description("Modbus TCP port (default 502).")] int modbus_port = 502,
        [Description("RTSP port (default 8554).")] int rtsp_port = 8554,
        [Description("Frames per second (default 10).")] int fps = 10,
        [Description("Milliseconds between sending the new image and setting D0 to 0 — margin for the client's RTSP delay (default 500).")] int settle_ms = 500,
        [Description("Listen on 127.0.0.1 only: no Windows firewall prompt, but other PCs cannot connect (default false).")] bool local_only = false) =>
        Of(session.StartEmulator(set_index, new EmulatorOptions(modbus_port, rtsp_port, Fps: fps, SettleMs: settle_ms, LocalOnly: local_only)));

    public async Task<CallToolResult> SetupMediaMtx() => Of(await session.SetupMediaMtxAsync());

    public CallToolResult StopEmulator() => Of(session.StopEmulator());

    public CallToolResult EmulatorStatus() => session.EmulatorStatus is { } e
        ? Text($"{(e.Running ? "running" : "stopped")} {e.Url} modbus {e.ModbusPort} image {e.Index + 1}/{e.Count} {e.File} D0={(e.Busy ? 1 : 0)} clients {e.Clients}{(e.Error is null ? "" : " error: " + e.Error)}")
        : Text("not running");

    public CallToolResult PackProject([Description("Package file path (.dvpkg).")] string path) => Of(session.PackProject(path));

    public async Task<CallToolResult> RunImage([Description("Image file path.")] string path)
    {
        var r = await session.RunImageAsync(path);
        if (!r.Ok)
            return Error(r.Message);
        var sb = new StringBuilder($"{r.Message}\n");
        foreach (var v in r.Values)
            sb.AppendLine($"- {v.Check} / {v.Name}: {v.Value:0.###}{(v.Unit is null ? "" : " " + v.Unit)} [{Range(v)}] {(v.Ok ? "OK" : "NG")}");
        return WithImage(sb.ToString(), r.Png);
    }

    public CallToolResult AddTestSet([Description("Folder with test images.")] string folder, [Description("FileName or Folder.")] string rule)
    {
        if (!Enum.TryParse<LabelRule>(rule, ignoreCase: true, out var r) || !Enum.IsDefined(r))
            return Error(Texts.T("mcp.rule", rule));
        return Of(session.AddTestSet(folder, r));
    }

    public async Task<CallToolResult> RunTest([Description("Test folder index, 0 = first.")] int set_index)
    {
        var r = await session.RunTestAsync(set_index);
        if (!r.Ok)
            return Error(r.Message);
        var sb = new StringBuilder($"{r.Correct}/{r.Total} correct" + (r.Unlabeled > 0 ? $" ({r.Unlabeled} unlabeled images run but not judged)" : "") + "\n");
        foreach (var l in r.Labels)
            sb.AppendLine($"- {l.Label} (expect {(l.ExpectOk ? "OK" : "NG")}): {l.Correct}/{l.Count}");
        if (r.Correct < r.Total)
            sb.AppendLine("Use get_failures to list the wrong images.");
        return Text(sb.ToString());
    }

    public CallToolResult GetFailures()
    {
        if (session.LastReport is not { } r)
            return Error(Texts.T("mcp.noReport"));
        var wrong = r.Items.Select((item, index) => (item, index)).Where(x => x.item.Correct == false).ToList();
        if (wrong.Count == 0)
            return Text(Texts.T("mcp.noFailures", r.Correct, r.Total));
        var sb = new StringBuilder($"{wrong.Count} wrong of {r.Total}:\n");
        foreach (var (i, index) in wrong)
            sb.AppendLine($"- index {index}: {Path.GetFileName(i.File)} label={i.Label} expected={(i.ExpectOk == true ? "Ok" : "Ng")} got={i.State}"
                + (i.Codes.Count > 0 ? $" codes={string.Join(",", i.Codes)}" : "") + (i.Error is null ? "" : $" error={i.Error}"));
        return Text(sb.ToString());
    }

    public async Task<CallToolResult> ViewTestItem([Description("Item index from get_failures.")] int index)
    {
        if (session.LastReport is not { } r)
            return Error(Texts.T("mcp.noReport"));
        if (index < 0 || index >= r.Items.Count)
            return Error(Texts.T("mcp.index", r.Items.Count - 1, index));
        var png = await session.RenderTestItemAsync(index);
        if (png is null)
            return Error(Texts.T("mcp.noPicture"));
        var item = r.Items[index];
        return WithImage($"{Path.GetFileName(item.File)} label={item.Label} (re-run with the current logic)", png);
    }

    // ── 내부 ─────────────────────────────────────────────

    private static string Range(ValueRow v) => v.Nominal is { } n
        ? $"{n:0.###} ({v.Min:0.###}..{v.Max:0.###})"
        : $"{(v.Min is { } a ? a.ToString("0.###") : "")}..{(v.Max is { } b ? b.ToString("0.###") : "")}";

    private static CallToolResult Of(CommandResult r) => r.Ok ? Text(r.Message) : Error(r.Message);

    private static CallToolResult WithImage(string text, byte[]? png) => png is null
        ? Text(text)
        : new() { Content = [new TextContentBlock { Text = text }, ImageContentBlock.FromBytes(png, "image/png")] };

    private static CallToolResult Text(string text) => new() { Content = [new TextContentBlock { Text = text }] };

    private static CallToolResult Error(string text) => new() { Content = [new TextContentBlock { Text = text }], IsError = true };

    private static McpServerToolCreateOptions Opt(string name, string title, string description, bool readOnly, bool destructive = false, bool idempotent = false) => new()
    {
        Name = name,
        Title = title,
        Description = description,
        ReadOnly = readOnly,
        Destructive = destructive,
        Idempotent = idempotent || readOnly,
        OpenWorld = false,
    };
}
