# Devinno.Vision

A .NET 9 vision inspection library on [OpenCvSharp4](https://github.com/shimat/opencvsharp).

One image goes into one job (a list of steps) and comes out as one result: OK / NG / Error, NG codes, measured values and overlays. An inspection is either loaded from a recipe made in the teaching tool (Devinno.Vision.Studio, in this repository) or written in C# as steps.

- Images are released automatically inside a run, and coordinates are tracked back to the original image.
- ROIs follow the part (alignment pose).
- A run does not throw for inspection problems: they come out as NG or Error in the result.
- Steps have timeouts; a part that was not inspected never comes out OK.
- A small toolkit: blobs, brightness, caliper, round-part alignment, difference against good parts.
- ONNX models on CPU (ONNX Runtime): anomaly detection from good parts, defect classifier.

> This is the first version. The API may change.

## Packages

| Package | What it is |
|---|---|
| `Devinno.Vision` | The library: images, jobs, results, recipes, toolkit, camera interface, image-folder and RTSP cameras |
| `Devinno.Vision.UI` | `DvVisionView`: shows a result (image, overlays, judgment) on a [Devinno.UI](https://github.com/devinno-kr/Devinno.UI) screen and lets the operator drag a ROI |
| `Devinno.Vision.FlashCap` | `FlashCapCamera`: USB (UVC) cameras, based on [FlashCap](https://github.com/kekyo/FlashCap) |

## Install

`Devinno.Vision` brings `OpenCvSharp4` and `Microsoft.ML.OnnxRuntime`. Add the OpenCV native runtime package yourself, with the same version as the `OpenCvSharp4` package that `Devinno.Vision` references. On Windows:

```xml
<ItemGroup>
  <PackageReference Include="Devinno.Vision" Version="0.1.0" />
  <PackageReference Include="OpenCvSharp4.runtime.win" Version="4.13.0.20261107" />
</ItemGroup>
```

Without it the first image call fails with an exception that names `OpenCvSharpExtern`. Other platforms have their own `OpenCvSharp4.runtime.*` packages; they are not verified with this library.

## Run a recipe

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

## Documentation

The documents ship inside the package (`docs/`) and are copied to `.devinno/Devinno.Vision/` in your project on build, so AI coding tools can read them. Turn the copy off with `<DevinnoCopyDocs>false</DevinnoCopyDocs>`.

- [llms.txt](https://github.com/devinno-kr/Devinno.Vision/blob/main/llms.txt): the entry point (question → page)
- [llm-rules.md](https://github.com/devinno-kr/Devinno.Vision/blob/main/llm-rules.md): rules, project setup, minimal examples, symptom → cause → fix
- [vision-recipe.md](https://github.com/devinno-kr/Devinno.Vision/blob/main/vision-recipe.md): recipes, results, packages, good-part model
- [vision-jobs.md](https://github.com/devinno-kr/Devinno.Vision/blob/main/vision-jobs.md): jobs, steps, ROI and pose, judging, images
- [vision-toolkit.md](https://github.com/devinno-kr/Devinno.Vision/blob/main/vision-toolkit.md): toolkit, ONNX, cameras

The C# API reference is the IntelliSense XML in the package (English, and Korean under `ko/`).

## License

[MIT](https://github.com/devinno-kr/Devinno.Vision/blob/main/LICENSE)
