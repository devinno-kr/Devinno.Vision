namespace Devinno.Vision;

/// <summary>
/// A camera that returns the image files of a folder in name order — for replaying saved images and for tests. The same code that uses a real camera runs unchanged.
/// </summary>
public sealed class FileCamera : IVisionCamera
{
    private static readonly string[] s_extensions = [".png", ".bmp", ".jpg", ".jpeg", ".tif", ".tiff"];
    private readonly string[] _files;
    private readonly object _gate = new();
    private int _next;
    private long _frameNo;

    /// <summary>Opens a folder.</summary>
    /// <param name="folder">The folder with image files (.png, .bmp, .jpg, .tif).</param>
    /// <param name="loop">Start again from the first file after the last one; <see langword="false"/> = throw <see cref="InvalidOperationException"/> at the end.</param>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    /// <exception cref="FileNotFoundException">The folder has no image files.</exception>
    public FileCamera(string folder, bool loop = true)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"Folder not found: {folder}");
        _files = [.. Directory.EnumerateFiles(folder)
            .Where(f => s_extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)];
        if (_files.Length == 0)
            throw new FileNotFoundException($"No image files in {folder}");
        Loop = loop;
    }

    /// <summary>The files, in the order they are returned.</summary>
    public IReadOnlyList<string> Files => _files;

    /// <summary>The file of the last returned frame, or <see langword="null"/>.</summary>
    public string? LastFile { get; private set; }

    /// <summary>Whether it starts again after the last file.</summary>
    public bool Loop { get; }

    /// <summary>Accepted for compatibility; every mode returns the next file.</summary>
    public CameraTrigger Trigger { get; set; } = CameraTrigger.Software;

    /// <summary>Always <see cref="double.NaN"/>; setting throws <see cref="NotSupportedException"/>.</summary>
    public double ExposureUs { get => double.NaN; set => throw new NotSupportedException("A file camera has no exposure."); }

    /// <summary>Always <see cref="double.NaN"/>; setting throws <see cref="NotSupportedException"/>.</summary>
    public double Gain { get => double.NaN; set => throw new NotSupportedException("A file camera has no gain."); }

    /// <summary>Returns the next file as an image.</summary>
    /// <param name="ct">Not used (reading is immediate).</param>
    /// <returns>The image, owned by the caller.</returns>
    /// <exception cref="InvalidOperationException">The last file was already returned and <see cref="Loop"/> is off.</exception>
    public Task<VisionImage> Grab(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string file;
        long no;
        lock (_gate)
        {
            if (_next >= _files.Length)
            {
                if (!Loop)
                    throw new InvalidOperationException("No more files.");
                _next = 0;
            }
            file = _files[_next++];
            no = ++_frameNo;
        }
        using var loaded = VisionImage.Load(file);
        var img = new VisionImage(VisionImage.Share(loaded.Mat), new VisionImageInfo { Camera = "file", FrameNo = no }, Affine.Identity, null);
        LastFile = file;
        return Task.FromResult(img);
    }

    /// <summary>Same as <see cref="Grab"/>.</summary>
    /// <param name="ct">Not used.</param>
    /// <returns>The image, owned by the caller.</returns>
    public Task<VisionImage> WaitFrame(CancellationToken ct) => Grab(ct);

    /// <summary>Nothing to release.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
