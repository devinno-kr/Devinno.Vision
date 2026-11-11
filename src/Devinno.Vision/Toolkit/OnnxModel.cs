using Microsoft.ML.OnnxRuntime;

namespace Devinno.Vision;

/// <summary>
/// An ONNX model run on CPU with ONNX Runtime: give it an image, get its output tensors. A thin wrapper — for anything special, use ONNX Runtime directly.
/// </summary>
/// <remarks>
/// <para>The model must take one image input of shape [1, C, H, W] (float). The input is made with <see cref="VisionImage.ToTensor"/>:
/// <see cref="InputWidth"/> × <see cref="InputHeight"/> and <see cref="Format"/> must match how the model was trained.</para>
/// <para><see cref="Run"/> can be called from several threads at once. Dispose the model when done (it holds native memory).</para>
/// <para>A wrong input size, channel order or normalization gives no error — only worse results.</para>
/// </remarks>
/// <example>
/// <code>
/// using var model = OnnxModel.Load(@"C:\models\classifier.onnx", inputWidth: 224, inputHeight: 224, VisionTensorFormat.ImageNet);
///
/// OnnxOutputs o = model.Run(image);
/// OnnxTensor scores = o.Ordered[0];              // or o["output name"]; model.Outputs lists the names
/// int best = Array.IndexOf(scores.Data, scores.Data.Max());
/// </code>
/// </example>
public sealed class OnnxModel : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _input;

    private OnnxModel(InferenceSession session, string path, int width, int height, VisionTensorFormat format)
    {
        _session = session;
        Path = path;
        InputWidth = width;
        InputHeight = height;
        Format = format;
        _input = session.InputMetadata.Keys.First();
        Outputs = [.. session.OutputMetadata.Keys];
    }

    /// <summary>The model file.</summary>
    public string Path { get; }

    /// <summary>Input width the image is resized to (px).</summary>
    public int InputWidth { get; }

    /// <summary>Input height (px).</summary>
    public int InputHeight { get; }

    /// <summary>Channels and normalization of the input.</summary>
    public VisionTensorFormat Format { get; }

    /// <summary>Output names, in model order.</summary>
    public IReadOnlyList<string> Outputs { get; }

    /// <summary>Loads a model.</summary>
    /// <param name="path">The .onnx file.</param>
    /// <param name="inputWidth">Input width (px).</param>
    /// <param name="inputHeight">Input height (px).</param>
    /// <param name="format">Input channels and normalization; <see langword="null"/> = <see cref="VisionTensorFormat.ImageNet"/>.</param>
    /// <param name="threads">CPU threads for one run; 0 = all cores.</param>
    /// <returns>The model. Dispose it when done.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="Microsoft.ML.OnnxRuntime.OnnxRuntimeException">The file is not a model ONNX Runtime can load.</exception>
    public static OnnxModel Load(string path, int inputWidth, int inputHeight, VisionTensorFormat? format = null, int threads = 0)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
            throw new FileNotFoundException($"ONNX model not found: {path}", path);
        var options = new SessionOptions { IntraOpNumThreads = threads > 0 ? threads : Environment.ProcessorCount };
        try
        {
            return new OnnxModel(new InferenceSession(path, options), path, inputWidth, inputHeight, format ?? VisionTensorFormat.ImageNet);
        }
        finally
        {
            options.Dispose();
        }
    }

    /// <summary>Runs the model on an image.</summary>
    /// <param name="image">The image (resized and converted to the model input).</param>
    /// <returns>All outputs by name. The values are copied, so they stay valid after the model is disposed.</returns>
    /// <remarks>Outputs must be float tensors.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    /// <exception cref="Microsoft.ML.OnnxRuntime.OnnxRuntimeException">The model rejected the input (for example a fixed input size that differs from <see cref="InputWidth"/> × <see cref="InputHeight"/>).</exception>
    public OnnxOutputs Run(VisionImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var data = image.ToTensor(InputWidth, InputHeight, Format);
        using var input = OrtValue.CreateTensorValueFromMemory(data, [1, Format.Channels, InputHeight, InputWidth]);
        using var options = new RunOptions();
        using var results = _session.Run(options, [_input], [input], [.. Outputs]);
        var map = new Dictionary<string, OnnxTensor>();
        for (var i = 0; i < Outputs.Count; i++)
        {
            var info = results[i].GetTensorTypeAndShape();
            map[Outputs[i]] = new OnnxTensor(results[i].GetTensorDataAsSpan<float>().ToArray(), [.. info.Shape.Select(d => (int)d)]);
        }
        return new OnnxOutputs(map, Outputs);
    }

    /// <summary>Releases the native session.</summary>
    public void Dispose() => _session.Dispose();
}

/// <summary>One output tensor: values in row-major order and the shape (for an image model usually [1, C, H, W]).</summary>
/// <param name="Data">The values.</param>
/// <param name="Shape">The shape.</param>
public sealed record OnnxTensor(float[] Data, int[] Shape);

/// <summary>The outputs of one <see cref="OnnxModel.Run"/>, by name and in model order.</summary>
public sealed class OnnxOutputs
{
    private readonly Dictionary<string, OnnxTensor> _map;

    internal OnnxOutputs(Dictionary<string, OnnxTensor> map, IReadOnlyList<string> order)
    {
        _map = map;
        Ordered = [.. order.Select(n => map[n])];
    }

    /// <summary>The output with the given name.</summary>
    /// <param name="name">Output name (see <see cref="OnnxModel.Outputs"/>).</param>
    /// <exception cref="KeyNotFoundException">The model has no output with this name.</exception>
    public OnnxTensor this[string name] => _map.TryGetValue(name, out var t) ? t : throw new KeyNotFoundException($"No output named '{name}'. Outputs: {string.Join(", ", _map.Keys)}");

    /// <summary>The outputs in model order.</summary>
    public IReadOnlyList<OnnxTensor> Ordered { get; }
}
