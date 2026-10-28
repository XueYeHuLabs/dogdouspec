using System.Text;
using System.Xml;
using System.Xml.Linq;
using DogdouSpec.Core.Resources;

namespace DogdouSpec.Core.Skills;

public sealed record SkillFileStatus(string Path, string State, bool Managed, string Provenance,
    bool? LocalChanged, bool? EmbeddedChanged);

public sealed record SkillInspection(string Directory, XDocument Metadata, IReadOnlyList<SkillFileStatus> Files)
{
    public bool InSync => Files.All(file => file.State == "matching");
    public string? BaselineVersion => (string?)Metadata.Root?.Element("baseline")?.Attribute("cli_version");
    public bool HasBaseline => SkillStateStore.ReadBaseline(Metadata) != null;
    public int LocalChanges => Files.Count(file => file.LocalChanged == true);
    public int EmbeddedChanges => Files.Count(file => file.EmbeddedChanged == true);
    public string AutomaticUpgrade
    {
        get
        {
            var targets = Files.Where(file => file.Managed).ToList();
            if (targets.All(file => file.State == "matching")) return "not-needed";
            if (HasBaseline && Files.Where(file => file.Managed || file.Provenance == "retired")
                .All(file => file.EmbeddedChanged == false)) return "not-needed";
            if (targets.Any(file => file.State != "matching" && file.LocalChanged == true)) return "review-needed";
            if (!HasBaseline || targets.Any(file => file.State != "matching" && file.LocalChanged == null)) return "unknown";
            return "ready";
        }
    }

    public const int MaximumFileBytes = 1024 * 1024;

    public static SkillInspection Inspect(string directory)
    {
        directory = Path.GetFullPath(directory);
        var metadata = SkillStateStore.Read(directory);
        var baseline = SkillStateStore.ReadBaseline(metadata);
        var managed = new HashSet<string>(EmbeddedResources.SkillFilePaths, SkillStateStore.PathComparer);
        var statuses = new List<SkillFileStatus>();
        var paths = managed.Concat(baseline?.Keys ?? []).Distinct(SkillStateStore.PathComparer).Order(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var target = Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar));
            SkillStateStore.CheckPath(directory, target);
            var exists = File.Exists(target);
            var isManaged = managed.Contains(path);
            if (!isManaged && !exists) continue;
            string? localHash = null;
            if (exists)
            {
                var bytes = ReadFile(target);
                try { localHash = SkillStateStore.Digest(bytes); }
                catch (DecoderFallbackException) { localHash = "invalid-utf8"; }
            }
            var embeddedHash = isManaged ? SkillStateStore.Digest(EmbeddedResources.GetSkillBytes(path)!) : null;
            string? baselineHash = null;
            var tracked = baseline?.TryGetValue(path, out baselineHash) == true;
            var localChanged = tracked ? localHash != baselineHash : baseline != null ? exists : (bool?)null;
            var embeddedChanged = tracked ? embeddedHash != baselineHash : baseline != null ? isManaged : (bool?)null;
            var state = !isManaged ? "extra" : !exists ? "missing" : localHash == embeddedHash ? "matching" : "modified";
            var provenance = !isManaged ? "retired" : baseline == null ? "unknown-provenance" :
                localChanged == true ? "locally-modified" : !tracked ? "new" : embeddedChanged == true ? "outdated" : "pristine";
            statuses.Add(new SkillFileStatus(path, state, isManaged, provenance, localChanged, embeddedChanged));
        }

        if (System.IO.Directory.Exists(directory))
        {
            var known = new HashSet<string>(statuses.Select(file => file.Path), SkillStateStore.PathComparer);
            var pending = new Stack<string>();
            pending.Push(directory);
            var count = 0;
            while (pending.Count > 0)
            {
                foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    if (++count > 1024) throw new IOException("Skill directory exceeds the 1024-entry inspection limit.");
                    var relative = Path.GetRelativePath(directory, entry).Replace('\\', '/');
                    var attributes = File.GetAttributes(entry);
                    if (attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint)) pending.Push(entry);
                    else if (!known.Contains(relative) && !SkillStateStore.PathComparer.Equals(relative, SkillStateStore.FileName))
                        statuses.Add(new SkillFileStatus(relative, "extra", false, "unmanaged", null, null));
                }
            }
        }
        return new SkillInspection(directory, metadata, statuses.OrderBy(file => file.Managed ? 0 : 1)
            .ThenBy(file => file.Path, StringComparer.Ordinal).ToList());
    }

    public static byte[] ReadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumFileBytes) throw new IOException($"File '{path}' exceeds {MaximumFileBytes} bytes.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new IOException($"File '{path}' changed while reading.");
        return bytes;
    }

    public static string ReadText(string path)
    {
        path = Path.GetFullPath(path);
        try
        {
            var text = new UTF8Encoding(false, true).GetString(ReadFile(path));
            // Structured diff output must be able to represent the same text as unified output.
            XmlConvert.VerifyXmlChars(text);
            return text;
        }
        catch (DecoderFallbackException ex)
        {
            throw new IOException($"Cannot decode file '{path}' as UTF-8: {ex.Message}", ex);
        }
        catch (XmlException ex)
        {
            throw new IOException($"Cannot compare file '{path}' as XML-compatible text: {ex.Message}", ex);
        }
    }
}
