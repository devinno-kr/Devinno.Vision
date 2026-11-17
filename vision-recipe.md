# Recipes: running an inspection made in Studio

Covers: loading a recipe (`VisionRecipe`), making and running its job, the result (`VisionResult`), the names a recipe job produces, the recipe file (`studio.json`), packages (`.dvpkg`), the good-part model and reason checks.

Read [llm-rules.md](llm-rules.md) first. Writing steps in C# instead: [vision-jobs.md](vision-jobs.md).

A recipe is made in Devinno.Vision.Studio, the teaching tool: the engineer opens a base image, adds checks, draws their ROIs, teaches the part position, tests on labelled image folders and saves. The app then loads the recipe and runs the same inspection without Studio.

---

<a id="run-a-recipe"></a>
## Run a recipe

```csharp
using Devinno.Vision;

using var recipe = VisionRecipe.Load(@"C:\recipes\plate");   // the folder that holds studio.json, or a .dvpkg file
var job = recipe.CreateJob();

using var img = VisionImage.Load(path);
using var r = await job.RunAsync(img);                           // the same judgment as in Studio
```

| Call | What it does | Throws |
|---|---|---|
| `VisionRecipe.Load(folder)` | Reads `studio.json`, restores calibration, checks and the taught reference pose, and loads the model files. A path ending in `.dvpkg` is opened as a package ([#package](#package)) | `FileNotFoundException`: no `studio.json`, no such package, or a model folder is missing. `InvalidDataException`: wrong content, a newer format version, an unknown check kind, an unreadable model, a package that does not match its manifest |
| `VisionRecipe.Load(folder, loadModels: false)` | The same without reading model files (for editing tools). `CreateJob` then reads them | as above, without the model errors |
| `recipe.CreateJob()` | Makes a `VisionJob` named after the recipe (`recipe.Name`; `"studio"` when the name is empty), with the calibration set | `InvalidOperationException`: nothing to run, a check that cannot run, or the good-part model must be trained again. `FileNotFoundException` / `InvalidDataException`: the good-part model is missing or unreadable. `ObjectDisposedException`: the recipe was disposed |
| `job.RunAsync(image, ct)` | Runs the checks on one image | Only `ArgumentNullException` for a `null` image. Inspection problems are in the result |
| `recipe.LoadBaseImage()` | Reads the base image the recipe was taught on (for display; running does not need it). The caller disposes it | `InvalidOperationException`: no base image set. `FileNotFoundException`: the file is missing |
| `recipe.Dispose()` | Releases the loaded models. A run in progress finishes first; later runs fail at the model step | — |

Rules:

- Load once, make the job once, run it for every image. Loading reads ONNX models and can take a noticeable time.
- The job is a snapshot: it copies the check values when it is made. After changing `recipe.Checks` in memory, call `CreateJob()` again.
- Keep the recipe alive as long as its jobs are used. A recipe without model checks holds nothing, but dispose it all the same.
- The order of the steps is fixed by the recipe: the alignment check (when the first check is one), then the good-part model (when the recipe has one), then the other checks in list order.
- The job is an ordinary `VisionJob`: `job.Timeout`, `job.AfterNg`, `job.ReasonSteps`, `job.Disable(name)` work as described in [vision-jobs.md#run-rules](vision-jobs.md#run-rules).
- Steps added to it in code (`job.Add(...)`) run after the recipe's checks, and their ROIs follow the recipe's pose. Studio does not know them, so its test results do not include them. Keep what the engineer tunes in the recipe, and use code steps only for what the checks cannot do.

---

<a id="result"></a>
## The result

Every run returns one `VisionResult`, whatever happens. Receive it with `using`; it borrows the image given to the run, so dispose it before that image.

| Member | Meaning |
|---|---|
| `State` | `VisionState.Ok` (inspected, good), `Ng` (inspected, defective), `Error` (could not inspect) |
| `Ok` | `State == VisionState.Ok` |
| `Codes` | NG codes, no duplicates, in the order they occurred. Empty unless `Ng` |
| `Error` | Why the run is `Error`, as `"job/step: reason"` (or `"Busy"`); `null` otherwise |
| `Values` | Every measured value: `VisionValue` with `Name`, `Value`, `Min`, `Max`, `Unit`, `Code`, `Ok`, `Step`, `Nominal`, `Deviation` |
| `Steps` | `StepResult` per step in order, also by name: `r.Steps["길이"]` (throws `KeyNotFoundException` for an unknown name). `State` (`Ok`, `Ng`, `Error`, `Skipped`), `Reason`, `Elapsed`, `Followed`, `Image` |
| `Disabled` | Names of disabled steps. They do not affect the judgment — show this |
| `Poses` | Poses set in this run by name; a recipe job sets `r.Poses[VisionRecipe.PoseName]` (`"part"`) when it has an alignment check |
| `Overlays` | Shapes to draw, in the coordinates of the inspected image: `VisionOverlay` with `Kind` (`Roi`, `Box`, `Contour`, `Line`, `Point`, `Text`), `Points`, `Label`, `Step` |
| `Image` | The inspected image (borrowed; do not dispose it). Throws `ObjectDisposedException` after the result is disposed |
| `Elapsed` | Total run time |
| `Job` | The job name |
| `ToJson(indented)` | Everything above except the image, as JSON text — the record format. The app decides where to store it. A number that is not finite (`NaN` when nothing was measured) is written as `null` |
| `VisionResult.Worst(r1, r2, …)` | The worst state of several results (`Error` > `Ng` > `Ok`); `Error` when none is given |

How the judgment is formed:

- Any judging step `Error`, a timeout or a cancel → `Error` (even if another step is NG).
- Otherwise any judging step NG → `Ng`, and `Codes` holds the NG codes.
- Otherwise, a step skipped because its pose is missing, or no step ran at all → `Error`. An inspection that did not inspect never comes out OK.
- Otherwise `Ok`.

A value outside its limits (or `NaN`) makes its step NG with the value's `Code`. `VisionValue.Ok` tells which value failed.

```csharp
if (r.State == VisionState.Ng)
    foreach (var v in r.Values.Where(v => !v.Ok))
        Console.WriteLine($"{v.Step}: {v.Name} = {v.Value:0.##} (limits {v.Min}–{v.Max}) → {v.Code}");
```

<a id="draw-overlays"></a>
### Drawing the overlays yourself

`DvVisionView` (package Devinno.Vision.UI) shows a result on a Devinno.UI screen. Without it, for example to save an NG picture or to check a result while developing, draw the overlays with OpenCV. The points are already in the coordinates of `r.Image`.

```csharp
using Devinno.Vision;
using OpenCvSharp;

static void SaveResultPicture(VisionResult r, string file)
{
    using var canvas = new Mat();
    if (r.Image.Format == VisionPixelFormat.Gray8)
        Cv2.CvtColor(r.Image.Mat, canvas, ColorConversionCodes.GRAY2BGR);
    else
        r.Image.Mat.CopyTo(canvas);

    foreach (var o in r.Overlays)
    {
        var color = r.Steps[o.Step].State switch
        {
            StepState.Ok => new Scalar(0, 200, 0),
            StepState.Ng => new Scalar(0, 0, 255),
            _ => new Scalar(0, 165, 255),
        };
        var pts = o.Points.Select(p => new Point((int)Math.Round(p.X), (int)Math.Round(p.Y))).ToArray();
        switch (o.Kind)
        {
            case VisionOverlayKind.Roi:
            case VisionOverlayKind.Box:
            case VisionOverlayKind.Contour:
                Cv2.Polylines(canvas, new[] { pts }, isClosed: true, color, thickness: 2);
                break;
            case VisionOverlayKind.Line:
                Cv2.Line(canvas, pts[0], pts[1], color, thickness: 2);
                break;
            case VisionOverlayKind.Point:
                Cv2.Circle(canvas, pts[0], 4, color, thickness: -1);
                break;
        }
        if (o.Label is { } label)
            Cv2.PutText(canvas, label, pts[0], HersheyFonts.HersheySimplex, 0.5, color);
    }
    Cv2.ImWrite(file, canvas);
}
```

`Cv2.PutText` draws ASCII only; a label with other characters (for example a Korean check name on a ROI) comes out as question marks. The shapes are still right.

---

<a id="recipe-names"></a>
## Names a recipe job produces

The step names are the check names the engineer typed in Studio. The value names and some texts are built from them by the library. **These are the result format; several are Korean literals.** Branch on NG codes, and use the constants below instead of typing the texts.

| Check kind (`Kind`) | Step name | Value it records | Judged | NG code (default) |
|---|---|---|---|---|
| `BlobAlign` | check name | none (sets the pose `VisionRecipe.PoseName` = `"part"`, draws the part) | `Error` with reason `부품 없음` when no part blob is found | — |
| `BlobCount` | check name | `"<check name> 개수"` — number of blobs | `Min` ≤ count ≤ `Max` | `Code` (`COUNT`) |
| `PixelCount` | check name | `"<check name> 픽셀"` — pixels with brightness `Low`–`High` | count ≤ `Max` | `Code` (`PIXEL`) |
| `Brightness` | check name | `"<check name> 밝기"` — mean brightness (0–255) | `Min` ≤ mean ≤ `Max` | `Code` (`BRIGHT`) |
| `Length` | check name | `"<check name>"` — distance between two edges; unit `mm` when the recipe has a calibration, `px` otherwise | `Nominal − Minus` ≤ value ≤ `Nominal + Plus` (`NaN` when the edges are not found → NG) | `Code` (`LENGTH`) |
| `Onnx` | check name | `"<check name> 불량 확률"` — probability that the part is not good (0–1) | value ≤ `Threshold` | the most likely defect label in capitals (`scratch` → `SCRATCH`) |
| good-part model | `VisionRecipe.GoodModelStepName` = `"양품 모델"` | `"양품 모델 점수"` — anomaly score | score ≤ `GoodModel.Max` | `GoodModel.Code` (`SURFACE`) |

- `r.Error` for a missing part in a recipe named `플레이트` with an alignment check named `정렬` is `"플레이트/정렬: 부품 없음"`. The checks after it are `Skipped` (their pose is missing).
- The exception messages of `VisionRecipe` (`Load`, `CreateJob`, `Pack`, …) are Korean sentences. Show them to the user as they are; decide in code by the exception type.
- To find a check's values without knowing the text: `r.Values.Where(v => v.Step == check.Name)`, or by code `r.Values.Where(v => v.Code == "HOLE")`.

---

<a id="recipe-file"></a>
## The recipe file

A recipe folder (a Studio project folder):

```
plate/
├─ studio.json              the recipe (this section)
├─ images/base.png          the base image the ROIs were drawn on
└─ models/
   ├─ good-20261111-103000/ good-part model (model.json, features.onnx, bank.bin)
   └─ defect-…/             defect model of an ONNX check (model.onnx, model.json)
```

Other files Studio keeps in the folder (`goodset.json`, `train.json` in a model folder, training folders) are Studio's own; the library does not read them when running.

`studio.json` is plain JSON, read and written with `VisionRecipe.JsonOptions` (indented, enums as names). The property names and `Kind` values are the file format and do not change. Make and edit recipes in Studio; the structure is listed here so that a file can be read and checked.

```json
{
  "Name": "plate",
  "FormatVersion": 1,
  "BaseImage": "images/base.png",
  "Calibration": { "MmPerPx": 0.25 },
  "Checks": [
    { "Kind": "BlobAlign", "Id": "bdba9c42", "Name": "align",
      "Blob": { "Mode": "Otsu", "Threshold": 128, "Invert": false, "MinArea": 20000, "MaxArea": 2147483647 },
      "Reference": { "X": 319.07, "Y": 240.66, "Angle": -2.3 },
      "Roi": { "Shape": "None", "X": 0, "Y": 0, "Width": 0, "Height": 0, "Angle": 0 } },
    { "Kind": "BlobCount", "Id": "772b0558", "Name": "left hole",
      "Blob": { "Mode": "Fixed", "Threshold": 80, "Invert": true, "MinArea": 300, "MaxArea": 2147483647 },
      "Min": 1, "Max": 1, "Code": "HOLE",
      "Roi": { "Shape": "Rect", "X": 138, "Y": 130, "Width": 60, "Height": 60, "Angle": 0 } },
    { "Kind": "Length", "Id": "4fb79f9b", "Name": "length",
      "Polarity": "DarkObject", "MinContrast": 8, "Nominal": 84, "Plus": 1, "Minus": 1, "Code": "LENGTH",
      "Roi": { "Shape": "Rect", "X": 135, "Y": 312, "Width": 370, "Height": 16, "Angle": 0 } }
  ],
  "TestSets": []
}
```

Top level (`VisionRecipe`):

| Property | Type | Meaning |
|---|---|---|
| `Name` | string | The recipe name; becomes the job name |
| `FormatVersion` | int | 1, or 2 when the recipe uses a good-part model, a reason check or an ONNX check. `VisionRecipe.CurrentFormatVersion` (2) is the newest this library reads; a newer file is refused |
| `BaseImage` | string or null | Base image path relative to the folder |
| `Calibration` | `{ "MmPerPx": number }` or null | With it, `Length` checks are judged in mm; without it in px |
| `GoodModel` | object, left out when there is none | [#good-model](#good-model) |
| `Checks` | array | The checks, in running order |
| `TestSets` | array of `{ "Folder", "Rule": "Folder"\|"FileName", "GoodLabel" }` | Studio's labelled test folders. Not used when a job runs |

Every check (`CheckDef`, namespace `Devinno.Vision.Recipes`):

| Property | Meaning |
|---|---|
| `Kind` | The check kind — one of `CheckDef.Kinds` |
| `Id` | Unique in the recipe (8 hex characters by default) |
| `Name` | Unique in the recipe; the step name |
| `Roi` | `{ "Shape": "None"\|"Rect"\|"Rotated", "X", "Y", "Width", "Height", "Angle" }` in base image coordinates. `Rect`: `X`, `Y` = top-left. `Rotated`: `X`, `Y` = center, `Angle` in degrees, clockwise on screen is +. `None` = the check looks at the whole image (usual for the alignment check; other checks normally have a ROI) |
| `Role` | Left out for the default (`Judge`); `"Reason"` = the check only names the defect ([#good-model](#good-model)) |

Per kind (defaults in brackets):

| `Kind` | Class | Properties |
|---|---|---|
| `BlobAlign` | `BlobAlignCheck` | `Blob` (`BlobParams`: `Mode` [`Otsu`] \| `Fixed`, `Threshold` [128], `Invert` [false], `MinArea` [50], `MaxArea`), `Reference` (`{ "X", "Y", "Angle" }` — the part position taught on the base image; without it the ROIs are not moved) |
| `BlobCount` | `BlobCountCheck` | `Blob`, `Min` [0], `Max` [1], `Code` [`COUNT`] |
| `PixelCount` | `PixelCountCheck` | `Low` [0], `High` [100], `Max` [0], `Code` [`PIXEL`] |
| `Brightness` | `BrightnessCheck` | `Min` [0], `Max` [255], `Code` [`BRIGHT`] |
| `Length` | `LengthCheck` | `Polarity` [`BrightObject`] \| `DarkObject` \| `Both`, `MinContrast` [8], `Nominal`, `Plus`, `Minus`, `Code` [`LENGTH`] |
| `Onnx` | `OnnxCheck` | `Model` (model folder relative to the recipe folder; empty before training), `Threshold` [0.5] |

Rules the loader enforces (`Load` → `InvalidDataException`, `CreateJob` and `Pack` → `InvalidOperationException`):

- Check names are unique and not empty; ids are unique and not empty.
- Only the first check may be an alignment check, and it always judges.
- `Code` is present on the checks that have one; `Onnx.Threshold` is 0–1.
- With a good-part model, no check may be named `"양품 모델"`.
- `CreateJob` needs at least one check or a good-part model. A recipe with nothing to run would pass everything, so no job is made.

Reading the checks in code:

```csharp
using Devinno.Vision;
using Devinno.Vision.Recipes;

using var recipe = VisionRecipe.Load(folder, loadModels: false);
foreach (var c in recipe.Checks)
    Console.WriteLine($"{c.Name} ({c.Kind}, {c.Role}) roi {c.Roi.Shape} {c.Roi.Width}×{c.Roi.Height}");
if (recipe.Checks.OfType<LengthCheck>().FirstOrDefault() is { } len)
    Console.WriteLine($"{len.Name}: {len.Nominal} +{len.Plus} / -{len.Minus}");
```

`VisionRecipe.Create(folder, name)` makes an empty recipe and `recipe.Save()` writes `studio.json` (and removes `models/good-…` and `models/defect-…` folders the recipe no longer uses). These are what Studio calls; an app normally only loads.

---

<a id="package"></a>
## Packages (.dvpkg)

A package is the recipe as one file for deployment: a zip with `manifest.json` (library version, time, SHA-256 and size of every file), `studio.json` without the test folders, the base image and the model folders the recipe uses. It holds data and model files only — no code.

```csharp
using var recipe = VisionRecipe.Load(@"C:\recipes\plate");
int files = recipe.Pack(@"C:\deploy\plate.dvpkg");             // overwrites; returns the number of files packed (manifest not counted)

using var onSite = VisionRecipe.Load(@"C:\deploy\plate.dvpkg"); // unpacked and checked, then loaded like a folder
var job = onSite.CreateJob();
```

- `Pack` throws `ArgumentException` when the file name does not end in `.dvpkg`, `InvalidOperationException` when the recipe cannot run as it is (no checks, a good-part model to train again, an ONNX check without a model), `FileNotFoundException` when a model folder is missing.
- `Load` of a package unpacks it under the user's local application data folder (`Devinno.Vision\packages\<hash of the file>`) and checks every file against the manifest. A package that was changed after packing is refused with `InvalidDataException`. An unchanged package is unpacked only once.
- After loading a package, `recipe.PackagePath` is the package file and `recipe.Folder` is the unpacked folder. Do not write into that folder.
- Constants: `VisionRecipe.PackageExtension` (`".dvpkg"`), `VisionRecipe.ManifestFile` (`"manifest.json"`), `VisionRecipe.FileName` (`"studio.json"`).

---

<a id="good-model"></a>
## Good-part model and reason checks

A recipe's reference can be a base image with standard checks, a good-part model, or both.

- The **good-part model** (`recipe.GoodModel`, class `GoodModel`) is an `AnomalyModel` learned from good images only. Its step cuts the region `GoodModel.Roi` (following the part), scores it and judges `score ≤ GoodModel.Max`. NG code: `GoodModel.Code`.
- A **reason check** (`"Role": "Reason"`, `StepRole.Reason`) does not judge. It runs after the judging steps, by default only when they made the result NG, and adds its NG code to that result — the model says *that* the part is bad, the standard check says *why*. It never turns an OK into NG or an NG into Error.
- `job.ReasonSteps = ReasonRun.Always` runs the reason checks on every part to show their values (tuning screens). The judgment is still not changed, and their codes are added only to an NG result. Default `ReasonRun.WhenNg`: good parts do not pay for them.
- A reason step that did not run is `Skipped` with the reason `Not needed (judged OK)` or `Not run (judgment is Error)`.

`GoodModel` properties: `Path` (model folder relative to the recipe folder), `Roi`, `Max`, `Code` [`SURFACE`], `Basis` (what it was trained for: the alignment settings, the reference pose and the region). When the alignment check, its reference or the region changes afterwards, `recipe.GoodModelIsCurrent` becomes `false` and `CreateJob` throws `InvalidOperationException` asking to train again.

Training is normally done in Studio. From code:

```csharp
using Devinno.Vision;
using Devinno.Vision.Recipes;

using var recipe = VisionRecipe.Load(folder, loadModels: false);
var result = await recipe.TrainGoodModelAsync(new GoodModelTraining
{
    Images = Directory.GetFiles(@"C:\images\good", "*.png"),   // good images only; 3 at least, 100 or more recommended
    Features = @"C:\models\features.onnx",                      // the feature model (feature maps [1, C, h, w])
    Roi = VisionRoi.Rect(100, 150, 470, 250),                   // the part, in base image coordinates; empty = whole image
});
recipe.Save();                                                   // keeps the new model, removes the old model folder
Console.WriteLine($"learned {result.Learned}, limit {result.Model.Max}, {result.MsPerImage:0} ms per image, skipped {result.Skipped.Count}");
```

- Each image is cut the way a job cuts it (alignment, then the region). Most images are learned; the share `CalibrationShare` (default 0.1) is kept out and sets the limit: the highest of their scores × `Margin` (default 1.05).
- Throws `ArgumentException` for fewer than 3 usable images, `FileNotFoundException` when the feature model does not exist, `OperationCanceledException` when canceled (the recipe is then unchanged).
- Learn the part, not the whole frame: with a region that covers background the model misses defects on the part.
- Other `GoodModelTraining` settings: `InputSize` [256], `Representatives` [48], `Neighborhood` [1], `Code` [`SURFACE`]. Progress arrives as `GoodModelProgress(Stage, Done, Total)` with `Stage` = `"crop"`, `"learn"` or `"limit"`.
- `recipe.CropAsync(image, roi)` cuts one image the same way (for preparing training images); the caller disposes the returned image.

An ONNX check (`OnnxCheck`) runs a defect classifier (`DefectModel`, [vision-toolkit.md#defect-model](vision-toolkit.md#defect-model)) on its ROI. Studio trains the classifier or imports an ONNX file; the library only runs it.
