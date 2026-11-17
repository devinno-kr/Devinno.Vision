# Toolkit, ONNX models and cameras

Covers: `Blob`, `Brightness`, `Edge.Caliper`, `RoundModel`, `DiffModel`, `OnnxModel`, `AnomalyModel`, `DefectModel`, and the cameras (`IVisionCamera`, `FileCamera`, `RtspCamera`).

Read [llm-rules.md](llm-rules.md) first. The tools are helpers to pick from: each takes a `VisionImage` and returns a result. They work inside a step (`ctx.Image`) and in ordinary code. Results found inside a step are drawn with `ctx.Draw(...)`, which converts their coordinates to the original image. Anything they do not cover is done with OpenCV on `image.Mat` ([vision-jobs.md#images](vision-jobs.md#images)).

Every tool converts a color image to gray itself. All coordinates are in the image the tool ran on.

---

<a id="blob"></a>
## Blob — regions by brightness

```csharp
Blob[] blobs = Blob.Find(ctx.Image, ThresholdMode.Fixed, threshold: 80, invert: true, minArea: 300);
Blob[] same  = Blob.Find(ctx.Image, s.Hole);            // s.Hole is a BlobParams kept in the setup
ctx.Draw(blobs);
ctx.Value("holes", blobs.Length, min: 4, max: 4, code: "HOLE");
```

- `Blob.Find(image, mode, threshold, invert, minArea, maxArea)` and `Blob.Find(image, BlobParams)` return the blobs **largest first**; an empty array when none.
- `ThresholdMode.Otsu` (default) picks the threshold automatically — good when the image clearly has object and background. When the object may be missing, Otsu splits whatever is there (surface texture) and reports false blobs: use `ThresholdMode.Fixed` with a `threshold` (0–255) for presence checks.
- `invert: true` finds dark objects (pixels below the threshold); the default finds bright ones.
- `minArea` (default 50 px²) and `maxArea` filter by area.
- `BlobParams` (`Mode`, `Threshold`, `Invert`, `MinArea`, `MaxArea`) is the same set as one object, saved as plain JSON in a setup.

| `Blob` member | Meaning |
|---|---|
| `Area` | px², inside the outline |
| `Center` | Center of mass (`Point2d`) |
| `Bounds` | Axis-aligned bounding box (`Rect`) |
| `Contour` | Outer outline points |
| `Angle` | Direction of the long axis in degrees, clockwise on screen is +, range (−90, 90] |
| `Length`, `Width` | Along and across the long axis of the smallest rotated box around the blob (px) |

For the part position use `VisionPose.FromBlob(blob, reference)` ([vision-jobs.md#roi-and-pose](vision-jobs.md#roi-and-pose)).

<a id="brightness"></a>
## Brightness

| Call | Returns | Typical use |
|---|---|---|
| `Brightness.Mean(image)` | Mean brightness, 0–255 | Presence, material, lighting check |
| `Brightness.StdDev(image)` | Standard deviation of brightness | How uneven a surface is: scratches, stains, texture |
| `Brightness.Count(image, min, max)` | Number of pixels with brightness in [min, max] | Dark or bright spots: discoloration, missing coating |

```csharp
ctx.Value("dark pixels", Brightness.Count(ctx.Image, 0, 100), max: 200, code: "NOTCH");
```

<a id="caliper"></a>
## Edge.Caliper — width, gap, length

```csharp
var e = Edge.Caliper(ctx.Image, EdgePolarity.BrightObject, minContrast: 8);
ctx.Draw(e);
ctx.Value("width", e.Distance, min: 395, max: 405, code: "WIDTH", unit: "px");
```

- Finds the outermost pair of edges **along the image's x axis** and measures the distance between them to sub-pixel. The brightness is averaged over all rows first.
- So the ROI goes across the feature: its **width along the direction to measure**, its height along the edges. For a vertical measurement use a rotated ROI (`VisionRoi.Rotated(cx, cy, length, thickness, 90)`); a ROI that follows the part measures along the part's axis.
- `polarity`: `EdgePolarity.BrightObject` (a bright object on a dark background), `DarkObject`, or `Both` (default: any change).
- `minContrast` (default 20): the weakest edge accepted, in brightness change per px after averaging. Lower it for soft edges, raise it to ignore texture.

| `CaliperResult` member | Meaning |
|---|---|
| `Found` | Whether both edges were found |
| `P1`, `P2` | The left and right edge on the image's middle row (sub-pixel) |
| `Distance` | `P2.X − P1.X` in px; `NaN` when not found — `ctx.Value` judges `NaN` as NG |
| `Contrast1`, `Contrast2` | Edge strength at `P1`, `P2` |

mm: `ctx.Mm(e)` with `job.Calibration` set ([vision-jobs.md#judging](vision-jobs.md#judging)). `ctx.Draw(e)` labels the line in mm when the job has a calibration, in px otherwise.

<a id="round-model"></a>
## RoundModel — alignment of round parts

For parts that can lie at any angle and have no long axis (nuts, washers, caps, gears). The brightness in a ring around the center is unrolled into a signature per angle (0.5° steps) and matched against the signature of a good part.

```csharp
// Teaching (once, on the base image): the ring must cover something that varies with the angle (lobes, notches)
var model = RoundModel.Train(baseImage, center: new Point2d(350, 350), innerRadius: 120, outerRadius: 200);
model.Save(Path.Combine(recipeFolder, "round.json"));

// Run: find the center first (for example the hole as a dark blob), then the rotation
.Add("align", pose: "nut", ctx =>
{
    var hole = Blob.Find(ctx.Image, ThresholdMode.Fixed, 60, invert: true, minArea: 2000).FirstOrDefault();
    if (hole is null) { ctx.Fail("part not found"); return; }
    var match = model.Find(ctx.Image, hole.Center);
    ctx.Value("match", match.Score, min: 0.5, code: "ALIGN");
    ctx.Judge(!match.Mirrored, "FLIPPED");
    ctx.SetPose(match.Pose, model.ReferencePose);
})
```

- `RoundModel.Train(image, center, innerRadius, outerRadius)`; `ArgumentException` unless 0 ≤ inner < outer. `RoundModel.Load(path)` / `model.Save(path)` (JSON; it can also be a setup property).
- `model.Find(image, center)` returns `RoundMatch`: `Pose` (center and rotation in the original image), `Score` (−1…1; a good part is close to 1), `MirrorScore`, `Mirrored` (the part lies upside down).
- `model.ReferencePose` is the reference to pass to `ctx.SetPose`.

<a id="diff-model"></a>
## DiffModel — difference against good parts

Learns the mean and spread of every pixel from **aligned good images of the same size**, then reports the regions of an image that differ more than good parts do (stains, missing material, foreign objects).

```csharp
// Teaching: the same ROI crop of every good part (inside a step: ctx.Image.Clone())
var model = DiffModel.Train(goodCrops, color: false);
model.Save(recipeFolder, "models/diff");               // model.Path is now "models/diff"

// Run, in a step whose ROI is the one used for training
var d = s.Diff.Compare(ctx.Image, k: 3.5, minSpread: 6, minArea: 10);
ctx.Draw(d.Regions);
ctx.Value("largest difference", d.MaxArea, max: 30, code: "SURFACE", unit: "px²");
```

- `DiffModel.Train(goods, color)`: at least 2 images (20 or more is better), all the same size; `color: true` also checks saturation (BGR images). `ArgumentException` otherwise.
- `Compare(image, k, minSpread, minArea)`: a pixel differs when `|value − mean| > k · (spread + minSpread)`. Lower `k` = more sensitive. `InvalidOperationException` when not trained or loaded, `ArgumentException` when the image size differs from the model.
- `DiffResult`: `Regions` (`Blob[]`, largest first), `MaxArea`, `TotalArea`.
- In a setup only `Path` is saved; after reading the setup call `model.Load(root)`. `IsReady` tells whether it has data.
- The images must be well aligned: the difference is per pixel. For surfaces where alignment to the pixel is not possible, use `AnomalyModel`.

---

<a id="onnx"></a>
## OnnxModel — running an ONNX model

```csharp
using var model = OnnxModel.Load(@"C:\models\classifier.onnx", inputWidth: 224, inputHeight: 224, VisionTensorFormat.ImageNet);
OnnxOutputs o = model.Run(image);          // the image is resized and converted to the model input
OnnxTensor first = o.Ordered[0];           // or o["output name"]; model.Outputs lists the names
float[] values = first.Data;               // row-major
int[] shape = first.Shape;                 // for example [1, 5]
```

- Runs on CPU with ONNX Runtime. The model must take **one image input of shape `[1, C, H, W]` (float)**.
- `OnnxModel.Load(path, inputWidth, inputHeight, format, threads)`: `FileNotFoundException` when the file does not exist. `threads: 0` (default) uses all cores.
- `format` is a `VisionTensorFormat` and **must match the training**: `VisionTensorFormat.ImageNet` (RGB, 0…1, ImageNet mean/std — torchvision and most pretrained CNNs; the default), `VisionTensorFormat.Unit` (RGB, 0…1), `VisionTensorFormat.Gray` (one channel, 0…1), or your own `new VisionTensorFormat(mean, std, rgb, scale)`. A wrong format gives no error, only worse results.
- `Run` may be called from several threads at once. Dispose the model when done.
- `image.ToTensor(width, height, format)` gives the same input array (`float[]`, channels-first) for your own ONNX Runtime code.

<a id="anomaly-model"></a>
## AnomalyModel — anomalies learned from good parts only

No defect images are needed. A pretrained CNN describes each small patch; the patches of good parts are kept per position; a patch far from every good patch at its place is an anomaly (scratches, stains, bends, missing parts).

```csharp
// Teaching
var features = OnnxModel.Load(@"C:\models\features.onnx", 256, 256);     // feature maps [1, C, h, w]
using var model = AnomalyModel.Train(features, goodCrops);               // aligned crops of the same view; the model keeps using `features`
model.Save(recipeFolder, "models/surface");                              // model.json, features.onnx, bank.bin

// Startup
var loaded = new AnomalyModel { Path = "models/surface" };
loaded.Load(recipeFolder);

// Run, in a step whose ROI is the view used for training
var a = loaded.Score(ctx.Image);
ctx.Draw(a);
ctx.Value("anomaly", a.Score, max: 2.3, code: "SURFACE");
```

- The **feature model** is an ONNX model whose outputs are feature maps `[1, C, h, w]`, for example the layer2 and layer3 outputs of an ImageNet ResNet18. The library does not include one; Devinno.Vision.Studio uses its own (`models/features.onnx` next to its executable).
- `AnomalyModel.Train(features, goods, representatives, neighborhood, progress, ct)`: `ArgumentException` for fewer than 2 images (use 20 or more; more good images give fewer false NG). Do not dispose `features` while the model is in use — disposing the model disposes it.
- `Score(image)` returns `AnomalyResult`: `Score` (distance of the most unusual patch from the good ones), `Region` (that patch, in the scored image), `Map` (score per patch, row by row), `GridWidth`, `GridHeight`. `InvalidOperationException` when not trained or loaded.
- **Set the limit from good images that were not used for training**: score them and put the limit a little above the highest score. The score has no fixed scale.
- Give aligned images of the same view — normally the step's ROI, which already follows the part.
- In a recipe, the good-part model does all of this for you ([vision-recipe.md#good-model](vision-recipe.md#good-model)).

<a id="defect-model"></a>
## DefectModel — defect classifier

An ONNX classifier that sorts an image into good and defect kinds. It needs real, labelled defect images for training (Studio trains it, or an ONNX file is imported). Use it to name the defect, or to judge when the defect kinds are known in advance.

```csharp
using var model = DefectModel.Load(@"C:\recipes\nut\models\defect-1");   // the folder with model.onnx and model.json
DefectResult d = model.Classify(ctx.Image);
ctx.Value("defect probability", d.DefectProbability, max: 0.5, code: d.Defect.ToUpperInvariant());
```

The model folder:

- `model.onnx`: input `[1, 3, H, W]`, output `[1, N]` scores, one per label.
- `model.json`: `{ "Labels": ["good", "scratch", …], "GoodLabel": "good", "InputWidth": 224, "InputHeight": 224, "Normalize": "ImageNet" }`. `Normalize` is `ImageNet` (default), `Unit` or `Gray`. `Labels` needs at least two different names and must contain `GoodLabel` (default `"good"`).

| Member | Meaning |
|---|---|
| `DefectModel.Load(folder)` | `FileNotFoundException`: `model.onnx` or `model.json` is missing. `InvalidDataException`: `model.json` is wrong |
| `Classify(image)` | `DefectResult`. `InvalidDataException` when the model output does not have one score per label |
| `Labels`, `GoodLabel`, `InputWidth`, `InputHeight`, `Folder` | From `model.json` |
| `DefectResult.Label`, `Probability` | The most likely label and its probability (0–1) |
| `DefectResult.Defect` | The most likely **defect** label (never the good label) |
| `DefectResult.DefectProbability` | 1 − probability of the good label |
| `DefectResult.Probabilities` | Probability per label |

---

<a id="cameras"></a>
## Cameras

A camera driver implements `IVisionCamera` (`IAsyncDisposable`). Every frame it returns is a `VisionImage` owned by the caller (`using`).

| Member | Meaning |
|---|---|
| `Grab(ct)` | Software trigger: one fresh frame, taken after the call |
| `WaitFrame(ct)` | Hardware or free mode: waits for the next frame |
| `Trigger` | `CameraTrigger.Software`, `Hardware`, `Free`. Setting a mode the device cannot do throws `NotSupportedException` |
| `ExposureUs`, `Gain` | `double.NaN` when unknown; setting throws `NotSupportedException` when the device cannot change it |

| Driver | Package | Opens with | Trigger modes | Exposure / gain |
|---|---|---|---|---|
| `FileCamera` | Devinno.Vision | `new FileCamera(folder, loop: true)` | every mode returns the next file | no |
| `RtspCamera` | Devinno.Vision | `await RtspCamera.OpenAsync(url, timeout, ct)` | `Software`, `Free` | no |
| `FlashCapCamera` | Devinno.Vision.FlashCap | `await FlashCapCamera.OpenAsync(device, width, height, gray, ct)` | `Software`, `Free` | no |

<a id="file-camera"></a>
### FileCamera

Returns the image files of a folder (`.png`, `.bmp`, `.jpg`, `.jpeg`, `.tif`, `.tiff`) in name order — for replaying saved images and for tests, through the same code path as a real camera.

- `new FileCamera(folder, loop)`: `DirectoryNotFoundException` when the folder does not exist, `FileNotFoundException` when it has no image files.
- `Files` (the order), `LastFile` (the file of the last frame), `Loop`.
- With `loop: false`, `Grab` after the last file throws `InvalidOperationException`.

<a id="rtsp-camera"></a>
### RtspCamera

A network camera that streams over RTSP: IP cameras, and the camera emulator of Devinno.Vision.Studio.

```csharp
await using var camera = await RtspCamera.OpenAsync("rtsp://192.168.0.10:8554/studio");
using var img = await camera.Grab(ct);       // a frame decoded after this call
```

- `OpenAsync(url, timeout, ct)` waits for the first frame (default 10 s) and throws `IOException` when the stream cannot be opened or sends no frame in time.
- A reader thread keeps decoding, so `Grab` returns a frame decoded after the call, never an old buffered one. `WaitFrame` does the same.
- `Grab` and `WaitFrame` throw `IOException` when the stream stopped. Reconnecting is the app's job: dispose the camera and open it again.
- `Url`, `Size` (`(Width, Height)` from the stream). Frames are BGR.
- Decoding uses OpenCV's FFmpeg backend (in `OpenCvSharp4.runtime.win`). TCP transport is used unless the environment variable `OPENCV_FFMPEG_CAPTURE_OPTIONS` is already set.
- A stream is delayed against the real scene. When a PLC or another signal says "the part is in place", a frame grabbed right away can still show the previous scene; allow for the stream's delay before grabbing.
