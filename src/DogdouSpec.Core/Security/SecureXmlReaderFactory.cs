using System.Xml;
using System.Xml.Schema;

namespace DogdouSpec.Core.Security;

/// <summary>
/// Factory for creating secure XmlReader instances that prohibit DTD and external resolution.
/// </summary>
public static class SecureXmlReaderFactory
{
    public const int DefaultMaxCharactersInDocument = 16 * 1024 * 1024; // 16 MiB limit

    /// <summary>
    /// Creates secure XmlReaderSettings with DTD and external resolution prohibited.
    /// </summary>
    public static XmlReaderSettings CreateSecureSettings(
        XmlSchemaSet? schemaSet = null,
        ValidationEventHandler? validationEventHandler = null,
        int maxCharacters = DefaultMaxCharactersInDocument)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = maxCharacters,
            MaxCharactersFromEntities = 1024,
            IgnoreWhitespace = false,
            IgnoreComments = false,
            IgnoreProcessingInstructions = false,
            CloseInput = true
        };

        if (schemaSet != null)
        {
            settings.ValidationType = ValidationType.Schema;
            settings.Schemas = schemaSet;
            settings.ValidationFlags = XmlSchemaValidationFlags.ReportValidationWarnings;

            if (validationEventHandler != null)
            {
                settings.ValidationEventHandler += validationEventHandler;
            }
        }
        else
        {
            settings.ValidationType = ValidationType.None;
        }

        return settings;
    }

    /// <summary>
    /// Creates a secure XmlReader for a Stream.
    /// </summary>
    public static XmlReader CreateReader(Stream stream, XmlReaderSettings? settings = null, string? baseUri = null)
    {
        var resolvedSettings = settings ?? CreateSecureSettings();
        return baseUri != null
            ? XmlReader.Create(stream, resolvedSettings, baseUri)
            : XmlReader.Create(stream, resolvedSettings);
    }

    /// <summary>
    /// Creates a secure XmlReader for a TextReader.
    /// </summary>
    public static XmlReader CreateReader(TextReader textReader, XmlReaderSettings? settings = null, string? baseUri = null)
    {
        var resolvedSettings = settings ?? CreateSecureSettings();
        return baseUri != null
            ? XmlReader.Create(textReader, resolvedSettings, baseUri)
            : XmlReader.Create(textReader, resolvedSettings);
    }
}
