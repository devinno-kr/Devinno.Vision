# Jobs: writing an inspection in C#

Covers: the three job types, steps and `ctx` (`VisionContext`), ROI and alignment (pose), judging and mm calibration, how a run ends, images and ownership, setup classes and teaching.

Read [llm-rules.md](llm-rules.md) first. Running a recipe made in Studio instead: [vision-recipe.md](vision-recipe.md). The tools used inside steps: [vision-toolkit.md](vision-toolkit.md).

---

<a id="job"></a>
## Jobs and steps

A job is a named list of steps. `RunAsync` runs them in order on one image and returns one `VisionResult` ([vision-recipe.md#result](vision-recipe.md#result)).

| Type | Step lambda | `RunAsync` | Use it when |
|---|---|---|---|
| `VisionJob` | `ctx => …` | `RunAsync(image, ct)` | No settings to keep: quick inspections, tests |
| `VisionJob<TSetup>` | `(ctx, s) => …` | `RunAsync(image, ct)` | ROIs, parameters and limits live in the app's setup class `TSetup`; set `job.Setup` before running |
| `VisionJob<TSetup, TData>` | `(ctx, s, d) => …` | `RunAsync(image, data, ct)` | The same, plus one data object per part that several jobs fill in (several faces of one part). `data` must not be `null`; a new part needs a new object |

`TSetup` is any class of yours (a reference type). A step is an `Action` or, for `await` inside the step, a `Func<…, Task>`.

```csharp
using Devinno.Vision;

public sealed class PlateSetup
{
    public BlobParams Body { get; set; } = new() { MinArea = 20000 };
    public VisionPose? BodyRef { get; set; }                                   // taught: the part position on the base image
    public VisionRoi HoleL { get; set; } = VisionRoi.Rect(138, 130, 60, 60);
    public BlobParams Hole { get; set; } = new() { Mode = ThresholdMode.Fixed, Threshold = 80, Invert = true, MinArea = 300 };
    public VisionRoi Width { get; set; } = VisionRoi.Rect(100, 180, 440, 12);
    public double WidthNominal { get; set; } = 100;                             // mm
}

var job = new VisionJob<PlateSetup>("plate") { Setup = new PlateSetup() }
    .Add("align", pose: "body", (ctx, s) =>
    {
        var part = Blob.Find(ctx.Image, s.Body).FirstOrDefault();
        if (part is null) { ctx.Fail("part not found"); return; }
        ctx.Draw(part);
        ctx.SetPose(VisionPose.FromBlob(part, s.BodyRef), s.BodyRef);
    })
    .Add("left hole", roi: s => s.HoleL, (ctx, s) =>
    {
        var holes = Blob.Find(ctx.Image, s.Hole);
        ctx.Draw(holes);
        ctx.Value("left holes", holes.Length, min: 1, max: 1, code: "HOLE");
    })
    .Add("width", roi: s => s.Width, (ctx, s) =>
    {
        var e = Edge.Caliper(ctx.Image, EdgePolarity.BrightObject, minContrast: 8);
        ctx.Draw(e);
        ctx.Value("width", ctx.Mm(e), nominal: s.WidthNominal, plus: 1, minus: 1, code: "WIDTH", unit: "mm");
    });
job.Calibration = VisionCalibration.Scale(0.25);                               // mm per px of this camera setup
```

`Add` overloads (each returns the job, so calls chain; every one takes an optional `timeout:`):

| Overload | Meaning |
|---|---|
| `Add(name, step)` | A step on the whole image |
| `Add(name, pose: "p", step)` | An alignment step: it finds the part and calls `ctx.SetPose`. It declares the pose `p` |
| `Add(name, roi: …, step)` | A step on a ROI. `roi` says where the ROI comes from: `() => roi` on `VisionJob`, `s => s.SomeRoi` on the generic jobs — it is read at every run, so a changed setup is used |
| `Add(name, roi: …, follow: "p", step)` | The same, following the named pose; `follow: ""` = do not follow (a fixed ROI) |
| `Add(stepObject, roi:, pose:, follow:, timeout:, role:)` | A step as an object (`IVisionStep`: `Name` and `RunAsync(ctx)`). The only overload with `role:` ([#run-rules](#run-rules)) |

On `VisionJob` the `pose:` and `follow:` overloads take a synchronous lambda only; the generic jobs have both forms for every overload.

Adding throws `ArgumentException` for an empty or duplicate step name, for a `follow:` that names a pose no earlier step declares, and for a reason step that makes a pose.

Job properties: `Name`, `Setup` (generic jobs), `Calibration`, `Timeout`, `AfterNg`, `ReasonSteps`. Methods: `Enable(step)`, `Disable(step)` (`ArgumentException` for an unknown name), `RunAsync`.

Per-part data, two faces of one part:

```csharp
public sealed class PartData { public double? TopWidth; public double? SideWidth; }

var top  = new VisionJob<TopSetup, PartData>("top")  { Setup = topSetup }
    .Add("width", roi: s => s.Width, (ctx, s, d) => { /* measure */ d.TopWidth = 12.3; });
var side = new VisionJob<SideSetup, PartData>("side") { Setup = sideSetup }
    .Add("width", roi: s => s.Width, (ctx, s, d) => { /* measure */ d.SideWidth = 4.5; });

var data = new PartData();                                    // one object per part
using var r1 = await top.RunAsync(topImage, data);
using var r2 = await side.RunAsync(sideImage, data);
var state = VisionResult.Worst(r1, r2);                       // Error > Ng > Ok
```

---

<a id="ctx"></a>
## The step context (`ctx`)

| Member | Meaning |
|---|---|
| `ctx.Image` | The step's image: its ROI cut out, upright and following the part; the whole image when the step has no ROI |
| `ctx.Step` | The step name |
| `ctx.Cancel` | Signaled when the step's time limit passes or the run is canceled. OpenCV calls cannot be interrupted: check it between long operations and pass it to awaited calls |
| `ctx.Value(name, value, min:, max:, code:, unit:)` | Records a value and judges it against the limits (both inclusive, both optional). Returns whether it is within them |
| `ctx.Value(name, value, nominal:, plus:, minus:, code:, unit:)` | The same against nominal ± tolerance; the result keeps the nominal (`VisionValue.Nominal`, `Deviation`). `plus` and `minus` are positive amounts |
| `ctx.Judge(ok, code)` | Judges without a value; `false` makes the step NG with `code` |
| `ctx.Fail(reason)` | The step could not inspect → `Error` |
| `ctx.Mm(caliper)`, `ctx.Mm(p1, p2, from:)` | A length in mm using `job.Calibration` |
| `ctx.SetPose(current, reference)`, `ctx.SetPose(name, current, reference)` | Sets the part's pose ([#roi-and-pose](#roi-and-pose)) |
| `ctx.Crop(roi)` | Cuts another region (base image coordinates) for a step that looks at several; it follows the same pose as the step's ROI and its border is drawn |
| `ctx.From(mat)` | Brings an OpenCV `Mat` into the run, with the same coordinates as `ctx.Image`; released with the run. The pixels are shared and your `Mat` is not disposed |
| `ctx.Draw(blob)`, `ctx.Draw(blobs)`, `ctx.Draw(caliper)`, `ctx.Draw(anomaly)` | Draws a toolkit result |
| `ctx.DrawBox(rect, label:, from:)`, `DrawLine(p1, p2, …)`, `DrawPoint(p, …)`, `DrawText(p, text, from:)`, `DrawContour(points, …)` | Draws a shape. Coordinates are in the image named by `from:` (default `ctx.Image`) and are converted to the original image |

A step that records nothing ends OK. The border of a step's ROI is drawn automatically.

---

<a id="roi-and-pose"></a>
## ROI and alignment

A `VisionRoi` is a region in the coordinates of the **base image** — the image the ROIs were drawn on when teaching.

| | |
|---|---|
| `VisionRoi.Rect(x, y, width, height)` | Axis-aligned; `x`, `y` = top-left corner |
| `VisionRoi.Rotated(cx, cy, width, height, angle)` | Rotated; `cx`, `cy` = center, `angle` in degrees, **clockwise on screen is +**. Inside the step the image is upright: its x axis runs along the rectangle's width |
| `default(VisionRoi)` | Not set (`Shape == VisionRoiShape.None`, `IsEmpty == true`). A step whose ROI is not set ends in Error |

A `VisionRoi` is a plain value saved as JSON (`Shape`, `X`, `Y`, `Width`, `Height`, `Angle`).

A ROI partly outside the image is allowed while at least half of it is inside; the outside part is filled with the nearest edge pixels. With less than half inside, the step is Error — it would inspect the wrong place.

**Alignment.** Parts do not arrive at the same place. An alignment step finds the part and reports its pose; the ROIs of the following steps are then moved and rotated by the difference between the pose now and the reference pose (the pose on the base image).

```csharp
.Add("align", pose: "body", (ctx, s) =>
{
    var part = Blob.Find(ctx.Image, s.Body).FirstOrDefault();
    if (part is null) { ctx.Fail("part not found"); return; }
    ctx.SetPose(VisionPose.FromBlob(part, s.BodyRef), s.BodyRef);
})
```

- `VisionPose(X, Y, Angle)`: the part's reference point and direction in the original image; `Angle` in degrees, clockwise on screen is +, range (−180, 180].
- `ctx.SetPose(current, reference)`: `reference` is the taught pose from the setup. `reference: null` means *this image is the base image* — ROIs stay where they were drawn. Use it when teaching, then save `r.Poses["body"]` as the reference.
- Which pose a ROI follows: with one alignment step nothing more is written — every later step follows it. With several, name it: `follow: "label"`. `follow: ""` keeps a ROI fixed.
- When the alignment did not set its pose (the part was not found, the step failed or is disabled), the steps that follow it are `Skipped` (`Pose '…' missing (…)`) and the result is `Error`.
- `VisionPose.FromBlob(blob, reference)` uses the blob's center and long axis. A long axis has two directions; the one closer to the reference angle is chosen. Do not use it where the part can turn more than 90°, or for nearly round or square parts — for round parts use `RoundModel` ([vision-toolkit.md#round-model](vision-toolkit.md#round-model)).

Teaching the reference pose:

```csharp
var s = job.Setup!;
s.BodyRef = null;                                   // this image is the base
using var baseImage = VisionImage.Load(basePath);
using var r = await job.RunAsync(baseImage);
if (r.Poses.TryGetValue("body", out var pose))
    s.BodyRef = pose;                               // save the setup (JSON) afterwards — the app's job
```

---

<a id="judging"></a>
## Judging and units

- `ctx.Value` is the normal way: the value is recorded with its limits (shown, logged, in `ToJson`) and judged. Out of range or `NaN` → the step is NG with `code` (`code: null` = the value name).
- Several values in one step give several codes; the step's `Reason` lists them, `r.Codes` holds each once.
- `ctx.Judge(ok, code)` when there is no number.
- `ctx.Fail(reason)` when the step cannot say good or bad (the part is missing, a model is not loaded). The result is `Error`, which machines usually handle differently from a reject.
- An exception thrown in a step makes that step `Error` with the exception message; the following steps still run.

mm instead of px:

```csharp
job.Calibration = VisionCalibration.FromLength(px: 400.0, mm: 100.0);   // a gauge of known length measured in the image
// in a step:
var e = Edge.Caliper(ctx.Image);
ctx.Value("width", ctx.Mm(e), nominal: 100, plus: 1, minus: 1, code: "WIDTH", unit: "mm");
```

- `VisionCalibration.Scale(mmPerPx)` or `FromLength(px, mm)`; both throw `ArgumentOutOfRangeException` for values that are not positive. It is one scale: the camera looks straight at a flat part.
- It belongs to the camera setup, not to the product: keep it apart from the product setup.
- `ctx.Mm` converts the points to the original image first, so crop, rotation and resize do not matter. Without `job.Calibration` it throws and the step is `Error`. `ctx.Mm(caliper)` gives `NaN` when the edges were not found, which `ctx.Value` judges NG.

---

<a id="run-rules"></a>
## How a run ends

`RunAsync` returns only after every step has really finished.

| Situation | Step | Following steps | Result |
|---|---|---|---|
| Step OK / NG | `Ok` / `Ng` (`Reason` = the codes) | run (`AfterNg.Continue`, the default) | NG if any step is NG |
| NG with `job.AfterNg = AfterNg.Stop` | `Ng` | `Skipped`: `Stopped after NG in <step>` | `Ng` |
| Exception or `ctx.Fail` | `Error` (`Reason` = the message) | run | `Error`; `r.Error` = `"job/step: reason"` of the first one |
| Time limit passed (`job.Timeout`, or `timeout:` on the step) | `Error`: `Timeout (limit … ms, took … ms)` | `Skipped`: `Skipped after timeout in <step>` | `Error` |
| Canceled (`ct`) | `Error`: `Canceled` | `Skipped`: `Canceled` | `Error` |
| Disabled (`job.Disable(name)`) | `Skipped`: `Disabled`, listed in `r.Disabled` | run | Not affected — show `r.Disabled` |
| Its pose is missing | `Skipped`: `Pose '…' missing (…)` | run | `Error` unless another step already made it NG or Error |
| No step ran (all disabled) | — | — | `Error` |
| The job is still running | — | — | `Error`, `r.Error == "Busy"`, returned at once |

- A time limit cannot stop an OpenCV call. The run signals `ctx.Cancel`, waits for the step to return, and then reports the timeout. The default is no limit (`null`; `Timeout.InfiniteTimeSpan` means the same, and on a step it overrides the job's limit). A limit of zero, a negative one or one longer than about 49 days is refused with `ArgumentOutOfRangeException` when it is set.
- `job.Disable(name)` is for commissioning. A disabled step does not judge, so a part can pass unchecked.

**Reason steps** (`role: StepRole.Reason`, only on `Add(stepObject, …)`): a reason step does not judge. It runs after all judging steps, by default only when they made the result NG (`job.ReasonSteps = ReasonRun.WhenNg`), and its NG codes are added to that NG result. It never turns OK into NG or NG into Error. `ReasonRun.Always` runs it every time to show its values. Use it when one step (for example a model) decides and others only name the defect.

---

<a id="images"></a>
## Images

`VisionImage` wraps an OpenCvSharp `Mat` (8-bit BGR or 8-bit gray) with capture information (`Info`) and the mapping back to the original image.

| Member | Meaning |
|---|---|
| `VisionImage.Load(path)` | Reads a file. Gray files stay gray, color files become BGR. `FileNotFoundException` when it cannot be read, `NotSupportedException` for other pixel formats |
| `VisionImage.From(mat, info)` | Wraps a `Mat` (pixels shared, not copied; your `Mat` stays yours). `ArgumentException` for an empty `Mat`, `NotSupportedException` for other pixel types |
| `Mat` | The pixels, for any OpenCV call. Do not dispose it |
| `Width`, `Height`, `Size`, `Format` (`VisionPixelFormat.Bgr8` / `Gray8`), `Info` (`VisionImageInfo`: `Time`, `Camera`, `FrameNo`, `Exposure`, `Gain`, `Dropped`) | |
| `Crop(roi)`, `Resize(w, h)`, `Gray()`, `Blur(kernel)`, `Threshold(mode, value, invert)` | Derived images. They remember how their pixels map to the original image |
| `ToSource(point)` | A point of this image → the original image |
| `ToTensor(width, height, format)` | A model input: resized, channel order and normalization applied, channels-first `float[]` |
| `Keep()` | Takes an image created in a step out of the run; the app then disposes it |
| `Clone()` | A copy of the pixels owned by the caller |
| `Save(path)` | Writes a file; the format follows the extension. `IOException` when it cannot be written |

Who disposes what:

| Image | Owner |
|---|---|
| From `Load`, `From`, a camera, `Clone`, `Keep` | The app (`using`) |
| Created inside a step (`ctx.Image`, `ctx.Crop`, `ctx.From`, `Gray()` …) | The run: released when it ends |
| Derived outside a run (`img.Gray()` in ordinary code) | The caller (`using`) |
| `r.Image` | Borrowed from the app's frame |
| `r.Steps[name].Image` (the last image the step created) | The result |
| `VisionModel.Image` | The model |

Going down to OpenCV inside a step, and coming back:

```csharp
.Add("edges", roi: () => roi, ctx =>
{
    using var edges = new Mat();                              // your Mat: you dispose it
    Cv2.Canny(ctx.Image.Mat, edges, 50, 150);
    var img = ctx.From(edges);                                // same coordinates as ctx.Image; released with the run
    ctx.Value("edge pixels", Cv2.CountNonZero(edges), max: 500, code: "EDGE");
    ctx.DrawBox(new Rect(10, 10, 20, 20), "here", from: img); // drawn at the right place on the original image
})
```

---

<a id="setup"></a>
## Setup classes, changing values, teaching

A setup class is the app's own class with the ROIs, parameters, limits and taught references of one product. Save and load it as JSON with `System.Text.Json` — where and when is the app's job. `VisionRoi`, `VisionPose`, `BlobParams`, `VisionCalibration` and `RoundModel` are saved as plain JSON; `VisionModel`, `DiffModel` and `AnomalyModel` save only their `Path` and are read from their files with `Load(root)`.

**Changing values while the line runs.** A run uses the setup object it took when it started. Never change the properties of the setup in use; edit a copy and swap it in:

```csharp
var edit = VisionSetup.Copy(job.Setup!);     // JSON round trip; reference images and models are shared, not copied
edit.Hole.Threshold = 90;
job.Setup = edit;                            // the next run uses the new values
```

**Reference images.** `VisionModel` is a reference image kept in a setup:

```csharp
public sealed class LabelSetup { public VisionModel Logo { get; set; } = new(); public VisionRoi LogoRoi { get; set; } }

// Teaching: cut the region out of the base image, save it under the recipe folder, put it in a setup copy
var edit = VisionSetup.Copy(job.Setup!);
edit.Logo = VisionModel.Teach(baseImage, edit.LogoRoi, recipeFolder, "models/logo.png");
job.Setup = edit;

// Startup: after reading the setup JSON, read the image file
setup.Logo.Load(recipeFolder);               // setup.Logo.Image is now set; do not dispose it
```

- `VisionModel.Load(root)` throws `InvalidOperationException` when `Path` is not set and `FileNotFoundException` when the file cannot be read.
- After a product change the old model is not disposed by the app: a running inspection may still use it.
