# Third-party notices — Devinno Vision Studio

Devinno Vision Studio (the download) contains the components below. Devinno Vision Studio itself, Devinno.Vision, Devinno.Vision.UI, Devinno.UI and Devinno.Communication are MIT (see `LICENSE`).

The license and notice files that ship inside the NuGet packages are copied unchanged into the `licenses/` folder of the download, one folder per package.

| Component | Files in the download | License | Source |
|---|---|---|---|
| OpenCvSharp4 | `OpenCvSharp.dll`, `OpenCvSharpExtern.dll` | Apache-2.0 | https://github.com/shimat/opencvsharp |
| OpenCV (built into `OpenCvSharpExtern.dll`) | `OpenCvSharpExtern.dll` | Apache-2.0 | https://opencv.org/license/ |
| FFmpeg (OpenCV's video plugin) | `opencv_videoio_ffmpeg4130_64.dll` | LGPL-2.1-or-later | https://ffmpeg.org/legal.html · build: https://github.com/opencv/opencv_3rdparty (branch `ffmpeg/4.x`) |
| ONNX Runtime | `Microsoft.ML.OnnxRuntime.dll`, `onnxruntime.dll`, `onnxruntime_providers_shared.dll` | MIT | https://github.com/microsoft/onnxruntime |
| SkiaSharp | `SkiaSharp*.dll`, `libSkiaSharp.dll` | MIT (Skia: BSD-3-Clause) | https://github.com/mono/SkiaSharp |
| Model Context Protocol C# SDK | `ModelContextProtocol*.dll` | Apache-2.0 | https://github.com/modelcontextprotocol/csharp-sdk |
| MQTTnet | `MQTTnet.dll` | MIT | https://github.com/dotnet/MQTTnet |
| OpenTK | `OpenTK*.dll` | MIT | https://github.com/opentk/opentk |
| GLFW | `glfw3.dll` | Zlib | https://www.glfw.org/license.html |
| FFmpeg.AutoGen (bindings only; no FFmpeg binaries) | `FFmpeg.AutoGen.dll` | MIT | https://github.com/Ruslan-B/FFmpeg.AutoGen |
| .NET libraries | `Microsoft.Extensions.*.dll`, `System.*.dll`, `Microsoft.Windows.SDK.NET.dll`, `WinRT.Runtime.dll` | MIT | https://github.com/dotnet |
| Font Awesome Free 6.1.2 (embedded in `Devinno.UI.dll`) | `Devinno.UI.dll` | Fonts: SIL OFL 1.1 · Icons: CC BY 4.0 | https://fontawesome.com/license/free |

## FFmpeg (LGPL)

`opencv_videoio_ffmpeg4130_64.dll` is the FFmpeg build that the OpenCV project distributes with OpenCV. It is a separate DLL, loaded at run time, and is licensed under the GNU Lesser General Public License, version 2.1 or later. You may replace it with your own build of the same interface. Its source code and build scripts are in the OpenCV third-party repository linked above.

## Not in the download

Studio can use these, but does not contain them:

- **MediaMTX** (MIT, https://github.com/bluenviron/mediamtx): downloaded by [Install MediaMTX] / `setup_mediamtx` only when the user asks, together with its `LICENSE`.
- **ffmpeg.exe**: installed by the user (for example `winget install ffmpeg`) for the virtual camera.
- **PyTorch and Python packages**: installed by `setup_python` into Studio's own Python environment when the user asks.
- **ResNet18 weights** for the feature model (`tools/export_features.py`): downloaded from pytorch.org by that script.
