using OpenCvSharp;

namespace Devinno.Vision;

/// <summary>
/// An image: an OpenCV <see cref="OpenCvSharp.Mat"/> plus capture information and where it came from in the original frame.
/// </summary>
/// <remarks>
/// <para><b>Ownership</b>: whoever creates an image disposes it. A frame from a camera or <see cref="Load"/> belongs to the app (<c>using</c>).
/// Images created inside an inspection step (<see cref="Gray"/>, <see cref="Crop"/>, …) are released automatically when the run ends;
/// to use one after the run, call <see cref="Keep"/> inside the step. Outside a run, derived images belong to the caller.</para>
/// <para><b>Coordinates</b>: a derived image remembers how its pixels map to the original image; <see cref="ToSource"/> converts a point.
/// Results drawn in a step are converted to the original image automatically.</para>
/// <para><b>Going down to OpenCV</b>: <see cref="Mat"/> is the image itself; any OpenCV function can use it. <see cref="From"/> brings a Mat back.</para>
/// <para><b>Formats</b>: 8-bit BGR and 8-bit gray only (<see cref="VisionPixelFormat"/>).</para>
/// </remarks>
/// <example>
/// <code>
/// using var img = VisionImage.Load(@"C:\images\part01.bmp");       // the app owns it
/// using var r = await job.RunAsync(img);                           // images made inside the run are released by the run
///
/// // Outside a run, derived images belong to the caller
/// using var gray = img.Gray();
/// using var small = gray.Resize(320, 240);
/// Cv2.GaussianBlur(small.Mat, small.Mat, new Size(5, 5), 0);       // any OpenCV call on the Mat
/// var inOriginal = small.ToSource(new Point2d(10, 10));            // the same point in img
/// </code>
/// </example>
public sealed class VisionImage : IDisposable
{
    private readonly object _gate = new();
    private Mat? _mat;
    private string? _releasedBy;
    private VisionScope? _scope;

    internal VisionImage(Mat mat, VisionImageInfo info, Affine toSource, VisionScope? scope)
    {
        CheckFormat(mat);
        _mat = mat;
        Info = info;
        ToSourceTransform = toSource;
        _scope = scope;
        scope?.Register(this);
    }

    /// <summary>The pixels. Use it to call OpenCV directly. Do not dispose it; dispose the <see cref="VisionImage"/> instead.</summary>
    /// <exception cref="ObjectDisposedException">The image was disposed or released at the end of a run; the message says which.</exception>
    public Mat Mat
    {
        get
        {
            var m = _mat;
            if (m is not null)
                return m;
            throw _releasedBy is { } job
                ? new ObjectDisposedException(nameof(VisionImage), $"This image was released when the run of '{job}' ended. To use it outside the run, call Keep() inside the step.")
                : new ObjectDisposedException(nameof(VisionImage), "This image has been disposed.");
        }
    }

    /// <summary>Capture information (shared by all images derived from the same frame).</summary>
    public VisionImageInfo Info { get; }

    /// <summary>The pixel format.</summary>
    public VisionPixelFormat Format => FormatOf(Mat);

    /// <summary>Width in px.</summary>
    public int Width => Mat.Width;

    /// <summary>Height in px.</summary>
    public int Height => Mat.Height;

    /// <summary>Size in px.</summary>
    public Size Size => new(Mat.Width, Mat.Height);

    internal Affine ToSourceTransform { get; }

    internal string? CreatedInStep { get; set; }

    internal bool IsDisposed => _mat is null;

    internal VisionScope? Scope => _scope;

    // ── 만들기 ─────────────────────────────────────────────

    /// <summary>Loads an image file (PNG, BMP, JPEG, …). Gray files stay <see cref="VisionPixelFormat.Gray8"/>; color files become <see cref="VisionPixelFormat.Bgr8"/>.</summary>
    /// <param name="path">The file path.</param>
    /// <returns>A new image owned by the caller (dispose it, after the results that borrow it).</returns>
    /// <remarks>A file with an alpha channel (8-bit BGRA) is loaded without the alpha. <see cref="Info"/> has the load time and no camera.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist or cannot be decoded.</exception>
    /// <exception cref="NotSupportedException">The file has a pixel format that is not supported yet (e.g. 16-bit).</exception>
    public static VisionImage Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var raw = ImageFile.Read(path, ImreadModes.Unchanged);
        if (raw.Empty())
        {
            raw.Dispose();
            throw new FileNotFoundException($"Cannot read the image file: {path}", path);
        }
        return new VisionImage(Normalize(raw, path), new VisionImageInfo(), Affine.Identity, null);
    }

    /// <summary>Wraps an OpenCV Mat. The pixels are shared, not copied; the Mat stays yours (dispose it as usual) and this image keeps the pixels alive until it is disposed.</summary>
    /// <param name="mat">An 8-bit BGR (<c>CV_8UC3</c>) or gray (<c>CV_8UC1</c>) Mat.</param>
    /// <param name="info">Capture information, or <see langword="null"/> for none.</param>
    /// <returns>A new image owned by the caller.</returns>
    /// <remarks>Inside a step use <c>ctx.From(mat)</c> instead: that image belongs to the run and keeps the step's coordinates.</remarks>
    /// <example>
    /// <code>
    /// using var mat = Cv2.ImRead(path, ImreadModes.Grayscale);     // or a frame from your own camera SDK
    /// using var img = VisionImage.From(mat, new VisionImageInfo { Camera = "cam1", FrameNo = 42 });
    /// using var r = await job.RunAsync(img);
    /// </code>
    /// </example>
    /// <exception cref="ArgumentException">The Mat is empty.</exception>
    /// <exception cref="NotSupportedException">The Mat is not 8-bit BGR (<c>CV_8UC3</c>) or 8-bit gray (<c>CV_8UC1</c>).</exception>
    public static VisionImage From(Mat mat, VisionImageInfo? info = null)
    {
        ArgumentNullException.ThrowIfNull(mat);
        if (mat.Empty())
            throw new ArgumentException("The Mat is empty.", nameof(mat));
        return new VisionImage(Share(mat), info ?? new VisionImageInfo(), Affine.Identity, null);
    }

    // ── 파생 이미지 ─────────────────────────────────────────

    /// <summary>Cuts out a region. A rectangle shares the pixels; a rotated rectangle is resampled upright (its x axis along the rectangle's angle).
    /// A region partly outside the image is allowed while at least half of it is inside — the outside is filled with the nearest edge pixels.</summary>
    /// <param name="roi">The region in this image's coordinates.</param>
    /// <returns>The region as an image (released at the end of the run inside a step; otherwise owned by the caller).</returns>
    /// <exception cref="ArgumentException">The ROI is not set.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Less than half of the ROI is inside the image.</exception>
    public VisionImage Crop(VisionRoi roi)
    {
        if (roi.IsEmpty)
            throw new ArgumentException("The ROI is not set (empty or zero size).", nameof(roi));
        return CropPlaced(roi.Center, roi.Width, roi.Height, roi.Shape == VisionRoiShape.Rotated ? roi.Angle : 0);
    }

    internal VisionImage CropPlaced(Point2d center, double width, double height, double angle)
    {
        var mat = Mat;
        var w = (int)Math.Round(width);
        var h = (int)Math.Round(height);
        if (w < 1 || h < 1)
            throw new ArgumentException($"The ROI is too small ({width:0.#}×{height:0.#} px).");

        var x0 = center.X - width / 2;
        var y0 = center.Y - height / 2;
        if (Math.Abs(angle) < 1e-9 && IsWhole(x0) && IsWhole(y0))
        {
            var rect = new Rect((int)Math.Round(x0), (int)Math.Round(y0), w, h);
            if (rect.X >= 0 && rect.Y >= 0 && rect.Right <= mat.Width && rect.Bottom <= mat.Height)
                return Derive(new Mat(mat, rect), Affine.Translate(rect.X, rect.Y));
        }

        // 일부가 이미지 밖이면: 절반 이상 안이면 바깥을 가장자리 값으로 채우고, 아니면 오류 (엉뚱한 곳을 검사하지 않게)
        var inside = InsideFraction(center, width, height, angle, mat.Width, mat.Height);
        if (inside < 0.5)
            throw new ArgumentOutOfRangeException(nameof(center), $"The ROI (center {center.X:0.#}, {center.Y:0.#}, {width:0.#}×{height:0.#}, {angle:0.##}°) is outside the image ({mat.Width}×{mat.Height}): only {inside:P0} inside.");

        // 출력 (u, v) → 이 이미지: x = cx + cos·(u − w/2) − sin·(v − h/2), y = cy + sin·(u − w/2) + cos·(v − h/2)
        // y 가 아래로 향하므로 각도 + 는 화면에서 시계방향 (VisionPose 와 같은 규약)
        var rad = angle * Math.PI / 180;
        double c = Math.Cos(rad), s = Math.Sin(rad);
        var map = new Affine(c, -s, center.X - c * w / 2.0 + s * h / 2.0, s, c, center.Y - s * w / 2.0 - c * h / 2.0);

        using var m = new Mat(2, 3, MatType.CV_64FC1);
        m.Set(0, 0, map.A); m.Set(0, 1, map.B); m.Set(0, 2, map.C);
        m.Set(1, 0, map.D); m.Set(1, 1, map.E); m.Set(1, 2, map.F);
        var dst = new Mat();
        Cv2.WarpAffine(mat, dst, m, new Size(w, h), InterpolationFlags.Linear | InterpolationFlags.WarpInverseMap, BorderTypes.Replicate);
        return Derive(dst, map);
    }

    /// <summary>Resizes the image. Coordinates are still tracked back to the original image.</summary>
    /// <param name="width">New width in px.</param>
    /// <param name="height">New height in px.</param>
    /// <returns>The resized image.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The size is not positive.</exception>
    public VisionImage Resize(int width, int height)
    {
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(nameof(width), $"Resize needs a positive size (got {width}×{height}).");
        var mat = Mat;
        var dst = new Mat();
        Cv2.Resize(mat, dst, new Size(width, height), 0, 0, InterpolationFlags.Area);
        double sx = (double)mat.Width / width, sy = (double)mat.Height / height;
        // OpenCV 화소 중심 규약: x_src = (x_dst + 0.5)·s − 0.5
        return Derive(dst, new Affine(sx, 0, 0.5 * sx - 0.5, 0, sy, 0.5 * sy - 0.5));
    }

    /// <summary>Converts to gray. A gray image returns a new image sharing the same pixels.</summary>
    /// <returns>A <see cref="VisionPixelFormat.Gray8"/> image.</returns>
    public VisionImage Gray()
    {
        var mat = Mat;
        if (Format == VisionPixelFormat.Gray8)
            return Derive(Share(mat), Affine.Identity);
        var dst = new Mat();
        Cv2.CvtColor(mat, dst, ColorConversionCodes.BGR2GRAY);
        return Derive(dst, Affine.Identity);
    }

    /// <summary>Gaussian blur.</summary>
    /// <param name="kernel">Kernel size in px; odd (3, 5, 7, …).</param>
    /// <returns>The blurred image.</returns>
    /// <exception cref="ArgumentException">The kernel size is not a positive odd number.</exception>
    public VisionImage Blur(int kernel)
    {
        if (kernel < 1 || kernel % 2 == 0)
            throw new ArgumentException($"Blur needs an odd kernel size such as 3 or 5 (got {kernel}).", nameof(kernel));
        var dst = new Mat();
        Cv2.GaussianBlur(Mat, dst, new Size(kernel, kernel), 0);
        return Derive(dst, Affine.Identity);
    }

    /// <summary>Binarizes the image: pixels above the threshold become 255, others 0 (reversed with <paramref name="invert"/>). A color image is converted to gray first.</summary>
    /// <param name="mode">Fixed threshold or automatic (Otsu).</param>
    /// <param name="value">The threshold (0–255) when <paramref name="mode"/> is <see cref="ThresholdMode.Fixed"/>.</param>
    /// <param name="invert">Make dark pixels 255 instead (to find dark objects).</param>
    /// <returns>A <see cref="VisionPixelFormat.Gray8"/> image with 0 and 255 only.</returns>
    public VisionImage Threshold(ThresholdMode mode = ThresholdMode.Otsu, int value = 128, bool invert = false)
    {
        var mat = Mat;
        using var gray = Format == VisionPixelFormat.Gray8 ? null : new Mat();
        if (gray is not null)
            Cv2.CvtColor(mat, gray, ColorConversionCodes.BGR2GRAY);
        var type = invert ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary;
        if (mode == ThresholdMode.Otsu)
            type |= ThresholdTypes.Otsu;
        var dst = new Mat();
        Cv2.Threshold(gray ?? mat, dst, Math.Clamp(value, 0, 255), 255, type);
        return Derive(dst, Affine.Identity);
    }

    // ── 모델 입력 ─────────────────────────────────────────

    /// <summary>
    /// Makes a model input (for ONNX and other inference): resized, channels converted (BGR↔RGB, color↔gray), scaled and normalized, channels-first (CHW).
    /// <see cref="OnnxModel.Run"/> calls this itself; use it directly when you run a model with your own inference code (e.g. an ONNX Runtime tensor of shape [1, C, height, width]).
    /// </summary>
    /// <param name="width">Model input width (px).</param>
    /// <param name="height">Model input height (px).</param>
    /// <param name="format">Channels and normalization; <see langword="null"/> = <see cref="VisionTensorFormat.ImageNet"/>.</param>
    /// <returns><c>C × height × width</c> values, channel by channel.</returns>
    /// <remarks>To map model output back to this image, resize to the same size with <see cref="Resize"/> and draw with <c>from:</c> that image.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The size is not positive.</exception>
    /// <exception cref="ArgumentException">The format does not have 1 or 3 channels, or its mean and std lengths differ.</exception>
    public float[] ToTensor(int width, int height, VisionTensorFormat? format = null)
    {
        format ??= VisionTensorFormat.ImageNet;
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(nameof(width), $"ToTensor needs a positive size (got {width}×{height}).");
        if (format.Channels is not (1 or 3) || format.Std.Length != format.Channels)
            throw new ArgumentException("VisionTensorFormat needs 1 or 3 channels and one Std per Mean.", nameof(format));

        var src = Mat;
        using var sized = new Mat();
        if (src.Width == width && src.Height == height)
            src.CopyTo(sized);
        else
            Cv2.Resize(src, sized, new Size(width, height), 0, 0, InterpolationFlags.Area);
        using var conv = new Mat();
        var c = format.Channels;
        switch (c, sized.Channels())
        {
            case (1, 3): Cv2.CvtColor(sized, conv, ColorConversionCodes.BGR2GRAY); break;
            case (3, 1): Cv2.CvtColor(sized, conv, format.Rgb ? ColorConversionCodes.GRAY2RGB : ColorConversionCodes.GRAY2BGR); break;
            case (3, 3) when format.Rgb: Cv2.CvtColor(sized, conv, ColorConversionCodes.BGR2RGB); break;
            default: sized.CopyTo(conv); break;
        }

        var plane = width * height;
        var bytes = new byte[plane * c];
        System.Runtime.InteropServices.Marshal.Copy(conv.Data, bytes, 0, bytes.Length);   // 연속 Mat (새로 만든 것)
        var result = new float[plane * c];
        for (var ch = 0; ch < c; ch++)
        {
            float k = format.Scale / format.Std[ch], b = -format.Mean[ch] / format.Std[ch];
            var off = ch * plane;
            for (var i = 0; i < plane; i++)
                result[off + i] = bytes[i * c + ch] * k + b;
        }
        return result;
    }

    // ── 좌표 ──────────────────────────────────────────────

    /// <summary>Converts a point in this image to the original image (the frame, or inside a run, the image given to <c>RunAsync</c>).</summary>
    /// <param name="point">A point in this image (px).</param>
    /// <returns>The same point in the original image.</returns>
    public Point2d ToSource(Point2d point) => ToSourceTransform.Map(point);

    // ── 수명 ──────────────────────────────────────────────

    /// <summary>Takes this image out of the run so it survives the end of the run. It then belongs to the app, which must dispose it.</summary>
    /// <returns>This image.</returns>
    /// <remarks>Call it inside the step, before the run ends. The pixels are not copied. Outside a run it does nothing.</remarks>
    /// <example>
    /// <code>
    /// VisionImage? binary = null;
    /// job.Add("holes", roi: () =&gt; roi, ctx =&gt;
    /// {
    ///     binary = ctx.Image.Threshold(ThresholdMode.Fixed, 80, invert: true).Keep();   // survives the run
    /// });
    /// using (var r = await job.RunAsync(img)) { }
    /// using (binary) binary?.Save("binary.png");                                         // the app disposes it
    /// </code>
    /// </example>
    /// <exception cref="ObjectDisposedException">The image was already disposed or released.</exception>
    public VisionImage Keep()
    {
        _ = Mat; // 해제된 이미지면 여기서 알린다
        var scope = Interlocked.Exchange(ref _scope, null);
        scope?.Remove(this);
        return this;
    }

    /// <summary>Copies the pixels into a new image owned by the caller (also outside a run).</summary>
    /// <returns>The copy.</returns>
    public VisionImage Clone() => new(Mat.Clone(), Info, ToSourceTransform, null);

    /// <summary>Saves the image to a file. The format follows the extension (.png is lossless).</summary>
    /// <param name="path">The file path; the folder must exist.</param>
    /// <exception cref="IOException">The file could not be written.</exception>
    public void Save(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        ImageFile.Write(path, Mat);
    }

    /// <summary>Releases the pixels. Safe to call more than once.</summary>
    public void Dispose()
    {
        Mat? m;
        lock (_gate)
        {
            m = _mat;
            _mat = null;
        }
        if (m is null)
            return;
        Interlocked.Exchange(ref _scope, null)?.Remove(this);
        m.Dispose();
    }

    // 실행 끝 — 범위가 해제
    internal void Release(string job)
    {
        Mat? m;
        lock (_gate)
        {
            m = _mat;
            _mat = null;
            _releasedBy = job;
            _scope = null;
        }
        m?.Dispose();
    }

    internal void DetachScope() => _scope = null;

    // ── 내부 ──────────────────────────────────────────────

    internal VisionImage Derive(Mat mat, Affine childToThis)
        => new(mat, Info, childToThis.Then(ToSourceTransform), _scope);

    // 같은 픽셀을 가리키는 새 Mat 헤더 (참조 수 증가 — 원본을 해제해도 픽셀은 산다)
    internal static Mat Share(Mat mat) => new(mat, new Rect(0, 0, mat.Width, mat.Height));

    private static bool IsWhole(double v) => Math.Abs(v - Math.Round(v)) < 1e-6;

    // ROI 면적 중 이미지 안에 있는 비율 (회전 사각형 ∩ 이미지 사각형)
    private static double InsideFraction(Point2d center, double width, double height, double angle, int imageWidth, int imageHeight)
    {
        var roi = VisionRoi.CornersOf(center, width, height, angle).Select(p => new Point2f((float)p.X, (float)p.Y)).ToArray();
        Point2f[] img = [new(0, 0), new(imageWidth, 0), new(imageWidth, imageHeight), new(0, imageHeight)];
        var area = Cv2.IntersectConvexConvex(roi, img, out _);
        return area / (width * height);
    }

    private static void CheckFormat(Mat mat) => FormatOf(mat);

    private static VisionPixelFormat FormatOf(Mat mat)
    {
        var t = mat.Type();
        if (t == MatType.CV_8UC3)
            return VisionPixelFormat.Bgr8;
        if (t == MatType.CV_8UC1)
            return VisionPixelFormat.Gray8;
        throw new NotSupportedException($"Unsupported pixel type {t}. Devinno.Vision supports 8-bit BGR (CV_8UC3) and 8-bit gray (CV_8UC1); convert the Mat first (e.g. Cv2.CvtColor).");
    }

    private static Mat Normalize(Mat raw, string path)
    {
        var t = raw.Type();
        if (t == MatType.CV_8UC3 || t == MatType.CV_8UC1)
            return raw;
        if (t == MatType.CV_8UC4)
        {
            var bgr = new Mat();
            Cv2.CvtColor(raw, bgr, ColorConversionCodes.BGRA2BGR);
            raw.Dispose();
            return bgr;
        }
        raw.Dispose();
        throw new NotSupportedException($"Unsupported pixel type {t} in {path}. Supported: 8-bit gray, 8-bit color.");
    }
}
