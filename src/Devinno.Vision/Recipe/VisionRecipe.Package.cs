using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Devinno.Vision.Recipes;

namespace Devinno.Vision;

// 레시피 패키지 (.dvpkg) — 폴더를 zip 하나로 묶어 배포. 실행 코드(DLL)는 넣지 않는다
public sealed partial class VisionRecipe
{
    /// <summary>The package file extension.</summary>
    public const string PackageExtension = ".dvpkg";

    /// <summary>The manifest entry name in a package.</summary>
    public const string ManifestFile = "manifest.json";

    /// <summary>The package this recipe was loaded from, or <see langword="null"/> when it was loaded from a folder.
    /// <see cref="Folder"/> is then the folder the package was unpacked to; do not write into it.</summary>
    [JsonIgnore]
    public string? PackagePath { get; private set; }

    /// <summary>
    /// Writes the recipe as one package file (zip): <c>manifest.json</c> (library version, time, SHA-256 of every file), <c>studio.json</c> without the
    /// test folders, the base image and the model folders the recipe uses. Studio-only files (test folders, training folders and summaries) are left out.
    /// </summary>
    /// <param name="file">The package file (.dvpkg). Overwritten if it exists; its folder is created if missing.</param>
    /// <returns>The number of files packed (manifest not counted).</returns>
    /// <remarks>
    /// A package holds data and model files only — no code. <see cref="Load"/> opens it and checks every file against the manifest,
    /// so a package that was changed after packing is refused. Save the recipe before packing: the model folders are read from <see cref="Folder"/>.
    /// </remarks>
    /// <example>
    /// <code>
    /// using var recipe = VisionRecipe.Load(@"C:\recipes\plate");
    /// int files = recipe.Pack(@"C:\deploy\plate.dvpkg");
    ///
    /// // On the line PC
    /// using var onSite = VisionRecipe.Load(@"C:\deploy\plate.dvpkg");
    /// var job = onSite.CreateJob();
    /// </code>
    /// </example>
    /// <exception cref="ArgumentException"><paramref name="file"/> is empty or does not end in .dvpkg.</exception>
    /// <exception cref="ObjectDisposedException">The recipe was disposed.</exception>
    /// <exception cref="InvalidOperationException">The recipe cannot run as it is (no checks, a model to train again, an ONNX check without a model …).</exception>
    /// <exception cref="FileNotFoundException">A file the recipe needs is missing.</exception>
    public int Pack(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!file.EndsWith(PackageExtension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"패키지 파일은 {PackageExtension} 이어야 합니다: {file}", nameof(file));
        if (Checks.Count == 0 && GoodModel is null)
            throw new InvalidOperationException("레시피에 검사가 없습니다.");
        if (Problem() is { } problem)
            throw new InvalidOperationException(problem);
        if (!GoodModelIsCurrent)
            throw new InvalidOperationException("양품 모델을 다시 학습한 뒤에 패키지를 만드세요 (정렬 · 기준 위치 · 영역이 바뀌었습니다).");
        if (Checks.OfType<OnnxCheck>().FirstOrDefault(o => string.IsNullOrWhiteSpace(o.Model)) is { } untrained)
            throw new InvalidOperationException($"{untrained.Name}: 불량 모델이 없습니다 — 학습하거나 ONNX 를 가져온 뒤에 패키지를 만드세요.");

        // 담을 파일 (패키지 안 경로 → 원본)
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (BaseImagePath is { } baseImage)
            files[Rel(BaseImage!)] = baseImage;
        var models = new List<string>();
        if (GoodModel is { } g)
            models.Add(g.Path);
        models.AddRange(Checks.OfType<OnnxCheck>().Select(o => o.Model));
        foreach (var m in models.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var dir = Path.GetFullPath(Path.Combine(Folder, m));
            if (!Directory.Exists(dir))
                throw new FileNotFoundException($"모델 폴더가 없습니다: {dir}", dir);
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(f).Equals("train.json", StringComparison.OrdinalIgnoreCase))
                    continue;   // 학습 요약 — 엔지니어 PC 경로가 들어 있다
                files[Rel(Path.Combine(m, Path.GetRelativePath(dir, f)))] = f;
            }
        }

        // studio.json — 테스트 폴더(PC 경로)는 뺀다
        var recipeJson = JsonSerializer.SerializeToUtf8Bytes(new VisionRecipe
        {
            Name = Name, FormatVersion = Version(), BaseImage = BaseImage, Calibration = Calibration, GoodModel = GoodModel, Checks = Checks, TestSets = [],
        }, JsonOptions);

        var entries = new List<PackageEntry> { new(FileName, Hash(recipeJson), recipeJson.Length) };
        var tmp = file + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            using (var s = zip.CreateEntry(FileName).Open())
                s.Write(recipeJson);
            foreach (var (rel, src) in files)
            {
                var bytes = File.ReadAllBytes(src);
                using (var s = zip.CreateEntry(rel).Open())
                    s.Write(bytes);
                entries.Add(new PackageEntry(rel, Hash(bytes), bytes.Length));
            }
            var manifest = new PackageManifest(Name, LibraryVersion, DateTime.UtcNow, Version(), entries);
            using var m = zip.CreateEntry(ManifestFile).Open();
            JsonSerializer.Serialize(m, manifest, ManifestJson);
        }
        File.Move(tmp, file, overwrite: true);
        return entries.Count;

        static string Rel(string path) => path.Replace('\\', '/').TrimStart('/');
    }

    private int Version() => GoodModel is not null || Checks.Any(c => c.Role != StepRole.Judge || c is OnnxCheck) ? 2 : 1;

    // 패키지를 사용자 캐시 폴더에 풀고(내용 해시로 이름), 매니페스트의 해시로 확인한다. 풀린 폴더를 돌려준다
    private static string Unpack(string package)
    {
        var full = Path.GetFullPath(package);
        string key;
        using (var fs = File.OpenRead(full))
            key = Convert.ToHexString(SHA256.HashData(fs))[..16];
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Devinno.Vision", "packages", key);
        PackageManifest manifest;
        ZipArchive opened;
        try
        {
            opened = ZipFile.OpenRead(full);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"패키지(zip)를 읽을 수 없습니다: {full}", ex);
        }
        try
        {
            using var zip = opened;
            var me = zip.GetEntry(ManifestFile) ?? throw new InvalidDataException($"패키지에 {ManifestFile} 이 없습니다: {full}");
            using (var ms = me.Open())
                manifest = JsonSerializer.Deserialize<PackageManifest>(ms, ManifestJson) ?? throw new InvalidDataException($"{ManifestFile} 이 비었습니다: {full}");
            if (manifest.Files is not { Count: > 0 } || manifest.Files.All(f => f.Path != FileName))
                throw new InvalidDataException($"패키지에 {FileName} 이 없습니다: {full}");
            if (Directory.Exists(dir) && Verify(dir, manifest) is null)
                return dir;   // 이미 풀었고 그대로다
            var work = dir + ".tmp-" + Guid.NewGuid().ToString("N")[..6];
            Directory.CreateDirectory(work);
            try
            {
                foreach (var f in manifest.Files)
                {
                    var target = Path.GetFullPath(Path.Combine(work, f.Path));
                    if (!target.StartsWith(Path.GetFullPath(work) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"패키지에 잘못된 경로가 있습니다: {f.Path}");
                    var entry = zip.GetEntry(f.Path) ?? throw new InvalidDataException($"매니페스트의 파일이 패키지에 없습니다: {f.Path}");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
                if (Verify(work, manifest) is { } bad)
                    throw new InvalidDataException($"패키지가 손상되었거나 바뀌었습니다 (해시가 다름): {bad}");
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
                Directory.Move(work, dir);
            }
            catch
            {
                try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{ManifestFile} 을 읽을 수 없습니다: {ex.Message}", ex);
        }
        return dir;
    }

    // 해시가 다른 첫 파일 (모두 같으면 null)
    private static string? Verify(string dir, PackageManifest manifest)
    {
        foreach (var f in manifest.Files)
        {
            var path = Path.Combine(dir, f.Path);
            if (!File.Exists(path))
                return f.Path;
            using var fs = File.OpenRead(path);
            if (fs.Length != f.Size || !string.Equals(Convert.ToHexString(SHA256.HashData(fs)), f.Sha256, StringComparison.OrdinalIgnoreCase))
                return f.Path;
        }
        return null;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string LibraryVersion => typeof(VisionRecipe).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                                            ?? typeof(VisionRecipe).Assembly.GetName().Version?.ToString() ?? "";

    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

    private sealed record PackageEntry(string Path, string Sha256, long Size);

    private sealed record PackageManifest(string Name, string Library, DateTime Created, int FormatVersion, List<PackageEntry> Files);
}
