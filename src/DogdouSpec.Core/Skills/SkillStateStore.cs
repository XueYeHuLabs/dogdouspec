using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DogdouSpec.Core.Resources;
using DogdouSpec.Core.Security;

namespace DogdouSpec.Core.Skills;

/// <summary>Portable collaboration metadata; summaries never authorize or prohibit synchronization.</summary>
public static class SkillStateStore
{
    public const string FileName = ".skill-state.xml";
    public const string ContentNormalization = "utf8-lf";
    internal static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public static string CliVersion => typeof(EmbeddedResources).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static string NormalizeContent(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    public static string Digest(byte[] bytes)
    {
        // Strict UTF-8 avoids treating undecodable local bytes as matching replacement characters.
        var text = new UTF8Encoding(false, true).GetString(bytes);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeContent(text))));
    }

    public static XDocument Read(string directory)
    {
        var path = Path.Combine(directory, FileName);
        CheckPath(directory, path);
        if (!File.Exists(path))
        {
            return new XDocument(new XElement("skill-state", new XAttribute("format_version", "1"), new XElement("history")));
        }
        using var reader = SecureXmlReaderFactory.CreateReader(File.OpenRead(path));
        var document = XDocument.Load(reader);
        if (document.Root?.Name != "skill-state" || (string?)document.Root.Attribute("format_version") != "1")
        {
            throw new XmlException($"Unsupported Skill state format in '{path}'.");
        }
        return document;
    }

    public static IReadOnlyDictionary<string, string>? ReadBaseline(XDocument document)
    {
        var baseline = document.Root?.Element("baseline");
        if (baseline == null || (string?)baseline.Attribute("content_normalization") != ContentNormalization)
        {
            return null;
        }
        var files = new Dictionary<string, string>(PathComparer);
        foreach (var file in baseline.Elements("file"))
        {
            var path = (string?)file.Attribute("path");
            var hash = (string?)file.Attribute("sha256");
            if (path == null || !IsRelativeFilePath(path) || PathComparer.Equals(path, FileName) || hash == null || hash.Length != 64 ||
                !hash.All(Uri.IsHexDigit) || !files.TryAdd(path, hash.ToLowerInvariant()))
            {
                return null;
            }
        }
        return files.Count == 0 ? null : files;
    }

    public static void SetBaseline(XDocument document, IEnumerable<string> writtenPaths)
    {
        var baseline = new XElement("baseline",
            new XAttribute("cli_version", CliVersion),
            new XAttribute("synced_at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
            new XAttribute("content_normalization", ContentNormalization),
            writtenPaths.Order(StringComparer.Ordinal).Select(path => new XElement("file",
                new XAttribute("path", path),
                new XAttribute("sha256", Digest(EmbeddedResources.GetSkillBytes(path)
                    ?? throw new IOException($"Embedded Skill file '{path}' is unavailable."))))));
        var root = document.Root!;
        root.Element("baseline")?.Remove();
        root.AddFirst(baseline);
    }

    public static XElement AddRecord(XDocument document, string summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        var history = document.Root!.Element("history");
        if (history == null)
        {
            history = new XElement("history");
            document.Root.Add(history);
        }
        var record = new XElement("record",
            new XAttribute("id", $"H-{Guid.NewGuid():N}"),
            new XAttribute("created_at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
            new XAttribute("cli_version", CliVersion),
            new XElement("summary", summary));
        history.Add(record);
        return record;
    }

    public static void Write(string directory, XDocument document)
    {
        var path = Path.Combine(directory, FileName);
        CheckPath(directory, path);
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var writer = XmlWriter.Create(temporaryPath, new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false), Indent = true, NewLineChars = "\n",
                NewLineHandling = NewLineHandling.Entitize
            }))
            {
                document.Save(writer);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    // Serialize writers to a shared ancestor Skill independently of workspace locks.
    public static IDisposable AcquireWriter(string directory) => new WriterLease(directory);

    public static void CheckPath(string directory, string path)
    {
        var (safe, error) = PathSecurity.CheckContainmentAndReparsePoints(Path.GetFullPath(directory), Path.GetFullPath(path));
        if (!safe || error != null) throw new IOException(error?.Message ?? $"Cannot access Skill path '{path}'.");
    }

    public static bool IsRelativeFilePath(string path) =>
        !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) && !path.Contains(':', StringComparison.Ordinal) &&
        !path.Contains('\\', StringComparison.Ordinal) &&
        path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..");

    private sealed class WriterLease : IDisposable
    {
        private readonly Mutex _mutex;

        public WriterLease(string directory)
        {
            var key = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            if (OperatingSystem.IsWindows()) key = key.ToUpperInvariant();
            _mutex = new Mutex(false, "DogdouSpec.Skill." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
            try { _mutex.WaitOne(); }
            catch (AbandonedMutexException) { /* Ownership was acquired; read the last published XML. */ }
        }

        public void Dispose()
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }
}
