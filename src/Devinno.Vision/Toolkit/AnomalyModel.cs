using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCvSharp;

namespace Devinno.Vision;

/// <summary>
/// Anomaly detection learned from good parts only (no defect images needed): a pretrained CNN describes each small patch of the image,
/// the patches of the good parts are kept per position, and a patch far from every good patch at its place is an anomaly — scratches, stains, bends, missing parts.
/// </summary>
/// <remarks>
/// <para><b>Feature model</b>: an ONNX model whose outputs are feature maps [1, C, h, w] (for example the layer2 and layer3 outputs of an ImageNet ResNet18;
/// <c>tools/export_features.py</c> makes one). Several outputs are smoothed (3×3), resampled to the finest grid and joined.</para>
/// <para><b>Alignment</b>: patches are compared at the same place (± <c>neighborhood</c> cells), so give aligned images of the same view —
/// normally the step's ROI (<c>ctx.Image</c>), which already follows the part's pose.</para>
/// <para><b>Size</b>: every position keeps only <c>representatives</c> patches (the most different ones), so the model stays tens of MB whatever the number of good images.
/// In a setup only <see cref="Path"/> is saved; <see cref="Save"/> writes the folder (model.json, features.onnx, bank.bin).</para>
/// <para><b>Limit</b>: the score has no fixed scale. Score good images that were not used for training and set the limit a little above their highest score.</para>
/// <para><b>Lifetime</b>: the model owns its feature model; disposing the model disposes it. <see cref="Score"/> does not change the model.
/// In a recipe, the good-part model (<see cref="VisionRecipe.TrainGoodModelAsync"/>) does the training, the limit and the loading for you.</para>
/// </remarks>
/// <example>
/// <code>
/// // Teaching: aligned crops of good parts (the same ROI of each)
/// var features = OnnxModel.Load(@"C:\models\features.onnx", 256, 256);
/// using var trained = AnomalyModel.Train(features, goodCrops);
/// trained.Save(recipeFolder, "models/surface");
///
/// // Startup
/// var model = new AnomalyModel { Path = "models/surface" };
/// model.Load(recipeFolder);
///
/// // A step whose ROI is the view used for training
/// job.Add("surface", roi: () =&gt; roi, ctx =&gt;
/// {
///     var a = model.Score(ctx.Image);
///     ctx.Draw(a);
///     ctx.Value("anomaly", a.Score, max: 2.3, code: "SURFACE");
/// });
/// </code>
/// </example>
[JsonConverter(typeof(AnomalyModelConverter))]
public sealed class AnomalyModel : IDisposable
{
    private OnnxModel? _features;
    private float[][][]? _bank;   // [칸][대표][D]

    /// <summary>The model folder, relative to the root passed to <see cref="Load"/> (or absolute).</summary>
    public string? Path { get; set; }

    /// <summary>Grid width (patch columns).</summary>
    public int GridWidth { get; private set; }

    /// <summary>Grid height (patch rows).</summary>
    public int GridHeight { get; private set; }

    /// <summary>Feature length per patch.</summary>
    public int Dimension { get; private set; }

    /// <summary>Patches kept per position.</summary>
    public int Representatives { get; private set; }

    /// <summary>Positions compared around each patch (± cells).</summary>
    public int Neighborhood { get; private set; }

    /// <summary>How many good images it was trained on.</summary>
    public int Count { get; private set; }

    /// <summary>Whether the model has data (trained or loaded).</summary>
    public bool IsReady => _bank is not null && _features is not null;

    /// <summary>Learns from good images.</summary>
    /// <param name="features">The feature model. The anomaly model keeps using it (do not dispose it while the model is in use).</param>
    /// <param name="goods">Aligned good images (20 or more; more good images = fewer false NG).</param>
    /// <param name="representatives">Patches kept per position (48 is plenty; more = larger and slower).</param>
    /// <param name="neighborhood">Compare with good patches up to this many cells away (absorbs small alignment errors).</param>
    /// <param name="progress">Reports the number of images described so far.</param>
    /// <param name="ct">Cancels the training.</param>
    /// <returns>The model.</returns>
    /// <exception cref="ArgumentException">Fewer than 2 good images, or the feature model gave different sizes for them.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="representatives"/> is below 1 or <paramref name="neighborhood"/> is negative.</exception>
    /// <exception cref="OperationCanceledException">Canceled.</exception>
    public static AnomalyModel Train(OnnxModel features, IEnumerable<VisionImage> goods, int representatives = 48, int neighborhood = 1,
                                     IProgress<int>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(goods);
        if (representatives < 1 || neighborhood < 0)
            throw new ArgumentOutOfRangeException(nameof(representatives), "representatives ≥ 1 and neighborhood ≥ 0.");
        var embeds = new List<float[][]>();
        int gw = 0, gh = 0, dim = 0;
        foreach (var g in goods)
        {
            ct.ThrowIfCancellationRequested();
            var (e, w, h, d) = Embed(features, g);
            if (embeds.Count > 0 && (w != gw || h != gh || d != dim))
                throw new ArgumentException("The feature model gave different sizes for the training images.");
            (gw, gh, dim) = (w, h, d);
            embeds.Add(e);
            progress?.Report(embeds.Count);
        }
        if (embeds.Count < 2)
            throw new ArgumentException("AnomalyModel needs at least 2 good images.", nameof(goods));

        var bank = new float[gw * gh][][];
        Parallel.For(0, gw * gh, new ParallelOptions { CancellationToken = ct }, p =>
        {
            int y = p / gw, x = p % gw;
            var cand = new List<float[]>();
            for (var dy = -neighborhood; dy <= neighborhood; dy++)
                for (var dx = -neighborhood; dx <= neighborhood; dx++)
                {
                    int yy = y + dy, xx = x + dx;
                    if (yy < 0 || xx < 0 || yy >= gh || xx >= gw) continue;
                    foreach (var e in embeds) cand.Add(e[yy * gw + xx]);
                }
            bank[p] = KCenter(cand, representatives);
        });
        return new AnomalyModel
        {
            _features = features, _bank = bank, GridWidth = gw, GridHeight = gh, Dimension = dim,
            Representatives = bank.Max(b => b.Length), Neighborhood = neighborhood, Count = embeds.Count,
        };
    }

    /// <summary>Scores an image: how far its most unusual patch is from the good patches at its place.</summary>
    /// <param name="image">An aligned image (same view as the training images).</param>
    /// <returns>Score (judge it with <c>ctx.Value(…, max: …)</c>; set the limit from good images not used for training), the worst region and the score map.</returns>
    /// <exception cref="InvalidOperationException">The model is not trained or loaded.</exception>
    public AnomalyResult Score(VisionImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (_bank is null || _features is null)
            throw new InvalidOperationException("The AnomalyModel is not trained or loaded.");
        var (e, w, h, d) = Embed(_features, image);
        if (w != GridWidth || h != GridHeight || d != Dimension)
            throw new InvalidOperationException($"The feature model gave {w}×{h}×{d}, the model was trained with {GridWidth}×{GridHeight}×{Dimension}.");
        var map = new float[w * h];
        var bank = _bank;
        Parallel.For(0, w * h, p =>
        {
            var best = float.MaxValue;
            foreach (var b in bank[p])
            {
                var dist = Dist(e[p], b);
                if (dist < best) best = dist;
            }
            map[p] = MathF.Sqrt(best);
        });
        var worst = 0;
        for (var p = 1; p < map.Length; p++)
            if (map[p] > map[worst]) worst = p;
        double cw = (double)image.Width / w, ch = (double)image.Height / h;
        var region = new Rect2d(worst % w * cw, worst / w * ch, cw, ch);
        return new AnomalyResult(map[worst], region, map, w, h, image.ToSourceTransform);
    }

    /// <summary>Saves the model to a folder (model.json, features.onnx, bank.bin) and sets <see cref="Path"/>.</summary>
    /// <param name="root">The root folder (for example the recipe folder).</param>
    /// <param name="path">The model folder relative to <paramref name="root"/>; created if missing.</param>
    /// <exception cref="InvalidOperationException">The model is not trained.</exception>
    public void Save(string root, string path)
    {
        if (_bank is null || _features is null)
            throw new InvalidOperationException("The AnomalyModel is not trained.");
        var dir = System.IO.Path.Combine(root, path);
        Directory.CreateDirectory(dir);
        var featureFile = System.IO.Path.Combine(dir, "features.onnx");
        if (!string.Equals(System.IO.Path.GetFullPath(_features.Path), System.IO.Path.GetFullPath(featureFile), StringComparison.OrdinalIgnoreCase))
            File.Copy(_features.Path, featureFile, overwrite: true);
        var info = new Info(GridWidth, GridHeight, Dimension, Representatives, Neighborhood, Count,
                            _features.InputWidth, _features.InputHeight, _features.Format);
        File.WriteAllText(System.IO.Path.Combine(dir, "model.json"), JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
        using (var w = new BinaryWriter(File.Create(System.IO.Path.Combine(dir, "bank.bin"))))
        {
            // 형식 2: 칸마다 대표 수를 먼저 쓴다 (양품이 적으면 모서리 · 가장자리 칸은 대표가 더 적다)
            w.Write("DVAB"u8); w.Write(2); w.Write(GridWidth); w.Write(GridHeight); w.Write(Dimension); w.Write(Representatives);
            var buf = new byte[Dimension * 4];
            foreach (var pos in _bank)
            {
                w.Write(pos.Length);
                foreach (var v in pos)
                {
                    Buffer.BlockCopy(v, 0, buf, 0, buf.Length);
                    w.Write(buf);
                }
            }
        }
        Path = path;
    }

    /// <summary>Reads the model folder at <see cref="Path"/> (including its feature model).</summary>
    /// <param name="root">The folder <see cref="Path"/> is relative to.</param>
    /// <exception cref="InvalidOperationException"><see cref="Path"/> is not set.</exception>
    /// <exception cref="FileNotFoundException">A file of the model folder (model.json, bank.bin, features.onnx) is missing.</exception>
    /// <exception cref="DirectoryNotFoundException">The model folder does not exist.</exception>
    /// <exception cref="InvalidDataException">The folder is not an anomaly model, or bank.bin has an unknown format.</exception>
    public void Load(string root)
    {
        if (string.IsNullOrEmpty(Path))
            throw new InvalidOperationException("AnomalyModel.Path is not set.");
        var dir = System.IO.Path.Combine(root, Path);
        var info = JsonSerializer.Deserialize<Info>(File.ReadAllText(System.IO.Path.Combine(dir, "model.json")))
            ?? throw new InvalidDataException($"Not an AnomalyModel folder: {dir}");
        using var r = new BinaryReader(File.OpenRead(System.IO.Path.Combine(dir, "bank.bin")));
        // 형식 1: 모든 칸의 대표 수가 헤더의 k. 형식 2: 칸마다 대표 수가 앞에 있다
        var version = r.ReadBytes(4).AsSpan().SequenceEqual("DVAB"u8) ? r.ReadInt32() : 0;
        if (version is not (1 or 2))
            throw new InvalidDataException($"bank.bin has an unknown format: {dir}");
        int gw = r.ReadInt32(), gh = r.ReadInt32(), dim = r.ReadInt32(), k = r.ReadInt32();
        var bank = new float[gw * gh][][];
        var buf = new byte[dim * 4];
        try
        {
            for (var p = 0; p < bank.Length; p++)
            {
                var n = version == 1 ? k : r.ReadInt32();
                if (n < 1 || n > k)
                    throw new InvalidDataException($"bank.bin is damaged: {dir}");
                bank[p] = new float[n][];
                for (var i = 0; i < n; i++)
                {
                    r.BaseStream.ReadExactly(buf);
                    var v = new float[dim];
                    Buffer.BlockCopy(buf, 0, v, 0, buf.Length);
                    bank[p][i] = v;
                }
            }
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException($"bank.bin is damaged: {dir}");
        }
        // 형식 1 로 저장된, 칸마다 대표 수가 다른 모델은 읽는 위치가 어긋난다 (남는 데이터로 드러난다)
        if (r.BaseStream.Position != r.BaseStream.Length)
            throw new InvalidDataException($"bank.bin is damaged (train the model again): {dir}");
        var features = OnnxModel.Load(System.IO.Path.Combine(dir, "features.onnx"), info.InputWidth, info.InputHeight, info.Format);
        _features?.Dispose();
        (_features, _bank) = (features, bank);
        (GridWidth, GridHeight, Dimension, Representatives, Neighborhood, Count) = (gw, gh, dim, k, info.Neighborhood, info.Count);
    }

    /// <summary>Releases the feature model.</summary>
    public void Dispose() => _features?.Dispose();

    internal static AnomalyModel Shared(string? path, AnomalyModel? from) => from is null
        ? new AnomalyModel { Path = path }
        : new AnomalyModel { Path = path, _features = from._features, _bank = from._bank, GridWidth = from.GridWidth, GridHeight = from.GridHeight,
                             Dimension = from.Dimension, Representatives = from.Representatives, Neighborhood = from.Neighborhood, Count = from.Count };

    private sealed record Info(int GridWidth, int GridHeight, int Dimension, int Representatives, int Neighborhood, int Count,
                               int InputWidth, int InputHeight, VisionTensorFormat Format);

    // 특징 맵들 → 칸마다 특징 벡터 (3×3 평균 → 가장 촘촘한 격자로 맞춰 이어 붙임)
    private static (float[][] Embed, int W, int H, int D) Embed(OnnxModel features, VisionImage image)
    {
        var outs = features.Run(image).Ordered;
        foreach (var t in outs)
            if (t.Shape.Length != 4 || t.Shape[0] != 1)
                throw new InvalidOperationException($"AnomalyModel needs feature maps [1, C, h, w]; the model gave [{string.Join(", ", t.Shape)}].");
        var finest = outs.OrderByDescending(t => t.Shape[2] * t.Shape[3]).First();
        int gh = finest.Shape[2], gw = finest.Shape[3];
        var dim = outs.Sum(t => t.Shape[1]);
        var embed = new float[gw * gh][];
        for (var p = 0; p < embed.Length; p++)
            embed[p] = new float[dim];
        var offset = 0;
        foreach (var t in outs)
        {
            int c = t.Shape[1], h = t.Shape[2], w = t.Shape[3];
            var pooled = Pool3(t.Data, c, h, w);
            for (var y = 0; y < gh; y++)
            {
                var sy = (y + 0.5f) * h / gh - 0.5f;
                for (var x = 0; x < gw; x++)
                {
                    var sx = (x + 0.5f) * w / gw - 0.5f;
                    var v = embed[y * gw + x];
                    for (var ch = 0; ch < c; ch++)
                        v[offset + ch] = Bilinear(pooled, ch, h, w, sy, sx);
                }
            }
            offset += c;
        }
        return (embed, gw, gh, dim);
    }

    private static float[] Pool3(float[] f, int c, int h, int w)
    {
        var o = new float[f.Length];
        for (var ch = 0; ch < c; ch++)
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    float s = 0; var k = 0;
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            int yy = y + dy, xx = x + dx;
                            if (yy < 0 || xx < 0 || yy >= h || xx >= w) continue;
                            s += f[(ch * h + yy) * w + xx];
                            k++;
                        }
                    o[(ch * h + y) * w + x] = s / k;
                }
        return o;
    }

    private static float Bilinear(float[] f, int ch, int h, int w, float y, float x)
    {
        y = Math.Clamp(y, 0, h - 1);
        x = Math.Clamp(x, 0, w - 1);
        int y0 = (int)y, x0 = (int)x, y1 = Math.Min(y0 + 1, h - 1), x1 = Math.Min(x0 + 1, w - 1);
        float wy = y - y0, wx = x - x0;
        float V(int yy, int xx) => f[(ch * h + yy) * w + xx];
        return (1 - wy) * ((1 - wx) * V(y0, x0) + wx * V(y0, x1)) + wy * ((1 - wx) * V(y1, x0) + wx * V(y1, x1));
    }

    // 가장 먼 것부터 고르는 k-center: 드문 모양의 양품 패치를 남긴다
    private static float[][] KCenter(List<float[]> cand, int k)
    {
        k = Math.Min(k, cand.Count);
        var pick = new List<float[]>(k) { cand[0] };
        var near = new float[cand.Count];
        for (var i = 0; i < near.Length; i++) near[i] = Dist(cand[i], cand[0]);
        while (pick.Count < k)
        {
            var far = 0;
            for (var i = 1; i < near.Length; i++)
                if (near[i] > near[far]) far = i;
            pick.Add(cand[far]);
            for (var i = 0; i < near.Length; i++)
                near[i] = Math.Min(near[i], Dist(cand[i], cand[far]));
        }
        return [.. pick];
    }

    private static float Dist(float[] a, float[] b)
    {
        var n = Vector<float>.Count;
        var acc = Vector<float>.Zero;
        var i = 0;
        for (; i <= a.Length - n; i += n)
        {
            var d = new Vector<float>(a, i) - new Vector<float>(b, i);
            acc += d * d;
        }
        var s = Vector.Sum(acc);
        for (; i < a.Length; i++)
        {
            var d = a[i] - b[i];
            s += d * d;
        }
        return s;
    }
}

/// <summary>Result of <see cref="AnomalyModel.Score"/>.</summary>
public sealed class AnomalyResult
{
    internal AnomalyResult(double score, Rect2d region, float[] map, int gridWidth, int gridHeight, Affine toSource)
    {
        Score = score;
        Region = region;
        Map = map;
        GridWidth = gridWidth;
        GridHeight = gridHeight;
        ToSource = toSource;
    }

    /// <summary>The anomaly score of the most unusual patch (distance to the nearest good patch at its place).</summary>
    public double Score { get; }

    /// <summary>The most unusual patch, in the coordinates of the scored image (draw it with <c>ctx.Draw(result)</c>).</summary>
    public Rect2d Region { get; }

    /// <summary>Score per patch, row by row (<see cref="GridWidth"/> × <see cref="GridHeight"/>).</summary>
    public float[] Map { get; }

    /// <summary>Grid width.</summary>
    public int GridWidth { get; }

    /// <summary>Grid height.</summary>
    public int GridHeight { get; }

    internal Affine ToSource { get; }
}

// JSON: { "Path": "…" }. VisionSetup.Copy 중에는 모델을 공유
internal sealed class AnomalyModelConverter : JsonConverter<AnomalyModel>
{
    public override AnomalyModel? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("AnomalyModel must be a JSON object.");
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
        return CopyShare.Active ? AnomalyModel.Shared(path, CopyShare.Next<AnomalyModel>()) : new AnomalyModel { Path = path };
    }

    public override void Write(Utf8JsonWriter writer, AnomalyModel value, JsonSerializerOptions options)
    {
        CopyShare.Wrote(value);
        writer.WriteStartObject();
        writer.WriteString("Path", value.Path);
        writer.WriteEndObject();
    }
}
