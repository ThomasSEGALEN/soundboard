using System.IO.Compression;
using System.Text.Json;

namespace Soundboard;

/// <summary>
/// Sound packs (.soundboard = zip with manifest.json + the audio files) and folder import.
/// Hotkeys are personal and are not exported.
/// </summary>
static class Library
{
    public static readonly string[] AudioExtensions = { ".wav", ".mp3", ".m4a", ".aac", ".wma", ".flac", ".aiff", ".aif" };

    public static bool IsAudio(string path) => AudioExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>A sound pack: ".soundboard", or ".soundmix" from before the app was renamed.</summary>
    public static bool IsPack(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".soundboard" or ".soundmix";

    /// <summary>Where imported packs are unpacked.</summary>
    public static string LibraryDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Soundboard", "library");

    // ------------------------------------------------------------ folder import

    /// <summary>
    /// Audio files of a folder and its subfolders. A file directly in the folder gets no category,
    /// a file in "Folder\Memes\x.mp3" (or deeper) gets the category "Memes" (first subfolder).
    /// </summary>
    public static List<(string File, string Category)> ScanFolder(string folder)
    {
        var result = new List<(string, string)>();
        foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(IsAudio).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            string rel = Path.GetRelativePath(folder, f);
            int sep = rel.IndexOfAny(new[] { '\\', '/' });
            result.Add((f, sep > 0 ? rel[..sep] : ""));
        }
        return result;
    }

    // ------------------------------------------------------------ packs

    sealed class Manifest
    {
        public int Version { get; set; } = 1;
        public string Name { get; set; } = "";
        public List<PackSound> Sounds { get; set; } = new();
    }

    sealed class PackSound
    {
        public string File { get; set; } = "";     // entry name inside the zip
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public float Volume { get; set; } = 1f;
        public ClickMode ClickMode { get; set; }
        public int Color { get; set; }
        public bool Favorite { get; set; }
        public double TrimStart { get; set; }
        public double TrimEnd { get; set; }
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Writes the sounds and their audio files into a .soundboard pack. Returns the number exported.</summary>
    public static int Export(IEnumerable<SoundEntry> sounds, string packPath, string packName)
    {
        var manifest = new Manifest { Name = packName };
        string tmp = packPath + ".tmp";
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            int i = 0;
            foreach (var s in sounds)
            {
                if (!File.Exists(s.Path)) continue;
                string entry = $"sounds/{++i:000}-{Sanitize(Path.GetFileName(s.Path))}";
                zip.CreateEntryFromFile(s.Path, entry, CompressionLevel.NoCompression); // audio is already compressed
                manifest.Sounds.Add(new PackSound
                {
                    File = entry, Name = s.Name, Category = s.Category, Volume = s.Volume, ClickMode = s.ClickMode,
                    Color = s.Color, Favorite = s.Favorite, TrimStart = s.TrimStart, TrimEnd = s.TrimEnd,
                });
            }
            var m = zip.CreateEntry("manifest.json");
            using var w = new StreamWriter(m.Open());
            w.Write(JsonSerializer.Serialize(manifest, Json));
        }
        File.Move(tmp, packPath, true);
        return manifest.Sounds.Count;
    }

    /// <summary>
    /// Unpacks a .soundboard pack into the library folder and returns the sounds to add
    /// (paths point at the unpacked files) and the pack name.
    /// </summary>
    public static (List<SoundEntry> Sounds, string Name) Import(string packPath, string? libraryDir = null)
    {
        using var zip = ZipFile.OpenRead(packPath);
        var me = zip.GetEntry("manifest.json") ?? throw new InvalidDataException(T("this file is not a Soundboard pack"));
        Manifest manifest;
        using (var r = new StreamReader(me.Open()))
            manifest = JsonSerializer.Deserialize<Manifest>(r.ReadToEnd()) ?? throw new InvalidDataException(T("unreadable pack"));

        string name = string.IsNullOrWhiteSpace(manifest.Name) ? Path.GetFileNameWithoutExtension(packPath) : manifest.Name;
        string baseDir = Path.Combine(libraryDir ?? LibraryDir, Sanitize(name));
        string dir = baseDir;
        for (int n = 2; Directory.Exists(dir); n++) dir = $"{baseDir} ({n})";
        Directory.CreateDirectory(dir);

        var result = new List<SoundEntry>();
        foreach (var ps in manifest.Sounds)
        {
            var entry = zip.GetEntry(ps.File);
            if (entry == null || !IsAudio(ps.File)) continue;
            // never trust paths from the pack: keep only the file name
            string target = Path.Combine(dir, Sanitize(Path.GetFileName(ps.File)));
            entry.ExtractToFile(target, overwrite: true);
            result.Add(new SoundEntry
            {
                Path = target, Name = ps.Name, Category = ps.Category ?? "", Volume = Math.Clamp(ps.Volume, 0f, 2f),
                ClickMode = Enum.IsDefined(ps.ClickMode) ? ps.ClickMode : ClickMode.Default,
                Color = ps.Color, Favorite = ps.Favorite, TrimStart = Math.Max(0, ps.TrimStart), TrimEnd = Math.Max(0, ps.TrimEnd),
            });
        }
        return (result, name);
    }

    static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        return name.Length == 0 ? "pack" : name;
    }
}
