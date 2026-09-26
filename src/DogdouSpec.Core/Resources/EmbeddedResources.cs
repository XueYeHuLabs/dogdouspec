using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Schema;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Security;

namespace DogdouSpec.Core.Resources;

/// <summary>
/// Catalog of versioned embedded schemas and templates.
/// </summary>
public static class EmbeddedResources
{
    private static readonly Assembly CoreAssembly = typeof(EmbeddedResources).Assembly;

    public static readonly IReadOnlyList<string> SupportedVersions = new[] { "1.0" };

    public static readonly IReadOnlyList<string> SchemaNames = new[]
    {
        "common",
        "spec",
        "tasks",
        "knowledge",
        "backlog",
        "requests"
    };

    public static readonly IReadOnlyList<string> TemplateNames = new[]
    {
        "backlog.item",
        "change.apply",
        "change.propose",
        "iteration.confirmation",
        "knowledge.entry",
        "record.discussion",
        "record.finding",
        "record.verification",
        "requirement.propose",
        "task.add",
        "task.revise",
        "task.review",
        "task.split",
        "task.update",
        "transaction.apply"
    };

    public static readonly IReadOnlyList<string> SkillFilePaths = new[]
    {
        "SKILL.md",
        "references/upgrade.md",
        "references/authority.md",
        "references/mutations.md",
        "references/xpath.md"
    };

    private static readonly ConcurrentDictionary<(string SchemaName, string Version), XmlSchemaSet> CachedSchemaSets = new();

    public static bool IsVersionSupported(string version) =>
        SupportedVersions.Contains(version);

    public static string NormalizeSchemaName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }
        return trimmed.ToLowerInvariant();
    }

    public static string NormalizeTemplateName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }
        return trimmed.ToLowerInvariant();
    }

    public static Stream? GetSchemaStream(string name, string version = "1.0")
    {
        if (!IsVersionSupported(version))
        {
            return null;
        }

        var normalizedName = NormalizeSchemaName(name);
        var resourceName = $"schemas.v1.{normalizedName}.xsd";
        return CoreAssembly.GetManifestResourceStream(resourceName);
    }

    public static byte[]? GetSchemaBytes(string name, string version = "1.0")
    {
        using var stream = GetSchemaStream(name, version);
        if (stream == null)
        {
            return null;
        }

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    public static string? GetSchemaText(string name, string version = "1.0")
    {
        var bytes = GetSchemaBytes(name, version);
        return bytes != null ? Encoding.UTF8.GetString(bytes) : null;
    }

    public static Stream? GetTemplateStream(string name, string version = "1.0")
    {
        if (!IsVersionSupported(version))
        {
            return null;
        }

        var normalizedName = NormalizeTemplateName(name);
        var resourceName = $"templates.v1.{normalizedName}.xml";
        return CoreAssembly.GetManifestResourceStream(resourceName);
    }

    public static byte[]? GetTemplateBytes(string name, string version = "1.0")
    {
        using var stream = GetTemplateStream(name, version);
        if (stream == null)
        {
            return null;
        }

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    public static string? GetTemplateText(string name, string version = "1.0")
    {
        var bytes = GetTemplateBytes(name, version);
        return bytes != null ? Encoding.UTF8.GetString(bytes) : null;
    }

    public static Stream? GetSkillStream(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var resourceName = $"skills.dogdouspec.{normalized.Replace('/', '.')}";
        return CoreAssembly.GetManifestResourceStream(resourceName);
    }

    public static string? GetSkillText(string relativePath)
    {
        var bytes = GetSkillBytes(relativePath);
        return bytes == null ? null : Encoding.UTF8.GetString(bytes);
    }

    public static byte[]? GetSkillBytes(string relativePath)
    {
        using var stream = GetSkillStream(relativePath);
        if (stream == null)
        {
            return null;
        }
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    public static string? GetAgentsTemplateText()
    {
        using var stream = CoreAssembly.GetManifestResourceStream("templates.v1.AGENTS.md");
        if (stream == null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static XmlSchemaSet GetCompiledSchemaSet(string schemaName, string version = "1.0")
    {
        if (!IsVersionSupported(version))
        {
            throw new ArgumentException($"Unsupported schema version '{version}'.", nameof(version));
        }

        var normalizedName = NormalizeSchemaName(schemaName);
        return CachedSchemaSets.GetOrAdd((normalizedName, version), key => CreateSchemaSet(key.SchemaName, key.Version));
    }

    private static XmlSchemaSet CreateSchemaSet(string schemaName, string version)
    {
        using var stream = GetSchemaStream(schemaName, version);
        if (stream == null)
        {
            throw new InvalidOperationException($"Embedded schema '{schemaName}' (version {version}) not found.");
        }

        var resolver = new EmbeddedResourceXmlResolver(version);
        var schemaSet = new XmlSchemaSet
        {
            XmlResolver = resolver
        };

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = resolver
        };

        using var reader = XmlReader.Create(stream, settings, $"file:///schemas/{schemaName}.xsd");
        schemaSet.Add(null, reader);
        schemaSet.Compile();
        return schemaSet;
    }

    private sealed class EmbeddedResourceXmlResolver : XmlResolver
    {
        private readonly string _version;

        public EmbeddedResourceXmlResolver(string version) =>
            _version = version;

        public override object? GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn)
        {
            var fileName = Path.GetFileName(absoluteUri.LocalPath);
            return GetSchemaStream(fileName, _version);
        }

        public override Uri ResolveUri(Uri? baseUri, string? relativeUri)
        {
            if (baseUri != null && !string.IsNullOrEmpty(relativeUri))
            {
                return new Uri(baseUri, relativeUri);
            }
            return new Uri($"file:///schemas/{relativeUri}");
        }
    }
}
