using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.XPath;

/// <summary>
/// Core execution engine for XPath 1.0 queries and multi-document searches.
/// </summary>
public static class XPathQueryEngine
{
    /// <summary>
    /// Evaluates an XPath expression against a single managed document.
    /// </summary>
    public static XPathQueryResult EvaluateDocument(
        string workspaceRoot,
        ManagedDocument managedDoc,
        string xpath,
        IReadOnlyDictionary<string, string>? variables,
        XPathEvaluationContext? context = null)
    {
        var evalContext = context ?? new XPathEvaluationContext();

        if (string.IsNullOrWhiteSpace(xpath))
        {
            throw new DogdouXPathException(DiagnosticCodes.InvalidArgument, "XPath expression cannot be empty.");
        }

        if (!File.Exists(managedDoc.FullPath))
        {
            throw new DogdouXPathException(
                DiagnosticCodes.DocumentNotFound,
                $"Document '{managedDoc.RelativePath}' does not exist.",
                document: managedDoc.RelativePath);
        }

        var fileInfo = new FileInfo(managedDoc.FullPath);
        if (fileInfo.Length > XPathQueryLimits.MaxDocumentBytes)
        {
            throw new DogdouXPathException(
                DiagnosticCodes.LimitExceeded,
                $"Document '{managedDoc.RelativePath}' exceeds maximum size of {XPathQueryLimits.MaxDocumentBytes} bytes (16 MiB).",
                exitCode: 7,
                document: managedDoc.RelativePath);
        }

        XDocument xdoc;
        try
        {
            using var fileStream = File.OpenRead(managedDoc.FullPath);
            using var xmlReader = SecureXmlReaderFactory.CreateReader(fileStream, baseUri: "dogdou://managed/" + managedDoc.RelativePath);
            xdoc = XDocument.Load(xmlReader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo | LoadOptions.SetBaseUri);
        }
        catch (XmlException ex)
        {
            if (ex.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase))
            {
                throw new DogdouXPathException(
                    DiagnosticCodes.DtdProhibited,
                    $"DTD is prohibited in managed XML documents: {ex.Message}",
                    exitCode: 2,
                    document: managedDoc.RelativePath,
                    line: ex.LineNumber,
                    column: ex.LinePosition,
                    innerException: ex);
            }

            throw new DogdouXPathException(
                DiagnosticCodes.XmlParseError,
                $"XML parse error in '{managedDoc.RelativePath}': {ex.Message}",
                exitCode: 2,
                document: managedDoc.RelativePath,
                line: ex.LineNumber,
                column: ex.LinePosition,
                innerException: ex);
        }

        var root = xdoc.Root;
        var revision = root?.Attribute("revision")?.Value ?? "0";

        XPathExpression xpathExpr;
        var xsltContext = new DogdouXsltContext(variables, evalContext);

        try
        {
            xpathExpr = XPathExpression.Compile(xpath);
            xpathExpr.SetContext(xsltContext);
        }
        catch (DogdouXPathException)
        {
            throw;
        }
        catch (XPathException ex)
        {
            if (ex.InnerException is DogdouXPathException inner)
            {
                throw inner;
            }

            throw new DogdouXPathException(
                DiagnosticCodes.InvalidArgument,
                $"Invalid XPath expression '{xpath}': {ex.Message}",
                exitCode: 2,
                document: managedDoc.RelativePath,
                innerException: ex);
        }

        object evalResult;
        try
        {
            var nav = xdoc.CreateNavigator();
            evalResult = nav.Evaluate(xpathExpr);
        }
        catch (DogdouXPathException)
        {
            throw;
        }
        catch (XPathException ex)
        {
            if (ex.InnerException is DogdouXPathException inner)
            {
                throw inner;
            }

            throw new DogdouXPathException(
                DiagnosticCodes.InvalidArgument,
                $"XPath evaluation error in '{managedDoc.RelativePath}': {ex.Message}",
                exitCode: 2,
                document: managedDoc.RelativePath,
                innerException: ex);
        }

        if (evalResult is XPathNodeIterator iterator)
        {
            var nodes = new List<XPathNavigator>();
            var it = iterator.Clone();
            while (it.MoveNext())
            {
                if (it.Current != null)
                {
                    nodes.Add(it.Current.Clone());
                }
            }

            if (nodes.Count > XPathQueryLimits.MaxResultNodes)
            {
                throw new DogdouXPathException(
                    DiagnosticCodes.LimitExceeded,
                    $"Query result node count {nodes.Count} exceeded the limit of {XPathQueryLimits.MaxResultNodes} nodes. Use a narrower XPath expression or structural projection.",
                    exitCode: 7,
                    document: managedDoc.RelativePath);
            }

            return XPathQueryResult.ForNodeSet(managedDoc.RelativePath, revision, nodes, evalContext.Derived);
        }

        if (evalResult is bool b)
        {
            return XPathQueryResult.ForBoolean(managedDoc.RelativePath, revision, b, evalContext.Derived);
        }

        if (evalResult is double d)
        {
            return XPathQueryResult.ForNumber(managedDoc.RelativePath, revision, d, evalContext.Derived);
        }

        if (evalResult is string s)
        {
            return XPathQueryResult.ForString(managedDoc.RelativePath, revision, s, evalContext.Derived);
        }

        throw new DogdouXPathException(
            DiagnosticCodes.InvalidArgument,
            $"Unsupported XPath result type '{evalResult?.GetType().Name}'.",
            exitCode: 2,
            document: managedDoc.RelativePath);
    }

    /// <summary>
    /// Evaluates an XPath expression independently across all managed documents in a search scope.
    /// Documents are visited in deterministic normalized relative-path order.
    /// </summary>
    public static XPathSearchResult EvaluateSearch(
        string workspaceRoot,
        string scope,
        string? iterationId,
        string xpath,
        IReadOnlyDictionary<string, string>? variables)
    {
        var (enumSuccess, docs, enumDiags) = WorkspaceDiscovery.EnumerateDocuments(
            workspaceRoot,
            iterationId: string.Equals(scope, "iteration", StringComparison.OrdinalIgnoreCase) ? iterationId : null);

        if (!enumSuccess || enumDiags.Count > 0)
        {
            var firstErr = enumDiags.FirstOrDefault(d => d.Severity == "error") ?? enumDiags[0];
            throw new DogdouXPathException(firstErr.Code, firstErr.Message, exitCode: 2);
        }

        // Sort documents in deterministic normalized relative path order
        var sortedDocs = docs.OrderBy(d => d.RelativePath, StringComparer.Ordinal).ToList();

        var evalContext = new XPathEvaluationContext();
        var nonEmptyResults = new List<XPathQueryResult>();
        var totalResultNodes = 0;

        foreach (var doc in sortedDocs)
        {
            var docResult = EvaluateDocument(workspaceRoot, doc, xpath, variables, evalContext);
            if (XPathResultFormatter.IsNonEmpty(docResult))
            {
                nonEmptyResults.Add(docResult);
                if (docResult.ResultType == XPathResultKind.NodeSet)
                {
                    totalResultNodes += docResult.Nodes.Count;
                    if (totalResultNodes > XPathQueryLimits.MaxResultNodes)
                    {
                        throw new DogdouXPathException(
                            DiagnosticCodes.LimitExceeded,
                            $"Total search result node count {totalResultNodes} exceeded the limit of {XPathQueryLimits.MaxResultNodes} nodes. Use a narrower XPath expression or structural projection.",
                            exitCode: 7);
                    }
                }
            }
        }

        return new XPathSearchResult(scope, iterationId, xpath, evalContext.Derived, nonEmptyResults);
    }
}
