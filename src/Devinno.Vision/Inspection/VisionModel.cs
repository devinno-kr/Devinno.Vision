using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCvSharp;

namespace Devinno.Vision;

/// <summary>
/// A reference image kept in a setup. Only <see cref="Path"/> is saved in JSON; the image is read with <see cref="Load"/>.
/// </summary>
/// <remarks>
/// After a recipe change the old model is not disposed by the app: a running inspection may still use it, and the garbage collector frees it later
/// (the one exception to "images are not left to the GC").
/// </remarks>
[JsonConverter(typeof(VisionModelConverter))]
public sealed class VisionModel
{
    /// <summary>The image file path, relative to the root folder passed to <see cref="Load"/> (or absolute).</summary>
    public string? Path { get; set; }

    /// <summary>The loaded image, or <see langword="null"/> before <see cref="Load"/>. Owned by the model; do not dispose it.</summary>
    public VisionImage? Image { get; private set; }

    /// <summary>Reads the image file.</summary>
    /// <param name="root">The folder <see cref="Path"/> is relative to (for example the recipe folder).</param>
    /// <exception cref="InvalidOperationException"><see cref="Path"/> is not set.</exception>
    /// <exception cref="FileNotFoundException">The file cannot be read.</exception>
    public void Load(string root)
    {
        if (string.IsNullOrEmpty(Path))
            throw new InvalidOperationException("VisionModel.Path is not set.");
        Image = VisionImage.Load(System.IO.Path.Combine(root, Path));
    }

    /// <summary>Teaching: cuts a region out of a base image, saves it as a file and returns a model holding it.</summary>
    /// <param name="image">The base image.</param>
    /// <param name="roi">The region to keep.</param>
    /// <param name="root">The root folder (for example the recipe folder).</param>
    /// <param name="path">The file path relative to <paramref name="root"/> (use .png); folders are created.</param>
    /// <returns>A new model with <see cref="Path"/> and <see cref="Image"/> set. Put it in the setup copy and swap the setup in.</returns>
    public static VisionModel Teach(VisionImage image, VisionRoi roi, string root, string path)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var crop = image.Crop(roi);
        var copy = new VisionImage(crop.Mat.Clone(), image.Info, Affine.Identity, null);
        var full = System.IO.Path.Combine(root, path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(full))!);
        copy.Save(full);
        return new VisionModel { Path = path, Image = copy };
    }

    internal static VisionModel Shared(string? path, VisionImage? image) => new() { Path = path, Image = image };
}

// JSON: { "Path": "…" }. VisionSetup.Copy 중에는 이미지를 공유한다 (같은 순서로 쓰고 읽는다).
internal sealed class VisionModelConverter : JsonConverter<VisionModel>
{
    public override VisionModel? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("VisionModel must be a JSON object.");
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
        if (CopyShare.Active)
            return VisionModel.Shared(path, CopyShare.Next<VisionModel>()?.Image);
        return new VisionModel { Path = path };
    }

    public override void Write(Utf8JsonWriter writer, VisionModel value, JsonSerializerOptions options)
    {
        CopyShare.Wrote(value);
        writer.WriteStartObject();
        writer.WriteString("Path", value.Path);
        writer.WriteEndObject();
    }
}

/// <summary>Setup helpers.</summary>
public static class VisionSetup
{
    /// <summary>
    /// Copies a setup by a JSON round trip (<c>System.Text.Json</c>, default options). Reference images (<see cref="VisionModel"/>) are shared, not copied.
    /// </summary>
    /// <typeparam name="T">The app's setup class.</typeparam>
    /// <param name="setup">The setup in use.</param>
    /// <returns>A copy to edit; then replace the job's setup with it as a whole (the next run uses the new values).</returns>
    /// <remarks>A run keeps the setup reference it took when it started. Changing properties of the setup in use in place changes a running inspection too — edit a copy instead.</remarks>
    public static T Copy<T>(T setup) where T : class
    {
        ArgumentNullException.ThrowIfNull(setup);
        CopyShare.Begin();
        try
        {
            var json = JsonSerializer.Serialize(setup);
            return JsonSerializer.Deserialize<T>(json)!;
        }
        finally
        {
            CopyShare.End();
        }
    }
}
