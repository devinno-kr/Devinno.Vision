# Devinno.Vision Rules (read first)

This is the **document to read first** when you write app code that inspects images with Devinno.Vision. It is written for AI agents and humans.

- This document: principles, which way to use the library, which type to pick, MUST/NEVER rules, symptom → cause → fix, how to verify, what is not supported.
- Running a recipe made in Studio, the result, the recipe file and package: [`vision-recipe.md`](vision-recipe.md)
- Writing an inspection in C# (jobs, steps, ROI, pose, judging, images): [`vision-jobs.md`](vision-jobs.md)
- Toolkit, ONNX models, cameras: [`vision-toolkit.md`](vision-toolkit.md)
- C# API (every public member): IntelliSense XML `Devinno.Vision.xml`

---

<a id="principles"></a>
## 1. Core principles

1. **Nothing exists that is not in the docs or the IntelliSense XML.** Do not invent types, methods, properties, check kinds or JSON properties. There is no pattern matching tool, no OCR, no barcode reader, no `VisionStation` (section 8).
2. **If a document and the code disagree, the code is right.**
3. **One image → one job → one result.** A job is a list of steps run in order; each step is one check item with its own ROI. Five check items on one image are five steps in one job, not five jobs.
4. **A run never throws for inspection problems.** Exceptions inside a step, timeouts, a missing part and a missing alignment all end up in the result as `VisionState.Error` with a reason. `RunAsync` throws only for wrong arguments (a `null` image, or `null` part data on `VisionJob<TSetup, TData>`).
5. **The judgment has three values: `Ok`, `Ng`, `Error`.** `Ng` = inspected and defective (`r.Codes` says why). `Error` = could not inspect (`r.Error` says why). Never treat `Error` as OK, and never treat it as an ordinary reject without deciding so on purpose.
6. **Vision judges; the app does everything else.** Triggers, machine sequence, PLC handshake, part IDs, saving NG images, logging, and when to load which recipe are the app's job. The library gives the result format (`r.ToJson()`) and the recipe format, not storage.
7. **OpenCV stays open.** `image.Mat` is the OpenCvSharp `Mat`; any OpenCV function can use it, and `VisionImage.From(mat)` / `ctx.From(mat)` bring a `Mat` back.
8. **Verify, do not guess.** Run the job on known good and known defective images and check `State`, `Codes` and `Values` (section 7).

---

<a id="choose-the-way"></a>
## 2. Choose the way

| Situation | Use | Read |
|---|---|---|
| The inspection was made in Devinno.Vision.Studio (a folder with `studio.json`, or a `.dvpkg` file) | `VisionRecipe.Load(path)` → `recipe.CreateJob()` → `job.RunAsync(image)` | [vision-recipe.md](vision-recipe.md) |
| The inspection is written in the app | `VisionJob`, `VisionJob<TSetup>` or `VisionJob<TSetup, TData>` with `.Add(...)` steps | [vision-jobs.md](vision-jobs.md) |
| Only a helper is needed (find blobs, measure a width, run an ONNX model) with your own OpenCV code | The toolkit directly, outside any job | [vision-toolkit.md](vision-toolkit.md) |

Both ways give the same `VisionJob` and the same `VisionResult`, so the code after `RunAsync` (branching on the state, showing, logging) is the same.

A job made by `recipe.CreateJob()` is an ordinary `VisionJob` holding the recipe's checks. Code steps can be appended with `job.Add(...)`; they run after the recipe's checks ([vision-recipe.md#run-a-recipe](vision-recipe.md#run-a-recipe)). Code steps cannot be placed between the recipe's checks.

<a id="choose-the-type"></a>
### Choose the type

| Need | Type |
|---|---|
| A recipe from Studio | `VisionRecipe` (`Load`, `CreateJob`, `Pack`) |
| A job without settings (quick inspection, test) | `VisionJob` |
| A job whose ROIs and limits live in the app's setup class | `VisionJob<TSetup>` |
| The same, plus one data object per part shared by several jobs (several faces of one part) | `VisionJob<TSetup, TData>` |
| A reusable step as a class | implement `IVisionStep`, add it with `job.Add(step, roi: …)` |
| An image | `VisionImage` (`Load`, `From(mat)`, `Mat`) |
| A region | `VisionRoi.Rect(x, y, w, h)` or `VisionRoi.Rotated(cx, cy, w, h, angle)` |
| Part position and direction | `VisionPose`; from a blob `VisionPose.FromBlob`, for round parts `RoundModel` |
| px → mm | `VisionCalibration.Scale(mmPerPx)` or `VisionCalibration.FromLength(px, mm)`, set on `job.Calibration` |
| Count or locate regions by brightness | `Blob.Find` |
| Mean brightness, unevenness, dark/bright pixel count | `Brightness.Mean`, `Brightness.StdDev`, `Brightness.Count` |
| Width, gap, length between two edges | `Edge.Caliper` |
| Compare with good parts pixel by pixel (aligned, same size) | `DiffModel` |
| Surface anomaly from good parts only (ONNX features) | `AnomalyModel` |
| Classify good / defect kinds with a trained ONNX classifier | `DefectModel` |
| Run any ONNX image model | `OnnxModel` |
| Images from a folder (replay, tests) | `FileCamera` |
| An RTSP stream (IP camera, Studio's camera emulator) | `RtspCamera` |
| A USB (UVC) camera | `FlashCapCamera` (package Devinno.Vision.FlashCap) |
| Show a result on a Devinno.UI screen | `DvVisionView` (package Devinno.Vision.UI) |

<a id="namespaces"></a>
### Namespaces

| `using` | Types |
|---|---|
| `Devinno.Vision` | `VisionRecipe`, `VisionJob` (all three), `VisionContext`, `VisionResult`, `VisionState`, `StepResult`, `StepState`, `VisionValue`, `VisionOverlay`, `VisionImage`, `VisionImageInfo`, `VisionRoi`, `VisionPose`, `VisionCalibration`, `VisionModel`, `VisionSetup`, `IVisionStep`, `AfterNg`, `StepRole`, `ReasonRun`, the toolkit (`Blob`, `BlobParams`, `Brightness`, `Edge`, `RoundModel`, `DiffModel`, `OnnxModel`, `AnomalyModel`, `DefectModel`), the cameras (`IVisionCamera`, `CameraTrigger`, `FileCamera`, `RtspCamera`) |
| `Devinno.Vision.Recipes` | The recipe's data types: `CheckDef` and the check kinds (`BlobAlignCheck`, `BlobCountCheck`, `PixelCountCheck`, `BrightnessCheck`, `LengthCheck`, `OnnxCheck`), `GoodModel`, `GoodModelTraining`, `GoodModelProgress`, `GoodModelResult`, `TestSet`, `LabelRule`. An app that only loads and runs a recipe does not need this namespace |
| `OpenCvSharp` | `Mat`, `Point2d`, `Rect`, `Rect2d`, `Size`, `Cv2` — points and boxes in the API are OpenCvSharp types |
| `Devinno.Vision.UI` | `DvVisionView` |
| `Devinno.Vision.FlashCap` | `FlashCapCamera` |

<a id="project-setup"></a>
### Project setup

- Target `net9.0` (or `net9.0-windows…` for a WinForms app).
- Reference `Devinno.Vision`. It brings `OpenCvSharp4` and `Microsoft.ML.OnnxRuntime` (CPU).
- **Add the OpenCV native runtime package yourself**, with the same version as the `OpenCvSharp4` package that `Devinno.Vision` references: on Windows `OpenCvSharp4.runtime.win`. Without it the first image call fails with an exception that names `OpenCvSharpExtern`. Other platforms (for example Linux arm64) have their own `OpenCvSharp4.runtime.*` packages; they are not verified with this library.

```xml
<ItemGroup>
  <PackageReference Include="Devinno.Vision" Version="…" />
  <PackageReference Include="OpenCvSharp4.runtime.win" Version="4.13.0.20261107" />
</ItemGroup>
```

---

<a id="minimal-examples"></a>
## 3. Minimal examples

Run a recipe made in Studio:

```csharp
using Devinno.Vision;

using var recipe = VisionRecipe.Load(@"C:\recipes\bracket");   // a folder with studio.json, or a .dvpkg file
var job = recipe.CreateJob();                                    // make it once, run it many times

using var img = VisionImage.Load(@"C:\images\part01.bmp");
using var r = await job.RunAsync(img);

switch (r.State)
{
    case VisionState.Ok:    /* accept */ break;
    case VisionState.Ng:    Console.WriteLine(string.Join(", ", r.Codes)); break;   // "HOLE, LENGTH"
    case VisionState.Error: Console.WriteLine(r.Error); break;                       // could not inspect
}
foreach (var v in r.Values)
    Console.WriteLine($"{v.Name} = {v.Value:0.##} {v.Unit} ({(v.Ok ? "OK" : v.Code)})");
```

Write the same kind of inspection in code:

```csharp
using Devinno.Vision;

var job = new VisionJob("bracket")
    .Add("align", pose: "body", ctx =>
    {
        var part = Blob.Find(ctx.Image, minArea: 20000).FirstOrDefault();
        if (part is null) { ctx.Fail("part not found"); return; }
        ctx.SetPose(VisionPose.FromBlob(part), reference: new VisionPose(330.4, 256.0, 0));   // reference = the pose on the base image
    })
    .Add("left hole", roi: () => VisionRoi.Rect(194, 251, 60, 60), ctx =>
    {
        var holes = Blob.Find(ctx.Image, ThresholdMode.Fixed, threshold: 80, invert: true, minArea: 300);
        ctx.Draw(holes);
        ctx.Value("left holes", holes.Length, min: 1, max: 1, code: "HOLE");
    })
    .Add("length", roi: () => VisionRoi.Rect(100, 305, 470, 30), ctx =>
    {
        var e = Edge.Caliper(ctx.Image, EdgePolarity.BrightObject, minContrast: 8);
        ctx.Draw(e);
        ctx.Value("length", e.Distance, nominal: 410, plus: 5, minus: 5, code: "LENGTH", unit: "px");
    });

using var img = VisionImage.Load(path);
using var r = await job.RunAsync(img);
```

---

<a id="must-never"></a>
## 4. MUST / NEVER

### Ownership and disposal

- **MUST** receive every frame and every result with `using`: `using var img = …; using var r = await job.RunAsync(img);`. The result borrows the image, so it must be disposed first — two `using var` lines in this order do that.
- **MUST** dispose a `VisionRecipe` when the app no longer runs its jobs (it holds the loaded ONNX models). **NEVER** dispose it while its jobs are still used: after that the model steps end in Error.
- **MUST** dispose cameras with `await using` (`IVisionCamera` is `IAsyncDisposable`), and `OnnxModel`, `AnomalyModel`, `DefectModel` with `using`/`Dispose`.
- **NEVER** dispose `image.Mat`, `r.Image`, `StepResult.Image` or `VisionModel.Image`. Dispose the owner instead (the image you created, the result).
- **NEVER** keep an image created inside a step (`ctx.Image`, `ctx.Crop(...)`, `img.Gray()`, …) for use after the run: it is released when the run ends. To keep one, call `.Keep()` (then the app disposes it) or `.Clone()` inside the step.
- **NEVER** use `r.Image` or `r.Steps[...].Image` after the result (or the frame) is disposed. To show a result later, hand it to the view first (`DvVisionView.Show` copies what it needs) or copy the data you need.

### Threads

- `RunAsync` does its work on a thread-pool thread and returns a task; `await` it. **NEVER** block on it with `.Result` or `.Wait()` on a UI thread.
- A job runs one image at a time. Calling `RunAsync` on a job that is still running returns `VisionState.Error` with `Error == "Busy"` at once. **MUST** await the previous run, or make one job per parallel run (`recipe.CreateJob()` can be called several times).
- Inside a step, `ctx` is thread-safe: tasks the step starts may record values and drawings, as long as they finish before the step returns.
- `OnnxModel.Run` may be called from several threads at once.

### Jobs and results

- **MUST** make the job once (at startup or when the recipe changes) and reuse it for every image. **NEVER** call `VisionRecipe.Load` or `CreateJob` per image.
- **MUST** make a new job after editing a recipe in memory: `CreateJob` copies the values, so an existing job does not see later edits.
- **MUST** handle all three states. `r.Ok` is `true` only for `VisionState.Ok`.
- **MUST** branch on `r.Codes` (NG codes) and `VisionValue.Code`, not on value names or reason texts: in a recipe job the value names contain the check names the engineer typed, and some built-in texts are Korean (see [vision-recipe.md#recipe-names](vision-recipe.md#recipe-names)).
- **MUST** show or log `r.Disabled` when it is not empty: a disabled step does not affect the judgment, so a part can pass without being checked.
- **NEVER** change the properties of a setup object a job is using in place. Copy it (`VisionSetup.Copy`), edit the copy, then assign the copy to `job.Setup` ([vision-jobs.md#setup](vision-jobs.md#setup)).
- **NEVER** rename the properties of your setup class (or of `BlobParams`) after recipes were saved: old JSON would silently fall back to the defaults.

### Steps (code-first)

- **MUST** give every step a unique name in its job; a duplicate name throws `ArgumentException` when the step is added.
- **MUST** judge through `ctx`: `ctx.Value(name, value, min, max, code)` for a measured value, `ctx.Judge(ok, code)` for a direct judgment, `ctx.Fail(reason)` when the step cannot inspect. A step that records nothing is OK.
- **MUST** use `ctx.Image` (the step's ROI, already cut, upright and following the part) rather than cropping the original frame yourself.
- **NEVER** throw to report a defect. An exception makes the step, and the whole result, `Error`.
- **NEVER** convert drawing coordinates yourself: `ctx.Draw…` takes points in the image they were found in (`from:` names it, default `ctx.Image`) and stores them in original-image coordinates.

---

<a id="symptoms"></a>
## 5. Symptom → cause → fix

| Symptom | Cause | Fix |
|---|---|---|
| Exception naming `OpenCvSharpExtern` at the first image call | The OpenCV native runtime is missing | Add `OpenCvSharp4.runtime.win` (same version as `OpenCvSharp4`) to the app project ([#project-setup](#project-setup)) |
| `r.State == Error`, `r.Error == "Busy"` | `RunAsync` was called while the same job was still running | Await the previous run; for parallel runs make one job each |
| `r.Error` ends with `Setup is not set.` | `VisionJob<TSetup>` / `<TSetup, TData>` ran with `job.Setup == null` | Assign `job.Setup` before the first run |
| `r.Error` ends with `the job has no steps.` | The job has no steps | Add steps before running |
| A step is `Error` with `The ROI is not set.` | The step's ROI is the default value (`Shape == None`) or has zero size | Give the ROI a value (`VisionRoi.Rect(...)`); in a setup class, teach or load it first |
| A step is `Error` with `The ROI (…) is outside the image` | Less than half of the ROI is inside the image (after following the part) | Check the ROI and the alignment; a ROI partly outside is fine while at least half is inside |
| Steps are `Skipped` with `Pose '…' missing (…)` and the result is `Error` | The alignment step did not set its pose (it failed, was NG without calling `SetPose`, or was disabled) | Fix the alignment; the reason text carries the alignment step's state. A ROI that must not move: `follow: ""` |
| `ObjectDisposedException`: `This image was released when the run of '…' ended` | An image created inside a step was used after the run | Call `.Keep()` or `.Clone()` inside the step, and dispose the kept image yourself |
| A step is `Error` with `The job has no calibration…` | `ctx.Mm` was used without `job.Calibration` | Set `job.Calibration` (in a recipe: set the calibration in Studio) |
| A length value is `NaN` and the step is NG | `Edge.Caliper` did not find both edges (`Found == false`) | Check the ROI direction (width along the measuring direction), the polarity and `minContrast` |
| The result is OK although a check did not run | The step is disabled | Read `r.Disabled`; enable it with `job.Enable(name)` |
| `ArgumentException`: `already has a step named` / `no earlier step makes the pose` when building a job | Duplicate step name / `follow:` names a pose no earlier step declares | Use unique names; declare the pose with `pose:` on an earlier step |
| `VisionRecipe.Load` throws `FileNotFoundException` | No `studio.json` in the folder, the `.dvpkg` file is missing, or a model folder the recipe names is missing | Pass the recipe folder itself (the one that holds `studio.json`); copy the whole folder including `models/` |
| `VisionRecipe.Load` throws `InvalidDataException` | Broken JSON, a check kind this version does not know, a newer format version, or a package whose files do not match its manifest | Update the library for newer recipes; make the package again with `Pack` instead of editing it |
| `recipe.CreateJob()` throws `InvalidOperationException` | The recipe has no checks, a check cannot run (the message says which), or the good-part model must be trained again because the alignment or its region changed | Fix the recipe in Studio and save it |
| A model step of a recipe job is `Error` after it worked before | The recipe was disposed (or its model was trained again) while the job was still used | Keep the recipe alive as long as its jobs; after training, make a new job |
| `VisionImage.Load` throws `NotSupportedException` | The file is not 8-bit gray or 8-bit color (for example 16-bit) | Convert the image to 8-bit first |
| `NotSupportedException` when setting `Trigger`, `ExposureUs` or `Gain` | The camera cannot do it (`FileCamera`, `RtspCamera` and `FlashCapCamera` have no hardware trigger, exposure or gain control) | Do not set it for these cameras; read `double.NaN` as "unknown" |
| An ONNX model gives poor results without any error | Wrong input size, channel order or normalization | Match `inputWidth`, `inputHeight` and `VisionTensorFormat` to the training exactly |

---

<a id="app-shape"></a>
## 6. The shape of an inspection app

```csharp
using Devinno.Vision;

// Startup: load once
using var recipe = VisionRecipe.Load(recipePath);
var job = recipe.CreateJob();
await using var camera = await RtspCamera.OpenAsync("rtsp://192.168.0.10:8554/studio");

// Per part (called by the app's trigger: a PLC bit, a button, a timer)
async Task<VisionState> InspectAsync(CancellationToken ct)
{
    using var img = await camera.Grab(ct);            // a frame taken after this call
    using var r = await job.RunAsync(img, ct);
    Log(r.ToJson());                                   // the app decides where results go
    if (r.State != VisionState.Ok)
        img.Save(Path.Combine(ngFolder, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.png"));
    return r.State;                                    // the app tells the PLC
}
```

- `Log` and `ngFolder` are the app's. The library has no logger and saves nothing by itself.
- Updating the screen from the result follows the UI library's thread rules (for Devinno.UI see its `llm-rules.md`). `DvVisionView.Show(r)` may be called from any thread and copies what it needs, so the result can be disposed right after.
- To change recipes at run time: load the new recipe, make its job, swap the two references, then dispose the old recipe after its last run has returned.

---

<a id="verify"></a>
## 7. How to verify

1. **Build.** The project must build without errors.
2. **Run on saved images.** Put known good and known defective images in a folder and run the job on each. `FileCamera` gives them through the camera interface, so the production code path is tested unchanged:

```csharp
await using var camera = new FileCamera(@"C:\images\bracket", loop: false);
foreach (var _ in camera.Files)
{
    using var img = await camera.Grab(default);
    using var r = await job.RunAsync(img);
    Console.WriteLine($"{Path.GetFileName(camera.LastFile)}: {r.State} [{string.Join(", ", r.Codes)}] {r.Error}");
}
```

3. **Check each image against what you expect**: good images `Ok`, each defect image `Ng` with the expected code, none `Error`. An `Error` on a good image is a setup problem, not a defect.
4. **Read the details when a result is wrong**: `r.ToJson(indented: true)` lists every value with its limits, every step with its state, reason and time, the poses and the overlays.
5. **Look at the picture.** Draw the overlays on a copy of the image and open the file ([vision-recipe.md#draw-overlays](vision-recipe.md#draw-overlays) has the code). Check that every ROI sits on the feature it should inspect — a ROI in the wrong place often still gives a number.
6. **Test the failure paths**: an image without the part must give `Error` (or the NG code you chose), not `Ok`.

When the inspection was made in Studio, run the same images in Studio's test screen too; the job is the same code, so the judgments must match.

---

<a id="not-supported"></a>
## 8. Not supported

Do not write code that assumes any of these.

- Pattern (template) matching, OCR, barcode and QR reading, color classification tools. Use OpenCV directly on `image.Mat` if you need them.
- `VisionStation` or any object that groups several jobs; jobs are independent, and `VisionResult.Worst` combines results.
- Placing code steps between a recipe's checks; `AddRecipeSteps` does not exist. Code steps can only be appended to the job `recipe.CreateJob()` returns.
- New recipe check kinds from the app: `CheckDef` cannot be subclassed outside the library, and there is no kind registration. The kinds are `BlobAlign`, `BlobCount`, `PixelCount`, `Brightness`, `Length`, `Onnx`.
- Round-part alignment (`RoundModel`) and difference models (`DiffModel`) as recipe checks. They are toolkit types for code steps only.
- Scripts or DLLs inside a recipe or package. A package holds data and model files only.
- Pixel formats other than 8-bit BGR and 8-bit gray (no 16-bit, no float images).
- Lens distortion and perspective correction. `VisionCalibration` is one scale (mm per px).
- GPU inference. `OnnxModel` runs on CPU.
- ONNX models with more than one input, or an input that is not one float image tensor `[1, C, H, W]`. Use ONNX Runtime directly for those.
- Hardware trigger, exposure and gain control in `FileCamera`, `RtspCamera` and `FlashCapCamera`.
- Triggering, queuing, PLC communication, result storage, part tracking. These are the app's job.
- Training defect classifiers from code. Studio trains them (Python); the library only runs the model (`DefectModel`). The good-part model is the exception: `VisionRecipe.TrainGoodModelAsync` trains it without Python.
