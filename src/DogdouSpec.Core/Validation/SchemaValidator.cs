using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Resources;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Validation;

/// <summary>
/// Authoritative validator that validates managed documents against embedded XSDs and semantic rules.
/// </summary>
public static class SchemaValidator
{
    public static DocumentValidationResult ValidateDocument(
        ManagedDocument document,
        string version = "1.0")
    {
        var diagnostics = new List<Diagnostic>();

        var schemaName = DocumentSchemaMapper.GetSchemaNameForDocument(document);
        if (schemaName == null)
        {
            diagnostics.Add(Diagnostic.Error(
                DiagnosticCodes.UnknownDocumentType,
                $"Could not determine schema for document '{document.RelativePath}'.",
                document.RelativePath));
            return new DocumentValidationResult(document.RelativePath, false, diagnostics);
        }

        var resolvedSchemaSet = EmbeddedResources.GetCompiledSchemaSet(schemaName, version);

        var settings = SecureXmlReaderFactory.CreateSecureSettings(
            schemaSet: resolvedSchemaSet,
            validationEventHandler: (sender, args) =>
            {
                var line = args.Exception?.LineNumber;
                var col = args.Exception?.LinePosition;
                var code = DiagnosticCodes.SchemaValidationError;

                var diag = args.Severity == XmlSeverityType.Error
                    ? Diagnostic.Error(code, args.Message, document.RelativePath, line, col)
                    : Diagnostic.Warning(code, args.Message, document.RelativePath, line, col);

                diagnostics.Add(diag);
            });

        try
        {
            using var stream = File.OpenRead(document.FullPath);
            using var reader = SecureXmlReaderFactory.CreateReader(stream, settings);

            while (reader.Read())
            {
                // Reading triggers schema validation handlers
            }
        }
        catch (XmlException xmlEx)
        {
            string code;
            if (xmlEx.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase))
            {
                code = DiagnosticCodes.DtdProhibited;
            }
            else if (xmlEx.Message.Contains("characters in the document", StringComparison.OrdinalIgnoreCase) ||
                     xmlEx.Message.Contains("MaxCharactersInDocument", StringComparison.OrdinalIgnoreCase) ||
                     (xmlEx.Message.Contains("limit", StringComparison.OrdinalIgnoreCase) && xmlEx.Message.Contains("exceeded", StringComparison.OrdinalIgnoreCase)))
            {
                code = DiagnosticCodes.LimitExceeded;
            }
            else
            {
                code = DiagnosticCodes.XmlParseError;
            }

            diagnostics.Add(Diagnostic.Error(
                code,
                xmlEx.Message,
                document.RelativePath,
                xmlEx.LineNumber,
                xmlEx.LinePosition));
        }
        catch (Exception ex)
        {
            diagnostics.Add(Diagnostic.Error(
                DiagnosticCodes.XmlParseError,
                $"Failed to read XML document: {ex.Message}",
                document.RelativePath));
        }

        var hasErrors = diagnostics.Any(d => d.Severity == "error");
        return new DocumentValidationResult(document.RelativePath, !hasErrors, diagnostics);
    }

    public static ValidationResult Validate(
        string workspaceRoot,
        string? iterationId = null,
        string? relativeDocumentPath = null,
        string version = "1.0")
    {
        var scope = !string.IsNullOrWhiteSpace(relativeDocumentPath)
            ? "document"
            : !string.IsNullOrWhiteSpace(iterationId)
                ? "iteration"
                : "workspace";

        if (!EmbeddedResources.IsVersionSupported(version))
        {
            var diag = Diagnostic.Error(
                DiagnosticCodes.UnsupportedVersion,
                $"Schema version '{version}' is not supported. Supported versions: {string.Join(", ", EmbeddedResources.SupportedVersions)}.");
            return new ValidationResult(false, scope, 0, Array.Empty<DocumentValidationResult>(), new[] { diag }, iterationId, relativeDocumentPath);
        }

        var (enumSuccess, documents, enumDiagnostics) = WorkspaceDiscovery.EnumerateDocuments(
            workspaceRoot,
            iterationId,
            relativeDocumentPath);

        if (!enumSuccess || enumDiagnostics.Count > 0)
        {
            var sortedEnumDiags = SortDiagnostics(enumDiagnostics);
            return new ValidationResult(false, scope, documents.Count, Array.Empty<DocumentValidationResult>(), sortedEnumDiags, iterationId, relativeDocumentPath);
        }

        // 1. Run XSD schema validation on target scope documents
        var targetDocResults = new List<DocumentValidationResult>();
        var targetScopeDiagnostics = new List<Diagnostic>();

        foreach (var doc in documents)
        {
            var docResult = ValidateDocumentSchemaOnly(doc, version: version);
            targetDocResults.Add(docResult);
            targetScopeDiagnostics.AddRange(docResult.Diagnostics);
        }

        var hasTargetSchemaErrors = targetDocResults.Any(r => !r.IsValid) || targetScopeDiagnostics.Any(d => d.Severity == "error");
        if (hasTargetSchemaErrors)
        {
            var sortedDiags = SortDiagnostics(targetScopeDiagnostics);
            return new ValidationResult(
                false,
                scope,
                documents.Count,
                targetDocResults,
                sortedDiags,
                iterationId,
                relativeDocumentPath);
        }

        // 3. Whole workspace vs Scoped validation
        if (scope == "workspace")
        {
            var parsedDocs = new List<(ManagedDocument Document, XDocument XDoc)>();
            foreach (var doc in documents)
            {
                try
                {
                    using var stream = File.OpenRead(doc.FullPath);
                    using var reader = SecureXmlReaderFactory.CreateReader(stream);
                    var xDoc = XDocument.Load(reader, LoadOptions.SetLineInfo);
                    parsedDocs.Add((doc, xDoc));
                }
                catch (Exception ex)
                {
                    var diag = Diagnostic.Error(
                        DiagnosticCodes.XmlParseError,
                        $"Failed to parse XML document: {ex.Message}",
                        doc.RelativePath);
                    return new ValidationResult(
                        false,
                        scope,
                        documents.Count,
                        targetDocResults,
                        new[] { diag },
                        iterationId,
                        relativeDocumentPath);
                }
            }

            var index = ProjectSemanticIndex.Build(parsedDocs);
            var semanticDiagnostics = SemanticValidator.Validate(index);

            var allDiagnostics = new List<Diagnostic>(targetScopeDiagnostics);
            allDiagnostics.AddRange(semanticDiagnostics);

            var sortedAllDiagnostics = SortDiagnostics(allDiagnostics);
            var overallValid = !sortedAllDiagnostics.Any(d => d.Severity == "error");

            return new ValidationResult(
                overallValid,
                scope,
                documents.Count,
                targetDocResults,
                sortedAllDiagnostics,
                iterationId,
                relativeDocumentPath);
        }
        else
        {
            // Scoped validation (iteration or document):
            // Check that the full workspace context needed for project semantic indexing is complete and schema-valid.
            var targetAttrDoc = relativeDocumentPath ?? (documents.Count > 0 ? documents[0].RelativePath : null);

            var (allEnumSuccess, allWorkspaceDocs, allEnumDiags) = WorkspaceDiscovery.EnumerateDocuments(workspaceRoot);
            if (!allEnumSuccess || allEnumDiags.Count > 0)
            {
                var detail = string.Join("; ", allEnumDiags.Select(d => $"{d.Document ?? "workspace"}: {d.Message}"));
                var contextDiag = Diagnostic.Error(
                    DiagnosticCodes.SemanticContextIncomplete,
                    $"Scoped semantic validation for {scope} '{iterationId ?? relativeDocumentPath}' failed because non-target workspace context is incomplete or malformed: {detail}.",
                    targetAttrDoc);

                return new ValidationResult(
                    false,
                    scope,
                    documents.Count,
                    targetDocResults,
                    new[] { contextDiag },
                    iterationId,
                    relativeDocumentPath);
            }

            var targetDocMap = documents.ToDictionary(d => d.FullPath, StringComparer.OrdinalIgnoreCase);
            var invalidNonTargetDocs = new List<string>();
            var parsedDocs = new List<(ManagedDocument Document, XDocument XDoc)>();

            foreach (var doc in allWorkspaceDocs)
            {
                if (targetDocMap.ContainsKey(doc.FullPath))
                {
                    try
                    {
                        using var stream = File.OpenRead(doc.FullPath);
                        using var reader = SecureXmlReaderFactory.CreateReader(stream);
                        var xDoc = XDocument.Load(reader, LoadOptions.SetLineInfo);
                        parsedDocs.Add((doc, xDoc));
                    }
                    catch (Exception ex)
                    {
                        invalidNonTargetDocs.Add($"{doc.RelativePath} ({ex.Message})");
                    }
                }
                else
                {
                    var checkResult = ValidateDocumentSchemaOnly(doc, version: version);
                    if (!checkResult.IsValid)
                    {
                        invalidNonTargetDocs.Add(doc.RelativePath);
                    }
                    else
                    {
                        try
                        {
                            using var stream = File.OpenRead(doc.FullPath);
                            using var reader = SecureXmlReaderFactory.CreateReader(stream);
                            var xDoc = XDocument.Load(reader, LoadOptions.SetLineInfo);
                            parsedDocs.Add((doc, xDoc));
                        }
                        catch
                        {
                            invalidNonTargetDocs.Add(doc.RelativePath);
                        }
                    }
                }
            }

            if (invalidNonTargetDocs.Count > 0)
            {
                var contextDiag = Diagnostic.Error(
                    DiagnosticCodes.SemanticContextIncomplete,
                    $"Scoped semantic validation for {scope} '{iterationId ?? relativeDocumentPath}' failed because non-target workspace document(s) are schema-invalid or unavailable: {string.Join(", ", invalidNonTargetDocs)}. Fix non-target documents or run full workspace validation.",
                    targetAttrDoc);

                return new ValidationResult(
                    false,
                    scope,
                    documents.Count,
                    targetDocResults,
                    new[] { contextDiag },
                    iterationId,
                    relativeDocumentPath);
            }

            var index = ProjectSemanticIndex.Build(parsedDocs);
            var semanticDiagnostics = SemanticValidator.Validate(
                index,
                iterationFilter: iterationId,
                documentFilter: relativeDocumentPath);

            var allDiagnostics = new List<Diagnostic>(targetScopeDiagnostics);
            allDiagnostics.AddRange(semanticDiagnostics);

            var sortedAllDiagnostics = SortDiagnostics(allDiagnostics);
            var overallValid = !sortedAllDiagnostics.Any(d => d.Severity == "error");

            return new ValidationResult(
                overallValid,
                scope,
                documents.Count,
                targetDocResults,
                sortedAllDiagnostics,
                iterationId,
                relativeDocumentPath);
        }
    }

    private static DocumentValidationResult ValidateDocumentSchemaOnly(
        ManagedDocument document,
        string version = "1.0")
    {
        var diagnostics = new List<Diagnostic>();

        var schemaName = DocumentSchemaMapper.GetSchemaNameForDocument(document);
        if (schemaName == null)
        {
            diagnostics.Add(Diagnostic.Error(
                DiagnosticCodes.UnknownDocumentType,
                $"Could not determine schema for document '{document.RelativePath}'.",
                document.RelativePath));
            return new DocumentValidationResult(document.RelativePath, false, diagnostics);
        }

        var resolvedSchemaSet = EmbeddedResources.GetCompiledSchemaSet(schemaName, version);

        var settings = SecureXmlReaderFactory.CreateSecureSettings(
            schemaSet: resolvedSchemaSet,
            validationEventHandler: (sender, args) =>
            {
                var line = args.Exception?.LineNumber;
                var col = args.Exception?.LinePosition;
                var code = DiagnosticCodes.SchemaValidationError;

                var diag = args.Severity == XmlSeverityType.Error
                    ? Diagnostic.Error(code, args.Message, document.RelativePath, line, col)
                    : Diagnostic.Warning(code, args.Message, document.RelativePath, line, col);

                diagnostics.Add(diag);
            });

        try
        {
            using var stream = File.OpenRead(document.FullPath);
            using var reader = SecureXmlReaderFactory.CreateReader(stream, settings);

            while (reader.Read())
            {
                // Reading triggers schema validation handlers
            }
        }
        catch (XmlException xmlEx)
        {
            string code;
            if (xmlEx.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase))
            {
                code = DiagnosticCodes.DtdProhibited;
            }
            else if (xmlEx.Message.Contains("characters in the document", StringComparison.OrdinalIgnoreCase) ||
                     xmlEx.Message.Contains("MaxCharactersInDocument", StringComparison.OrdinalIgnoreCase) ||
                     (xmlEx.Message.Contains("limit", StringComparison.OrdinalIgnoreCase) && xmlEx.Message.Contains("exceeded", StringComparison.OrdinalIgnoreCase)))
            {
                code = DiagnosticCodes.LimitExceeded;
            }
            else
            {
                code = DiagnosticCodes.XmlParseError;
            }

            diagnostics.Add(Diagnostic.Error(
                code,
                xmlEx.Message,
                document.RelativePath,
                xmlEx.LineNumber,
                xmlEx.LinePosition));
        }
        catch (Exception ex)
        {
            diagnostics.Add(Diagnostic.Error(
                DiagnosticCodes.XmlParseError,
                $"Failed to read XML document: {ex.Message}",
                document.RelativePath));
        }

        var hasErrors = diagnostics.Any(d => d.Severity == "error");
        return new DocumentValidationResult(document.RelativePath, !hasErrors, diagnostics);
    }

    /// <summary>
    /// Validates prospective new or modified documents against authoritative XSDs and the full prospective workspace semantic model.
    /// Does not require or perform live file writes.
    /// </summary>
    public static ValidationResult ValidateProspective(
        string workspaceRoot,
        IReadOnlyList<ProspectiveDocument> prospectiveDocuments,
        string version = "1.0")
    {
        var diagnostics = new List<Diagnostic>();
        var targetDocResults = new List<DocumentValidationResult>();

        if (!EmbeddedResources.IsVersionSupported(version))
        {
            var diag = Diagnostic.Error(
                DiagnosticCodes.UnsupportedVersion,
                $"Schema version '{version}' is not supported. Supported versions: {string.Join(", ", EmbeddedResources.SupportedVersions)}.");
            return new ValidationResult(false, "workspace", prospectiveDocuments.Count, Array.Empty<DocumentValidationResult>(), new[] { diag });
        }

        var prospectiveMap = new Dictionary<string, (ProspectiveDocument Doc, XDocument XDoc, ManagedDocument ManagedDoc)>(StringComparer.OrdinalIgnoreCase);

        // 1. Validate XSD schema of each prospective document
        foreach (var pDoc in prospectiveDocuments)
        {
            var (isValidPath, normalizedRelPath, pathErr) = PathSecurity.ValidateRelativeDocumentPath(pDoc.RelativePath);
            if (!isValidPath || pathErr != null)
            {
                diagnostics.Add(pathErr!);
                continue;
            }

            string? iterId = null;
            var segs = normalizedRelPath.Split('/');
            if (segs.Length > 1)
            {
                iterId = segs[0];
            }

            var fullFakePath = Path.Combine(workspaceRoot, normalizedRelPath.Replace('/', Path.DirectorySeparatorChar));
            var managedDoc = new ManagedDocument(normalizedRelPath, fullFakePath, iterId);

            var schemaName = DocumentSchemaMapper.GetSchemaNameForDocument(managedDoc);
            if (schemaName == null)
            {
                var unknownDiag = Diagnostic.Error(
                    DiagnosticCodes.UnknownDocumentType,
                    $"Could not determine schema for document '{pDoc.RelativePath}'.",
                    pDoc.RelativePath);
                diagnostics.Add(unknownDiag);
                targetDocResults.Add(new DocumentValidationResult(pDoc.RelativePath, false, new[] { unknownDiag }));
                continue;
            }

            var resolvedSchemaSet = EmbeddedResources.GetCompiledSchemaSet(schemaName, version);
            var docDiagnostics = new List<Diagnostic>();

            var settings = SecureXmlReaderFactory.CreateSecureSettings(
                schemaSet: resolvedSchemaSet,
                validationEventHandler: (sender, args) =>
                {
                    var line = args.Exception?.LineNumber;
                    var col = args.Exception?.LinePosition;
                    var code = DiagnosticCodes.SchemaValidationError;

                    var diag = args.Severity == XmlSeverityType.Error
                        ? Diagnostic.Error(code, args.Message, pDoc.RelativePath, line, col)
                        : Diagnostic.Warning(code, args.Message, pDoc.RelativePath, line, col);

                    docDiagnostics.Add(diag);
                });

            XDocument? xDoc = null;
            try
            {
                using var strReader = new StringReader(pDoc.Content);
                using var xmlReader = SecureXmlReaderFactory.CreateReader(strReader, settings);
                xDoc = XDocument.Load(xmlReader, LoadOptions.SetLineInfo);
            }
            catch (XmlException xmlEx)
            {
                string code;
                if (xmlEx.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase))
                {
                    code = DiagnosticCodes.DtdProhibited;
                }
                else if (xmlEx.Message.Contains("characters in the document", StringComparison.OrdinalIgnoreCase) ||
                         xmlEx.Message.Contains("MaxCharactersInDocument", StringComparison.OrdinalIgnoreCase) ||
                         (xmlEx.Message.Contains("limit", StringComparison.OrdinalIgnoreCase) && xmlEx.Message.Contains("exceeded", StringComparison.OrdinalIgnoreCase)))
                {
                    code = DiagnosticCodes.LimitExceeded;
                }
                else
                {
                    code = DiagnosticCodes.XmlParseError;
                }

                docDiagnostics.Add(Diagnostic.Error(
                    code,
                    xmlEx.Message,
                    pDoc.RelativePath,
                    xmlEx.LineNumber,
                    xmlEx.LinePosition));
            }
            catch (Exception ex)
            {
                docDiagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.XmlParseError,
                    $"Failed to read XML document: {ex.Message}",
                    pDoc.RelativePath));
            }

            var hasDocErrors = docDiagnostics.Any(d => d.Severity == "error");
            targetDocResults.Add(new DocumentValidationResult(pDoc.RelativePath, !hasDocErrors, docDiagnostics));
            diagnostics.AddRange(docDiagnostics);

            if (!hasDocErrors && xDoc != null)
            {
                prospectiveMap[normalizedRelPath] = (pDoc, xDoc, managedDoc);
            }
        }

        if (diagnostics.Any(d => d.Severity == "error"))
        {
            return new ValidationResult(
                false,
                "workspace",
                prospectiveDocuments.Count,
                targetDocResults,
                SortDiagnostics(diagnostics));
        }

        // 2. Load existing workspace documents to build prospective workspace view
        var (enumSuccess, existingDocs, enumDiags) = WorkspaceDiscovery.EnumerateDocuments(workspaceRoot);
        if (!enumSuccess || enumDiags.Count > 0)
        {
            diagnostics.AddRange(enumDiags);
            return new ValidationResult(
                false,
                "workspace",
                prospectiveDocuments.Count,
                targetDocResults,
                SortDiagnostics(diagnostics));
        }

        var combinedList = new List<(ManagedDocument Document, XDocument XDoc)>();

        foreach (var exDoc in existingDocs)
        {
            if (prospectiveMap.TryGetValue(exDoc.RelativePath, out var prospectiveItem))
            {
                // Validate expected revision if specified
                if (prospectiveItem.Doc.ExpectedRevision.HasValue)
                {
                    try
                    {
                        using var s = File.OpenRead(exDoc.FullPath);
                        using var r = SecureXmlReaderFactory.CreateReader(s);
                        var curXDoc = XDocument.Load(r);
                        var curRevStr = curXDoc.Root?.Attribute("revision")?.Value;
                        if (int.TryParse(curRevStr, System.Globalization.CultureInfo.InvariantCulture, out var curRev))
                        {
                            if (curRev != prospectiveItem.Doc.ExpectedRevision.Value)
                            {
                                diagnostics.Add(new Diagnostic(
                                    DiagnosticCodes.RevisionConflict,
                                    "error",
                                    $"Expected revision {prospectiveItem.Doc.ExpectedRevision.Value} does not match actual revision {curRev} for document '{exDoc.RelativePath}'.",
                                    Document: exDoc.RelativePath,
                                    ExpectedRevision: prospectiveItem.Doc.ExpectedRevision.Value,
                                    ActualRevision: curRev));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.XmlParseError,
                            $"Failed to read existing document revision for '{exDoc.RelativePath}': {ex.Message}",
                            exDoc.RelativePath));
                    }
                }

                combinedList.Add((prospectiveItem.ManagedDoc, prospectiveItem.XDoc));
                prospectiveMap.Remove(exDoc.RelativePath);
            }
            else
            {
                var schemaCheck = ValidateDocumentSchemaOnly(exDoc, version: version);
                if (!schemaCheck.IsValid)
                {
                    diagnostics.AddRange(schemaCheck.Diagnostics);
                }
                else
                {
                    try
                    {
                        using var s = File.OpenRead(exDoc.FullPath);
                        using var r = SecureXmlReaderFactory.CreateReader(s);
                        var exXDoc = XDocument.Load(r, LoadOptions.SetLineInfo);
                        combinedList.Add((exDoc, exXDoc));
                    }
                    catch (Exception ex)
                    {
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.XmlParseError,
                            $"Failed to load existing document '{exDoc.RelativePath}': {ex.Message}",
                            exDoc.RelativePath));
                    }
                }
            }
        }

        // Add remaining prospective documents (new additions)
        foreach (var remaining in prospectiveMap.Values)
        {
            combinedList.Add((remaining.ManagedDoc, remaining.XDoc));
        }

        if (diagnostics.Any(d => d.Severity == "error"))
        {
            return new ValidationResult(
                false,
                "workspace",
                combinedList.Count,
                targetDocResults,
                SortDiagnostics(diagnostics));
        }

        // 3. Whole workspace semantic validation over prospective graph
        var index = ProjectSemanticIndex.Build(combinedList);
        var semanticDiagnostics = SemanticValidator.Validate(index);
        diagnostics.AddRange(semanticDiagnostics);

        var sortedDiagnostics = SortDiagnostics(diagnostics);
        var overallValid = !sortedDiagnostics.Any(d => d.Severity == "error");

        return new ValidationResult(
            overallValid,
            "workspace",
            combinedList.Count,
            targetDocResults,
            sortedDiagnostics);
    }

    private static List<Diagnostic> SortDiagnostics(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics
            .OrderBy(d => d.Document, StringComparer.Ordinal)
            .ThenBy(d => d.Line ?? int.MaxValue)
            .ThenBy(d => d.Column ?? int.MaxValue)
            .ThenBy(d => d.Code, StringComparer.Ordinal)
            .ThenBy(d => d.Message, StringComparer.Ordinal)
            .ToList();
}
