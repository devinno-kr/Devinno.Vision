using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCvSharp;

namespace Devinno.Vision;

/// <summary>
/// Difference inspection against good parts ("golden template"): learns the mean and spread of each pixel from aligned good images,
/// and finds the regions that differ more than the good parts do — stains, missing material, bends, foreign objects.
/// </summary>
/// <remarks>
/// <para>The images must be aligned and the same size — normally the step's ROI (<c>ctx.Image</c>), which already follows the part's pose.
/// Collect training crops by running the alignment on good parts and keeping <c>ctx.Image.Clone()</c>.</para>
/// <para>A pixel differs when <c>|value − mean| &gt; k · (spread + minSpread)</c>. Where good parts vary (edges, texture) the spread is large, so they do not trigger.
/// With <c>color: true</c> the saturation is checked too (colored stains on a gray part).</para>
/// <para>In a setup only <see cref="Path"/> is saved (like <see cref="VisionModel"/>); <see cref="Save"/> writes the model folder (JSON + 16-bit PNG).</para>
/// <para>The comparison is per pixel, so the alignment must be good to the pixel. Where that is not possible, use <see cref="AnomalyModel"/>.</para>
/// </remarks>
/// <example>
/// <code>
/// // Teaching: the same ROI crop of every good part
/// var trained = DiffModel.Train(goodCrops);
/// trained.Save(recipeFolder, "models/diff");
///
/// // A step whose ROI is the one used for training (s.Diff was read with s.Diff.Load(recipeFolder))
/// job.Add("surface", roi: s =&gt; s.SurfaceRoi, (ctx, s) =&gt;
/// {
///     var d = s.Diff.Compare(ctx.Image);
///     ctx.Draw(d.Regions);
///     ctx.Value("largest difference", d.MaxArea, max: 30, code: "SURFACE", unit: "px²");
/// });
/// </code>
/// </example>
[JsonConverter(typeof(DiffModelConverter))]
public sealed class DiffModel : IDisposable
{
    private Mat? _meanGray, _stdGray, _meanSat, _stdSat;

    /// <summary>The model folder, relative to the root passed to <see cref="Load"/> (or absolute).</summary>
    public string? Path { get; set; }

    /// <summary>Image width the model was trained for (px); 0 before training or loading.</summary>
    public int Width { get; private set; }

    /// <summary>Image height (px).</summary>
    public int Height { get; private set; }

    /// <summary>Whether saturation is checked too.</summary>
    public bool Color { get; private set; }

    /// <summary>How many good images it was trained on.</summary>
    public int Count { get; private set; }

    /// <summary>Whether the model has data (trained or loaded).</summary>
    public bool IsReady => _meanGray is not null;

    /// <summary>Learns from aligned good images of the same size.</summary>
    /// <param name="goods">Good images (at least 2; 20 or more is better).</param>
    /// <param name="color">Check saturation too (images must be BGR).</param>
    /// <returns>The model.</returns>
    /// <exception cref="ArgumentException">Fewer than 2 images, or the sizes differ.</exception>
    public static DiffModel Train(IEnumerable<VisionImage> goods, bool color = false)
    {
        ArgumentNullException.ThrowIfNull(goods);
        var list = goods.ToList();
        if (list.Count < 2)
            throw new ArgumentException("DiffModel needs at least 2 good images.", nameof(goods));
        var size = list[0].Size;
        if (list.Any(g => g.Size != size))
            throw new ArgumentException($"All training images must be the same size ({size.Width}×{size.Height}). Use the same ROI for all of them.", nameof(goods));
        if (color && list.Any(g => g.Format != VisionPixelFormat.Bgr8))
            throw new ArgumentException("color: true needs BGR images.", nameof(goods));

        var model = new DiffModel { Width = size.Width, Height = size.Height, Color = color, Count = list.Count };
        (model._meanGray, model._stdGray) = Stats(list.Select(g => Channel(g, saturation: false)));
        if (color)
            (model._meanSat, model._stdSat) = Stats(list.Select(g => Channel(g, saturation: true)));
        return model;
    }

    /// <summary>Compares an aligned image with the model.</summary>
    /// <param name="image">The image (same size as the training images).</param>
    /// <param name="k">How many spreads a pixel may differ (3–4 is typical; lower = more sensitive).</param>
    /// <param name="minSpread">Added to the spread so that very uniform areas do not trigger on noise (brightness levels).</param>
    /// <param name="minArea">Smallest region to report (px²).</param>
    /// <returns>The differing regions, largest first.</returns>
    /// <exception cref="InvalidOperationException">The model is not trained or loaded.</exception>
    /// <exception cref="ArgumentException">The image size differs from the model.</exception>
    public DiffResult Compare(VisionImage image, double k = 3.5, double minSpread = 6, int minArea = 10)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (_meanGray is null || _stdGray is null)
            throw new InvalidOperationException("The DiffModel is not trained or loaded.");
        if (image.Width != Width || image.Height != Height)
            throw new ArgumentException($"The image is {image.Width}×{image.Height} but the model is {Width}×{Height}. Use the same ROI as for training.", nameof(image));

        using var mask = Outliers(Channel(image, saturation: false), _meanGray, _stdGray, k, minSpread);
        if (Color && _meanSat is not null && _stdSat is not null && image.Format == VisionPixelFormat.Bgr8)
        {
            using var sat = Outliers(Channel(image, saturation: true), _meanSat, _stdSat, k, minSpread);
            Cv2.BitwiseOr(mask, sat, mask);
        }
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Open, kernel);
        Cv2.FindContours(mask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        return new DiffResult(Blob.FromContours(contours, minArea, double.MaxValue, image.ToSourceTransform));
    }

    /// <summary>Saves the model to a folder (model.json + 16-bit PNG), and sets <see cref="Path"/>.</summary>
    /// <param name="root">The root folder (for example the recipe folder).</param>
    /// <param name="path">The model folder relative to <paramref name="root"/>; created if missing.</param>
    /// <exception cref="InvalidOperationException">The model is not trained.</exception>
    public void Save(string root, string path)
    {
        if (_meanGray is null || _stdGray is null)
            throw new InvalidOperationException("The DiffModel is not trained.");
        var dir = System.IO.Path.Combine(root, path);
        Directory.CreateDirectory(dir);
        File.WriteAllText(System.IO.Path.Combine(dir, "model.json"), JsonSerializer.Serialize(new Info(Width, Height, Color, Count)));
        Write(_meanGray, System.IO.Path.Combine(dir, "mean_gray.png"));
        Write(_stdGray, System.IO.Path.Combine(dir, "std_gray.png"));
        if (_meanSat is not null && _stdSat is not null)
        {
            Write(_meanSat, System.IO.Path.Combine(dir, "mean_sat.png"));
            Write(_stdSat, System.IO.Path.Combine(dir, "std_sat.png"));
        }
        Path = path;
    }

    /// <summary>Reads the model folder at <see cref="Path"/>.</summary>
    /// <param name="root">The folder <see cref="Path"/> is relative to.</param>
    /// <exception cref="InvalidOperationException"><see cref="Path"/> is not set.</exception>
    /// <exception cref="FileNotFoundException">A file of the model folder is missing or cannot be read.</exception>
    /// <exception cref="DirectoryNotFoundException">The model folder does not exist.</exception>
    /// <exception cref="InvalidDataException">The folder is not a difference model.</exception>
    public void Load(string root)
    {
        if (string.IsNullOrEmpty(Path))
            throw new InvalidOperationException("DiffModel.Path is not set.");
        var dir = System.IO.Path.Combine(root, Path);
        var info = JsonSerializer.Deserialize<Info>(File.ReadAllText(System.IO.Path.Combine(dir, "model.json")))
            ?? throw new InvalidDataException($"Not a DiffModel folder: {dir}");
        (Width, Height, Color, Count) = (info.Width, info.Height, info.Color, info.Count);
        _meanGray = Read(System.IO.Path.Combine(dir, "mean_gray.png"));
        _stdGray = Read(System.IO.Path.Combine(dir, "std_gray.png"));
        if (Color)
        {
            _meanSat = Read(System.IO.Path.Combine(dir, "mean_sat.png"));
            _stdSat = Read(System.IO.Path.Combine(dir, "std_sat.png"));
        }
    }

    /// <summary>Releases the model data. Copies made by <c>VisionSetup.Copy</c> share the data: dispose the model only when no setup uses it any more.</summary>
    public void Dispose()
    {
        _meanGray?.Dispose();
        _stdGray?.Dispose();
        _meanSat?.Dispose();
        _stdSat?.Dispose();
        _meanGray = _stdGray = _meanSat = _stdSat = null;
    }

    internal static DiffModel Shared(string? path, DiffModel? from) => from is null
        ? new DiffModel { Path = path }
        : new DiffModel { Path = path, Width = from.Width, Height = from.Height, Color = from.Color, Count = from.Count,
                          _meanGray = from._meanGray, _stdGray = from._stdGray, _meanSat = from._meanSat, _stdSat = from._stdSat };

    private sealed record Info(int Width, int Height, bool Color, int Count);

    private static Mat Channel(VisionImage img, bool saturation)
    {
        var f = new Mat();
        if (saturation)
        {
            using var hsv = new Mat();
            Cv2.CvtColor(img.Mat, hsv, ColorConversionCodes.BGR2HSV);
            using var s = hsv.ExtractChannel(1);
            s.ConvertTo(f, MatType.CV_32F);
        }
        else
        {
            using var g = img.Gray();
            g.Mat.ConvertTo(f, MatType.CV_32F);
        }
        return f;
    }

    private static (Mat Mean, Mat Std) Stats(IEnumerable<Mat> channels)
    {
        Mat? sum = null, sq = null;
        var n = 0;
        foreach (var c in channels)
        {
            using (c)
            {
                sum ??= new Mat(c.Size(), MatType.CV_32F, Scalar.All(0));
                sq ??= new Mat(c.Size(), MatType.CV_32F, Scalar.All(0));
                Cv2.Add(sum, c, sum);
                using var c2 = new Mat();
                Cv2.Multiply(c, c, c2);
                Cv2.Add(sq, c2, sq);
                n++;
            }
        }
        var mean = new Mat();
        sum!.ConvertTo(mean, -1, 1.0 / n);
        using var ex2 = new Mat();
        sq!.ConvertTo(ex2, -1, 1.0 / n);
        using var m2 = new Mat();
        Cv2.Multiply(mean, mean, m2);
        using var variance = new Mat();
        Cv2.Subtract(ex2, m2, variance);
        Cv2.Max(variance, 0, variance);
        var std = new Mat();
        Cv2.Sqrt(variance, std);
        sum.Dispose();
        sq.Dispose();
        return (mean, std);
    }

    private static Mat Outliers(Mat value, Mat mean, Mat std, double k, double minSpread)
    {
        using (value)
        {
            using var d = new Mat();
            Cv2.Absdiff(value, mean, d);
            using var den = new Mat();
            Cv2.Add(std, minSpread, den);
            den.ConvertTo(den, -1, k);
            using var z = new Mat();
            Cv2.Compare(d, den, z, CmpTypes.GT);
            return z.Clone();
        }
    }

    // 값 × 100 을 16비트 PNG 로 (0.01 단계, 655 까지)
    private static void Write(Mat m, string file)
    {
        using var u = new Mat();
        m.ConvertTo(u, MatType.CV_16U, 100);
        if (!Cv2.ImWrite(file, u))
            throw new IOException($"Cannot write {file}");
    }

    private static Mat Read(string file)
    {
        using var u = Cv2.ImRead(file, ImreadModes.Unchanged);
        if (u.Empty())
            throw new FileNotFoundException($"Cannot read {file}", file);
        var f = new Mat();
        u.ConvertTo(f, MatType.CV_32F, 0.01);
        return f;
    }
}

/// <summary>Result of <see cref="DiffModel.Compare"/>.</summary>
public sealed class DiffResult
{
    internal DiffResult(Blob[] regions) => Regions = regions;

    /// <summary>The differing regions, largest first (draw them with <c>ctx.Draw</c>).</summary>
    public Blob[] Regions { get; }

    /// <summary>Area of the largest region (px²); 0 when none — judge with <c>ctx.Value(…, max: …)</c>.</summary>
    public double MaxArea => Regions.Length > 0 ? Regions[0].Area : 0;

    /// <summary>Total area of all regions (px²).</summary>
    public double TotalArea => Regions.Sum(r => r.Area);
}

// JSON: { "Path": "…" }. VisionSetup.Copy 중에는 학습 데이터를 공유
internal sealed class DiffModelConverter : JsonConverter<DiffModel>
{
    public override DiffModel? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("DiffModel must be a JSON object.");
        string? path = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString();
            reader.Read();
            if (string.Equals(name, "Path", StringComparison.OrdinalIgnoreCase) && reader.TokenType == JsonTokenType.String)
                path = reader.GetString();
            else
                reader.Skip();
        }
        return CopyShare.Active ? DiffModel.Shared(path, CopyShare.Next<DiffModel>()) : new DiffModel { Path = path };
    }

    public override void Write(Utf8JsonWriter writer, DiffModel value, JsonSerializerOptions options)
    {
        CopyShare.Wrote(value);
        writer.WriteStartObject();
        writer.WriteString("Path", value.Path);
        writer.WriteEndObject();
    }
}
