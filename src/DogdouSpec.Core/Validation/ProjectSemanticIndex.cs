using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Validation;

/// <summary>
/// Immutable deterministic semantic index for a project or workspace.
/// </summary>
public sealed class ProjectSemanticIndex
{
    public IReadOnlyList<(ManagedDocument Document, XDocument XDoc)> LoadedDocuments { get; }
    public IReadOnlyList<IndexedObject> AllObjects { get; }
    public IReadOnlyDictionary<string, List<IndexedObject>> ObjectsById { get; }
    public IReadOnlyList<IndexedOperationReceipt> AllOperationReceipts { get; }
    public IReadOnlyDictionary<string, List<IndexedOperationReceipt>> OperationReceiptsById { get; }
    public IReadOnlyList<ParsedReference> AllReferences { get; }
    public IReadOnlyList<ParsedIteration> Iterations { get; }
    public IReadOnlyList<ParsedTasksDocument> TasksDocuments { get; }
    public IReadOnlyList<ParsedTask> AllTasks { get; }

    private ProjectSemanticIndex(
        IReadOnlyList<(ManagedDocument Document, XDocument XDoc)> loadedDocuments,
        IReadOnlyList<IndexedObject> allObjects,
        IReadOnlyDictionary<string, List<IndexedObject>> objectsById,
        IReadOnlyList<IndexedOperationReceipt> allOperationReceipts,
        IReadOnlyDictionary<string, List<IndexedOperationReceipt>> operationReceiptsById,
        IReadOnlyList<ParsedReference> allReferences,
        IReadOnlyList<ParsedIteration> iterations,
        IReadOnlyList<ParsedTasksDocument> tasksDocuments,
        IReadOnlyList<ParsedTask> allTasks)
    {
        LoadedDocuments = loadedDocuments;
        AllObjects = allObjects;
        ObjectsById = objectsById;
        AllOperationReceipts = allOperationReceipts;
        OperationReceiptsById = operationReceiptsById;
        AllReferences = allReferences;
        Iterations = iterations;
        TasksDocuments = tasksDocuments;
        AllTasks = allTasks;
    }

    public static bool IsValidTimeFirstId(string id) =>
        !string.IsNullOrEmpty(id) && PathSecurity.IterationIdRegex.IsMatch(id);

    public static bool IsValidToken(string? value) =>
        PathSecurity.IsValidTokenValue(value);

    public static (bool Success, ProjectSemanticIndex? Index, IReadOnlyList<Diagnostic> Diagnostics) LoadConsistent(
        string workspaceRoot,
        int maxRetries = 3)
    {
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            var (enumSuccess, allDocs, enumDiags) = WorkspaceDiscovery.EnumerateDocuments(workspaceRoot);
            if (!enumSuccess || enumDiags.Count > 0)
            {
                return (false, null, enumDiags);
            }

            var parsedDocs = new List<(ManagedDocument Document, XDocument XDoc)>();
            var docWriteTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            bool parseFailed = false;
            var parseDiags = new List<Diagnostic>();
            foreach (var doc in allDocs)
            {
                try
                {
                    docWriteTimes[doc.FullPath] = File.GetLastWriteTimeUtc(doc.FullPath);
                    using var stream = File.OpenRead(doc.FullPath);
                    using var reader = SecureXmlReaderFactory.CreateReader(stream);
                    var xDoc = XDocument.Load(reader, LoadOptions.SetLineInfo);
                    parsedDocs.Add((doc, xDoc));
                }
                catch (Exception ex)
                {
                    parseFailed = true;
                    parseDiags.Add(Diagnostic.Error(
                        DiagnosticCodes.XmlParseError,
                        $"Failed to parse XML document '{doc.RelativePath}': {ex.Message}",
                        doc.RelativePath));
                    break;
                }
            }

            if (parseFailed)
            {
                return (false, null, parseDiags);
            }

            // Verify read consistency: check if any document was modified during reading
            bool driftDetected = false;
            foreach (var (fullPath, initialTime) in docWriteTimes)
            {
                if (File.Exists(fullPath) && File.GetLastWriteTimeUtc(fullPath) != initialTime)
                {
                    driftDetected = true;
                    break;
                }
            }

            if (driftDetected)
            {
                if (attempt + 1 < maxRetries)
                {
                    continue; // Retry read
                }
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.RevisionConflict, "Concurrent document modification detected during document load. Retry bound exceeded.") });
            }

            var index = Build(parsedDocs);
            return (true, index, Array.Empty<Diagnostic>());
        }

        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.RevisionConflict, "Failed to obtain consistent document snapshot after retries.") });
    }

    public static ProjectSemanticIndex Build(IReadOnlyList<(ManagedDocument Document, XDocument XDoc)> documents)
    {
        var allObjects = new List<IndexedObject>();
        var objectsById = new Dictionary<string, List<IndexedObject>>(StringComparer.Ordinal);
        var allOperationReceipts = new List<IndexedOperationReceipt>();
        var operationReceiptsById = new Dictionary<string, List<IndexedOperationReceipt>>(StringComparer.Ordinal);
        var allReferences = new List<ParsedReference>();
        var iterations = new List<ParsedIteration>();
        var tasksDocuments = new List<ParsedTasksDocument>();
        var allTasks = new List<ParsedTask>();

        foreach (var (doc, xDoc) in documents)
        {
            if (xDoc.Root == null)
            {
                continue;
            }

            // 1. Collect all elements with @id and @operation_id
            foreach (var element in xDoc.Root.DescendantsAndSelf())
            {
                var idAttr = element.Attribute("id");
                if (idAttr != null && !string.IsNullOrEmpty(idAttr.Value))
                {
                    var lineInfo = (IXmlLineInfo)element;
                    var indexed = new IndexedObject(
                        idAttr.Value,
                        element.Name.LocalName,
                        element.Parent?.Name.LocalName,
                        doc,
                        lineInfo.HasLineInfo() ? lineInfo.LineNumber : null,
                        lineInfo.HasLineInfo() ? lineInfo.LinePosition : null,
                        element);

                    allObjects.Add(indexed);
                    if (!objectsById.TryGetValue(idAttr.Value, out var list))
                    {
                        list = new List<IndexedObject>();
                        objectsById[idAttr.Value] = list;
                    }
                    list.Add(indexed);
                }

                var opIdAttr = element.Attribute("operation_id");
                if (opIdAttr != null && !string.IsNullOrEmpty(opIdAttr.Value))
                {
                    var lineInfo = (IXmlLineInfo)element;
                    var recId = element.Attribute("id")?.Value;
                    var containingTaskId = element.Ancestors("task").FirstOrDefault()?.Attribute("id")?.Value;
                    if (containingTaskId == null && element.Name.LocalName == "task")
                    {
                        containingTaskId = element.Attribute("id")?.Value;
                    }

                    var receipt = new IndexedOperationReceipt(
                        opIdAttr.Value,
                        recId,
                        element.Name.LocalName,
                        element.Parent?.Name.LocalName,
                        doc,
                        containingTaskId,
                        lineInfo.HasLineInfo() ? lineInfo.LineNumber : null,
                        lineInfo.HasLineInfo() ? lineInfo.LinePosition : null,
                        element);

                    allOperationReceipts.Add(receipt);
                    if (!operationReceiptsById.TryGetValue(opIdAttr.Value, out var opList))
                    {
                        opList = new List<IndexedOperationReceipt>();
                        operationReceiptsById[opIdAttr.Value] = opList;
                    }
                    opList.Add(receipt);
                }

                // 2. Collect all <ref> elements
                if (element.Name.LocalName == "ref")
                {
                    var scope = element.Attribute("scope")?.Value ?? string.Empty;
                    var target = element.Attribute("target")?.Value ?? string.Empty;
                    var relation = element.Attribute("relation")?.Value ?? string.Empty;
                    var lineInfo = (IXmlLineInfo)element;

                    var containingObjId = element.Ancestors()
                        .FirstOrDefault(a => a.Attribute("id") != null)
                        ?.Attribute("id")?.Value;

                    var parsedRef = new ParsedReference(
                        scope,
                        target,
                        relation,
                        doc,
                        containingObjId,
                        lineInfo.HasLineInfo() ? lineInfo.LineNumber : null,
                        lineInfo.HasLineInfo() ? lineInfo.LinePosition : null,
                        element);

                    allReferences.Add(parsedRef);
                }
            }

            // 3. Document-specific entities
            var rootName = xDoc.Root.Name.LocalName;
            if (rootName == "iteration")
            {
                var parsedIter = ParseIteration(xDoc.Root, doc);
                iterations.Add(parsedIter);
            }
            else if (rootName == "tasks")
            {
                var parsedTasksDoc = ParseTasksDocument(xDoc.Root, doc);
                tasksDocuments.Add(parsedTasksDoc);
                allTasks.AddRange(parsedTasksDoc.Tasks);
            }
        }

        return new ProjectSemanticIndex(
            documents,
            allObjects,
            objectsById,
            allOperationReceipts,
            operationReceiptsById,
            allReferences,
            iterations,
            tasksDocuments,
            allTasks);
    }

    private static ParsedIteration ParseIteration(XElement root, ManagedDocument doc)
    {
        var lineInfo = (IXmlLineInfo)root;
        var id = root.Attribute("id")?.Value ?? string.Empty;
        var kind = root.Attribute("kind")?.Value ?? string.Empty;
        var status = root.Attribute("status")?.Value ?? string.Empty;
        var createdAt = root.Attribute("created_at")?.Value;
        var updatedAt = root.Attribute("updated_at")?.Value;
        var completedAt = root.Attribute("completed_at")?.Value;

        var productElem = root.Element("product");
        var researchElem = root.Element("research");

        var requirements = new List<ParsedRequirement>();
        if (productElem != null)
        {
            var reqsContainer = productElem.Element("requirements");
            if (reqsContainer != null)
            {
                foreach (var reqElem in reqsContainer.Elements("requirement"))
                {
                    var reqLine = (IXmlLineInfo)reqElem;
                    requirements.Add(new ParsedRequirement(
                        reqElem.Attribute("id")?.Value ?? string.Empty,
                        reqElem.Attribute("status")?.Value ?? string.Empty,
                        doc,
                        reqLine.HasLineInfo() ? reqLine.LineNumber : null,
                        reqLine.HasLineInfo() ? reqLine.LinePosition : null,
                        reqElem));
                }
            }
        }

        var questions = new List<ParsedResearchQuestion>();
        if (researchElem != null)
        {
            var questionsContainer = researchElem.Element("questions");
            if (questionsContainer != null)
            {
                foreach (var qElem in questionsContainer.Elements("question"))
                {
                    var qLine = (IXmlLineInfo)qElem;
                    questions.Add(new ParsedResearchQuestion(
                        qElem.Attribute("id")?.Value ?? string.Empty,
                        qElem.Attribute("status")?.Value ?? string.Empty,
                        doc,
                        qLine.HasLineInfo() ? qLine.LineNumber : null,
                        qLine.HasLineInfo() ? qLine.LinePosition : null,
                        qElem));
                }
            }
        }

        var criteria = new List<ParsedCriterion>();
        var acceptanceElem = productElem?.Element("acceptance") ?? researchElem?.Element("acceptance");
        if (acceptanceElem != null)
        {
            foreach (var critElem in acceptanceElem.Elements("criterion"))
            {
                var critLine = (IXmlLineInfo)critElem;
                criteria.Add(new ParsedCriterion(
                    critElem.Attribute("id")?.Value ?? string.Empty,
                    null,
                    critElem.Attribute("decision")?.Value,
                    doc,
                    critLine.HasLineInfo() ? critLine.LineNumber : null,
                    critLine.HasLineInfo() ? critLine.LinePosition : null,
                    critElem));
            }
        }

        var designDecisions = new List<ParsedDesignDecision>();
        var designElem = root.Element("design");
        if (designElem != null)
        {
            var decisionsContainer = designElem.Element("decisions");
            if (decisionsContainer != null)
            {
                foreach (var decElem in decisionsContainer.Elements("decision"))
                {
                    var decLine = (IXmlLineInfo)decElem;
                    designDecisions.Add(new ParsedDesignDecision(
                        decElem.Attribute("id")?.Value ?? string.Empty,
                        decElem.Attribute("status")?.Value ?? string.Empty,
                        doc,
                        decLine.HasLineInfo() ? decLine.LineNumber : null,
                        decLine.HasLineInfo() ? decLine.LinePosition : null,
                        decElem));
                }
            }
        }

        var confirmations = new List<ParsedConfirmation>();
        var confirmationsElem = root.Element("confirmations");
        if (confirmationsElem != null)
        {
            foreach (var confElem in confirmationsElem.Elements("confirmation"))
            {
                confirmations.Add(ParseConfirmation(confElem, doc));
            }
        }

        return new ParsedIteration(
            id,
            kind,
            status,
            createdAt,
            updatedAt,
            completedAt,
            productElem != null,
            researchElem != null,
            requirements,
            questions,
            criteria,
            designDecisions,
            confirmations,
            doc,
            lineInfo.HasLineInfo() ? lineInfo.LineNumber : null,
            lineInfo.HasLineInfo() ? lineInfo.LinePosition : null,
            root);
    }

    private static ParsedConfirmation ParseConfirmation(XElement confElem, ManagedDocument doc)
    {
        var lineInfo = (IXmlLineInfo)confElem;
        var id = confElem.Attribute("id")?.Value ?? string.Empty;
        var action = confElem.Attribute("action")?.Value ?? string.Empty;
        var decision = confElem.Attribute("decision")?.Value ?? string.Empty;
        var actor = confElem.Attribute("actor")?.Value;
        var decidedAt = confElem.Attribute("decided_at")?.Value;
        var summary = confElem.Element("summary")?.Value ?? string.Empty;
        var rationale = confElem.Element("rationale")?.Value;

        var reqs = new List<ParsedConfirmationTarget>();
        var reqsElem = confElem.Element("requirements");
        if (reqsElem != null)
        {
            foreach (var child in reqsElem.Elements())
            {
                var target = child.Attribute("target")?.Value;
                var dec = child.Attribute("decision")?.Value;
                var childLine = (IXmlLineInfo)child;
                if (!string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(dec))
                {
                    reqs.Add(new ParsedConfirmationTarget(
                        target,
                        dec,
                        childLine.HasLineInfo() ? childLine.LineNumber : null,
                        childLine.HasLineInfo() ? childLine.LinePosition : null,
                        child));
                }
            }
        }

        var questions = new List<ParsedConfirmationTarget>();
        var questionsElem = confElem.Element("questions");
        if (questionsElem != null)
        {
            foreach (var child in questionsElem.Elements())
            {
                var target = child.Attribute("target")?.Value;
                var dec = child.Attribute("decision")?.Value;
                var childLine = (IXmlLineInfo)child;
                if (!string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(dec))
                {
                    questions.Add(new ParsedConfirmationTarget(
                        target,
                        dec,
                        childLine.HasLineInfo() ? childLine.LineNumber : null,
                        childLine.HasLineInfo() ? childLine.LinePosition : null,
                        child));
                }
            }
        }

        var design = new List<ParsedConfirmationTarget>();
        var designElem = confElem.Element("design");
        if (designElem != null)
        {
            foreach (var child in designElem.Elements())
            {
                var target = child.Attribute("target")?.Value;
                var dec = child.Attribute("decision")?.Value;
                var childLine = (IXmlLineInfo)child;
                if (!string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(dec))
                {
                    design.Add(new ParsedConfirmationTarget(
                        target,
                        dec,
                        childLine.HasLineInfo() ? childLine.LineNumber : null,
                        childLine.HasLineInfo() ? childLine.LinePosition : null,
                        child));
                }
            }
        }

        var acceptance = new List<ParsedConfirmationTarget>();
        var acceptanceElem = confElem.Element("acceptance");
        if (acceptanceElem != null)
        {
            foreach (var child in acceptanceElem.Elements())
            {
                var target = child.Attribute("target")?.Value;
                var dec = child.Attribute("decision")?.Value;
                var childLine = (IXmlLineInfo)child;
                if (!string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(dec))
                {
                    acceptance.Add(new ParsedConfirmationTarget(
                        target,
                        dec,
                        childLine.HasLineInfo() ? childLine.LineNumber : null,
                        childLine.HasLineInfo() ? childLine.LinePosition : null,
                        child));
                }
            }
        }

        return new ParsedConfirmation(
            id,
            action,
            decision,
            actor,
            decidedAt,
            summary,
            rationale,
            reqs,
            questions,
            design,
            acceptance,
            doc,
            lineInfo.HasLineInfo() ? lineInfo.LineNumber : null,
            lineInfo.HasLineInfo() ? lineInfo.LinePosition : null,
            confElem);
    }

    private static ParsedTasksDocument ParseTasksDocument(XElement root, ManagedDocument doc)
    {
        var lineInfo = (IXmlLineInfo)root;
        var id = root.Attribute("id")?.Value ?? string.Empty;
        var iterationAttr = root.Attribute("iteration")?.Value ?? string.Empty;

        var tasks = new List<ParsedTask>();
        foreach (var taskElem in root.Elements("task"))
        {
            var taskLine = (IXmlLineInfo)taskElem;
            var taskId = taskElem.Attribute("id")?.Value ?? string.Empty;
            var taskStatus = taskElem.Attribute("status")?.Value ?? string.Empty;
            var startedAt = taskElem.Attribute("started_at")?.Value;
            var completedAt = taskElem.Attribute("completed_at")?.Value;

            var criteria = new List<ParsedCriterion>();
            var acceptElem = taskElem.Element("acceptance");
            if (acceptElem != null)
            {
                foreach (var critElem in acceptElem.Elements("criterion"))
                {
                    var critLine = (IXmlLineInfo)critElem;
                    criteria.Add(new ParsedCriterion(
                        critElem.Attribute("id")?.Value ?? string.Empty,
                        critElem.Attribute("status")?.Value,
                        null,
                        doc,
                        critLine.HasLineInfo() ? critLine.LineNumber : null,
                        critLine.HasLineInfo() ? critLine.LinePosition : null,
                        critElem));
                }
            }

            var records = new List<ParsedRecord>();
            var recordsElem = taskElem.Element("records");
            if (recordsElem != null)
            {
                foreach (var recElem in recordsElem.Elements("record"))
                {
                    records.Add(ParseRecord(recElem, doc));
                }
            }

            var dependencies = new List<ParsedReference>();
            var depElem = taskElem.Element("dependencies");
            if (depElem != null)
            {
                foreach (var refElem in depElem.Elements("ref"))
                {
                    var rLine = (IXmlLineInfo)refElem;
                    dependencies.Add(new ParsedReference(
                        refElem.Attribute("scope")?.Value ?? string.Empty,
                        refElem.Attribute("target")?.Value ?? string.Empty,
                        refElem.Attribute("relation")?.Value ?? string.Empty,
                        doc,
                        taskId,
                        rLine.HasLineInfo() ? rLine.LineNumber : null,
                        rLine.HasLineInfo() ? rLine.LinePosition : null,
                        refElem));
                }
            }

            var origins = new List<ParsedReference>();
            var originElem = taskElem.Element("origin");
            if (originElem != null)
            {
                foreach (var refElem in originElem.Elements("ref"))
                {
                    var rLine = (IXmlLineInfo)refElem;
                    origins.Add(new ParsedReference(
                        refElem.Attribute("scope")?.Value ?? string.Empty,
                        refElem.Attribute("target")?.Value ?? string.Empty,
                        refElem.Attribute("relation")?.Value ?? string.Empty,
                        doc,
                        taskId,
                        rLine.HasLineInfo() ? rLine.LineNumber : null,
                        rLine.HasLineInfo() ? rLine.LinePosition : null,
                        refElem));
                }
            }

            tasks.Add(new ParsedTask(
                taskId,
                taskStatus,
                startedAt,
                completedAt,
                criteria,
                records,
                dependencies,
                origins,
                doc,
                taskLine.HasLineInfo() ? taskLine.LineNumber : null,
                taskLine.HasLineInfo() ? taskLine.LinePosition : null,
                taskElem));
        }

        return new ParsedTasksDocument(
            id,
            iterationAttr,
            tasks,
            doc,
            lineInfo.HasLineInfo() ? lineInfo.LineNumber : null,
            lineInfo.HasLineInfo() ? lineInfo.LinePosition : null,
            root);
    }

    private static ParsedRecord ParseRecord(XElement recElem, ManagedDocument doc)
    {
        var lineInfo = (IXmlLineInfo)recElem;
        var id = recElem.Attribute("id")?.Value ?? string.Empty;
        var kind = recElem.Attribute("kind")?.Value ?? string.Empty;
        var status = recElem.Attribute("status")?.Value ?? string.Empty;
        var createdAt = recElem.Attribute("created_at")?.Value;
        var actor = recElem.Attribute("actor")?.Value;
        var operationId = recElem.Attribute("operation_id")?.Value;

        var covers = new List<ParsedReference>();
        var coversElem = recElem.Element("covers");
        if (coversElem != null)
        {
            foreach (var refElem in coversElem.Elements("ref"))
            {
                var rLine = (IXmlLineInfo)refElem;
                covers.Add(new ParsedReference(
                    refElem.Attribute("scope")?.Value ?? string.Empty,
                    refElem.Attribute("target")?.Value ?? string.Empty,
                    refElem.Attribute("relation")?.Value ?? string.Empty,
                    doc,
                    id,
                    rLine.HasLineInfo() ? rLine.LineNumber : null,
                    rLine.HasLineInfo() ? rLine.LinePosition : null,
                    refElem));
            }
        }

        var sources = new List<ParsedReference>();
        var sourcesElem = recElem.Element("sources");
        if (sourcesElem != null)
        {
            foreach (var refElem in sourcesElem.Elements("ref"))
            {
                var rLine = (IXmlLineInfo)refElem;
                sources.Add(new ParsedReference(
                    refElem.Attribute("scope")?.Value ?? string.Empty,
                    refElem.Attribute("target")?.Value ?? string.Empty,
                    refElem.Attribute("relation")?.Value ?? string.Empty,
                    doc,
                    id,
                    rLine.HasLineInfo() ? rLine.LineNumber : null,
                    rLine.HasLineInfo() ? rLine.LinePosition : null,
                    refElem));
            }
        }

        return new ParsedRecord(
            id,
            kind,
            status,
            createdAt,
            actor,
            operationId,
            covers,
            sources,
            doc,
            lineInfo.HasLineInfo() ? lineInfo.LineNumber : null,
            lineInfo.HasLineInfo() ? lineInfo.LinePosition : null,
            recElem);
    }
}
