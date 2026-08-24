using System.Xml.Linq;
using DogdouSpec.Core.Append;
using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Core.Transactions;

/// <summary>
/// Authoritative resolved-state guard that inspects before and after managed XML trees
/// to ensure low-level mutations (generic append, transaction apply) cannot bypass
/// protected product decisions and owner authority gates.
/// </summary>
public static class ProtectedStateGuard
{
    /// <summary>
    /// Evaluates the actual resolved XML tree before and after mutation for the given managed document.
    /// Returns a Diagnostic with OWNER_DECISION_REQUIRED (exit 5) if any protected rule is violated,
    /// or null if the mutation is permitted.
    /// </summary>
    public static Diagnostic? CheckProtectedState(
        string normalizedDocPath,
        XDocument beforeDoc,
        XDocument afterDoc)
    {
        var beforeRoot = beforeDoc.Root;
        var afterRoot = afterDoc.Root;

        if (beforeRoot == null || afterRoot == null)
        {
            return null;
        }

        var receiptDiag = CheckOperationReceipts(normalizedDocPath, beforeDoc, afterDoc);
        if (receiptDiag != null)
        {
            return receiptDiag;
        }

        var rootName = afterRoot.Name.LocalName;

        // 1. SPEC document (spec.xml / iteration root)
        if (string.Equals(rootName, "iteration", StringComparison.Ordinal) ||
            normalizedDocPath.EndsWith("/spec.xml", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedDocPath, "spec.xml", StringComparison.OrdinalIgnoreCase))
        {
            // 1.1 Iteration lifecycle status and completed_at
            var beforeIterStatus = beforeRoot.Attribute("status")?.Value;
            var afterIterStatus = afterRoot.Attribute("status")?.Value;
            if (!string.Equals(beforeIterStatus, afterIterStatus, StringComparison.Ordinal))
            {
                return Diagnostic.Error(
                    DiagnosticCodes.OwnerDecisionRequired,
                    $"Direct modification of iteration lifecycle status (from '{beforeIterStatus}' to '{afterIterStatus}') requires explicit owner confirmation.",
                    normalizedDocPath);
            }

            var beforeCompletedAt = beforeRoot.Attribute("completed_at")?.Value;
            var afterCompletedAt = afterRoot.Attribute("completed_at")?.Value;
            if (!string.Equals(beforeCompletedAt, afterCompletedAt, StringComparison.Ordinal))
            {
                return Diagnostic.Error(
                    DiagnosticCodes.OwnerDecisionRequired,
                    "Direct modification of iteration completed_at requires explicit owner confirmation.",
                    normalizedDocPath);
            }

            // 1.2 Confirmations: strictly prohibited via generic mutation
            var beforeConfirmations = beforeDoc.Descendants("confirmation").ToList();
            var afterConfirmations = afterDoc.Descendants("confirmation").ToList();

            if (afterConfirmations.Count > beforeConfirmations.Count)
            {
                return Diagnostic.Error(
                    DiagnosticCodes.OwnerDecisionRequired,
                    "Appending confirmation records requires explicit owner confirmation via 'iteration confirm'. Generic transactions cannot append confirmations.",
                    normalizedDocPath);
            }

            if (afterConfirmations.Count < beforeConfirmations.Count ||
                !AreConfirmationListsEqual(beforeConfirmations, afterConfirmations))
            {
                return Diagnostic.Error(
                    DiagnosticCodes.OwnerDecisionRequired,
                    "Modifying confirmation records requires explicit owner confirmation via 'iteration confirm'.",
                    normalizedDocPath);
            }

            var beforeConfContainer = beforeDoc.Descendants("confirmations").Any();
            var afterConfContainer = afterDoc.Descendants("confirmations").Any();
            if (beforeConfContainer != afterConfContainer)
            {
                return Diagnostic.Error(
                    DiagnosticCodes.OwnerDecisionRequired,
                    "Adding or removing the confirmations container requires explicit owner confirmation via 'iteration confirm'.",
                    normalizedDocPath);
            }

            // 1.3 Requirements status and lifecycle
            var reqDiag = CheckRequirements(normalizedDocPath, beforeDoc, afterDoc);
            if (reqDiag != null) return reqDiag;

            // 1.4 Research questions status and lifecycle
            var questionDiag = CheckQuestions(normalizedDocPath, beforeDoc, afterDoc);
            if (questionDiag != null) return questionDiag;

            // 1.5 Material design decisions status and lifecycle
            var decisionDiag = CheckDesignDecisions(normalizedDocPath, beforeDoc, afterDoc);
            if (decisionDiag != null) return decisionDiag;

            // 1.6 Product and research acceptance criteria decisions
            var critDiag = CheckCriteria(normalizedDocPath, beforeDoc, afterDoc);
            if (critDiag != null) return critDiag;
        }

        // 2. Knowledge entries in knowledge.xml
        if (string.Equals(rootName, "knowledge", StringComparison.Ordinal) ||
            string.Equals(normalizedDocPath, "knowledge.xml", StringComparison.OrdinalIgnoreCase))
        {
            var entryDiag = CheckKnowledgeEntries(normalizedDocPath, beforeDoc, afterDoc);
            if (entryDiag != null) return entryDiag;
        }

        // 3. Backlog items in backlog.xml
        if (string.Equals(rootName, "backlog", StringComparison.Ordinal) ||
            string.Equals(normalizedDocPath, "backlog.xml", StringComparison.OrdinalIgnoreCase))
        {
            var backlogDiag = CheckBacklogItems(normalizedDocPath, beforeDoc, afterDoc);
            if (backlogDiag != null) return backlogDiag;
        }

        return null;
    }

    private static Diagnostic? CheckOperationReceipts(
        string docPath,
        XDocument beforeDoc,
        XDocument afterDoc)
    {
        static Dictionary<string, XElement> IndexReceipts(XDocument doc)
        {
            var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
            foreach (var record in doc.Descendants("record"))
            {
                var operationId = record.Attribute("operation_id")?.Value;
                var recordId = record.Attribute("id")?.Value;
                if (string.IsNullOrEmpty(operationId) || string.IsNullOrEmpty(recordId))
                {
                    continue;
                }

                result[$"{operationId}\u001f{recordId}"] = record;
            }
            return result;
        }

        var beforeReceipts = IndexReceipts(beforeDoc);
        var afterReceipts = IndexReceipts(afterDoc);
        if (beforeReceipts.Count != afterReceipts.Count)
        {
            return Diagnostic.Error(
                DiagnosticCodes.IdempotencyConflict,
                "Low-level mutations cannot add or remove durable Task update receipts.",
                docPath);
        }

        foreach (var (key, beforeRecord) in beforeReceipts)
        {
            if (!afterReceipts.TryGetValue(key, out var afterRecord) ||
                !GenericAppender.AreElementsCanonicallyEqual(beforeRecord, afterRecord))
            {
                return Diagnostic.Error(
                    DiagnosticCodes.IdempotencyConflict,
                    $"Low-level mutations cannot modify durable Task update receipt '{beforeRecord.Attribute("operation_id")?.Value}'.",
                    docPath);
            }
        }

        return null;
    }

    private static bool AreConfirmationListsEqual(List<XElement> before, List<XElement> after)
    {
        if (before.Count != after.Count) return false;
        for (var i = 0; i < before.Count; i++)
        {
            if (!GenericAppender.AreElementsCanonicallyEqual(before[i], after[i]))
            {
                return false;
            }
        }
        return true;
    }

    private static Diagnostic? CheckRequirements(string docPath, XDocument beforeDoc, XDocument afterDoc)
    {
        var beforeReqs = IndexById(beforeDoc, "requirement");
        var afterReqs = IndexById(afterDoc, "requirement");

        foreach (var (id, afterElem) in afterReqs)
        {
            var status = afterElem.Attribute("status")?.Value ?? string.Empty;
            if (beforeReqs.TryGetValue(id, out var beforeElem))
            {
                var beforeStatus = beforeElem.Attribute("status")?.Value ?? string.Empty;
                if (!string.Equals(beforeStatus, status, StringComparison.Ordinal))
                {
                    return Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Changing requirement '{id}' status from '{beforeStatus}' to '{status}' requires owner confirmation.",
                        docPath);
                }
                if (!string.Equals(beforeStatus, "proposed", StringComparison.Ordinal) &&
                    !AreProtectedContentEqual(beforeElem, afterElem))
                {
                    return Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Modifying approved or otherwise decided requirement '{id}' requires owner confirmation.",
                        docPath);
                }
            }
            else
            {
                // Added requirement
                if (!string.Equals(status, "proposed", StringComparison.Ordinal))
                {
                    return Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Adding requirement '{id}' with status '{status}' requires owner confirmation. Generic mutations only allow status='proposed'.",
                        docPath);
                }
            }
        }

        foreach (var (id, beforeElem) in beforeReqs)
        {
            if (!afterReqs.ContainsKey(id))
            {
                // Removed requirement
                var beforeStatus = beforeElem.Attribute("status")?.Value ?? string.Empty;
                if (!string.Equals(beforeStatus, "proposed", StringComparison.Ordinal))
                {
                    return Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Removing requirement '{id}' with non-proposed status '{beforeStatus}' requires owner confirmation.",
                        docPath);
                }
            }
        }

        return null;
    }

    private static Diagnostic? CheckQuestions(string docPath, XDocument beforeDoc, XDocument afterDoc)
    {
        var beforeQuestions = IndexById(beforeDoc, "question");
        var afterQuestions = IndexById(afterDoc, "question");

        foreach (var (id, afterElem) in afterQuestions)
        {
            var status = afterElem.Attribute("status")?.Value ?? string.Empty;
            if (beforeQuestions.TryGetValue(id, out var beforeElem))
            {
                var beforeStatus = beforeElem.Attribute("status")?.Value ?? string.Empty;
                if (!string.Equals(beforeStatus, status, StringComparison.Ordinal))
                {
                    return Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Changing research question '{id}' status from '{beforeStatus}' to '{status}' requires owner confirmation.",
                        docPath);
                }
                if (!string.Equals(beforeStatus, "open", StringComparison.Ordinal) &&
                    !AreProtectedContentEqual(beforeElem, afterElem))
                {
                    return Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Modifying disposed research question '{id}' requires owner confirmation.",
                        docPath);
                }
            }
            else
            {
                // Added question
                if (!string.Equals(status, "open", StringComparison.Ordinal))
                {
                    return Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Adding research question '{id}' with status '{status}' requires owner confirmation. Generic mutations only allow status='open'.",
                        docPath);
                }
            }
        }

        foreach (var (id, beforeElem) in beforeQuestions)
        {
            if (!afterQuestions.ContainsKey(id))
            {
                // Removed question
                var beforeStatus = beforeElem.Attribute("status")?.Value ?? string.Empty;
                if (!string.Equals(beforeStatus, "open", StringComparison.Ordinal))
                {
                    return Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Removing research question '{id}' with non-open status '{beforeStatus}' requires owner confirmation.",
                        docPath);
                }
            }
        }

        return null;
    }

    private static Diagnostic? CheckDesignDecisions(string docPath, XDocument beforeDoc, XDocument afterDoc)
    {
        var beforeDecisions = IndexById(beforeDoc, "decision");
        var afterDecisions = IndexById(afterDoc, "decision");

        foreach (var (id, afterElem) in afterDecisions)
        {
            var status = afterElem.Attribute("status")?.Value ?? string.Empty;
            if (beforeDecisions.TryGetValue(id, out var beforeElem))
            {
                var beforeStatus = beforeElem.Attribute("status")?.Value ?? string.Empty;
                if (!string.Equals(beforeStatus, status, StringComparison.Ordinal))
                {
                    return Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Changing design decision '{id}' status from '{beforeStatus}' to '{status}' requires owner confirmation.",
                        docPath);
                }
                if (!string.Equals(beforeStatus, "proposed", StringComparison.Ordinal) &&
                    !AreProtectedContentEqual(beforeElem, afterElem))
                {
                    return Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Modifying accepted or otherwise decided design decision '{id}' requires owner confirmation.",
                        docPath);
                }
            }
            else
            {
                // Added decision
                if (!string.Equals(status, "proposed", StringComparison.Ordinal))
                {
                    return Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Adding design decision '{id}' with status '{status}' requires owner confirmation. Generic mutations only allow status='proposed'.",
                        docPath);
                }
            }
        }

        foreach (var (id, beforeElem) in beforeDecisions)
        {
            if (!afterDecisions.ContainsKey(id))
            {
                // Removed decision
                var beforeStatus = beforeElem.Attribute("status")?.Value ?? string.Empty;
                if (!string.Equals(beforeStatus, "proposed", StringComparison.Ordinal))
                {
                    return Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Removing design decision '{id}' with non-proposed status '{beforeStatus}' requires owner confirmation.",
                        docPath);
                }
            }
        }

        return null;
    }

    private static Diagnostic? CheckCriteria(string docPath, XDocument beforeDoc, XDocument afterDoc)
    {
        var beforeCriteria = IndexById(beforeDoc, "criterion");
        var afterCriteria = IndexById(afterDoc, "criterion");

        foreach (var (id, afterElem) in afterCriteria)
        {
            var decision = afterElem.Attribute("decision")?.Value ?? string.Empty;
            if (beforeCriteria.TryGetValue(id, out var beforeElem))
            {
                var beforeDecision = beforeElem.Attribute("decision")?.Value ?? string.Empty;
                if (!string.Equals(beforeDecision, decision, StringComparison.Ordinal))
                {
                    return Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Changing acceptance criterion '{id}' decision from '{beforeDecision}' to '{decision}' requires owner confirmation.",
                        docPath);
                }
                if (!string.Equals(beforeDecision, "pending", StringComparison.Ordinal) &&
                    !AreProtectedContentEqual(beforeElem, afterElem))
                {
                    return Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Modifying decided acceptance criterion '{id}' requires owner confirmation.",
                        docPath);
                }
            }
            else
            {
                // Added criterion
                if (!string.Equals(decision, "pending", StringComparison.Ordinal))
                {
                    return Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Adding acceptance criterion '{id}' with decision '{decision}' requires owner confirmation. Generic mutations only allow decision='pending'.",
                        docPath);
                }
            }
        }

        foreach (var (id, beforeElem) in beforeCriteria)
        {
            if (!afterCriteria.ContainsKey(id))
            {
                // Removed criterion
                var beforeDecision = beforeElem.Attribute("decision")?.Value ?? string.Empty;
                if (!string.Equals(beforeDecision, "pending", StringComparison.Ordinal))
                {
                    return Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Removing acceptance criterion '{id}' with non-pending decision '{beforeDecision}' requires owner confirmation.",
                        docPath);
                }
            }
        }

        return null;
    }

    private static Diagnostic? CheckKnowledgeEntries(string docPath, XDocument beforeDoc, XDocument afterDoc)
    {
        var beforeEntries = IndexById(beforeDoc, "entry");
        var afterEntries = IndexById(afterDoc, "entry");

        foreach (var (id, afterElem) in afterEntries)
        {
            var status = afterElem.Attribute("status")?.Value ?? string.Empty;
            if (beforeEntries.TryGetValue(id, out var beforeElem))
            {
                var beforeStatus = beforeElem.Attribute("status")?.Value ?? string.Empty;
                if (!string.Equals(beforeStatus, status, StringComparison.Ordinal))
                {
                    return Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Changing knowledge entry '{id}' status from '{beforeStatus}' to '{status}' requires owner confirmation.",
                        docPath);
                }
                if (!string.Equals(beforeStatus, "proposed", StringComparison.Ordinal) &&
                    !AreProtectedContentEqual(beforeElem, afterElem))
                {
                    return Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Modifying verified or otherwise decided knowledge entry '{id}' requires owner confirmation.",
                        docPath);
                }
            }
            else
            {
                // Added entry
                if (!string.Equals(status, "proposed", StringComparison.Ordinal))
                {
                    return Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Adding knowledge entry '{id}' with status '{status}' requires owner confirmation. Generic mutations only allow status='proposed'.",
                        docPath);
                }
            }
        }

        foreach (var (id, beforeElem) in beforeEntries)
        {
            if (!afterEntries.ContainsKey(id))
            {
                // Removed entry
                var beforeStatus = beforeElem.Attribute("status")?.Value ?? string.Empty;
                if (!string.Equals(beforeStatus, "proposed", StringComparison.Ordinal))
                {
                    return Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Removing knowledge entry '{id}' with non-proposed status '{beforeStatus}' requires owner confirmation.",
                        docPath);
                }
            }
        }

        return null;
    }

    private static Diagnostic? CheckBacklogItems(string docPath, XDocument beforeDoc, XDocument afterDoc)
    {
        var beforeItems = IndexById(beforeDoc, "item");
        var afterItems = IndexById(afterDoc, "item");

        foreach (var (id, afterElem) in afterItems)
        {
            var status = afterElem.Attribute("status")?.Value ?? string.Empty;
            if (beforeItems.TryGetValue(id, out var beforeElem))
            {
                var beforeStatus = beforeElem.Attribute("status")?.Value ?? string.Empty;
                if (!string.Equals(beforeStatus, status, StringComparison.Ordinal))
                {
                    return Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Changing backlog item '{id}' status from '{beforeStatus}' to '{status}' requires owner decision.",
                        docPath);
                }
                if (!string.Equals(beforeStatus, "open", StringComparison.Ordinal) &&
                    !AreProtectedContentEqual(beforeElem, afterElem))
                {
                    return Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Modifying scheduled or otherwise disposed backlog item '{id}' requires owner decision.",
                        docPath);
                }
            }
            else
            {
                // Added item
                if (!string.Equals(status, "open", StringComparison.Ordinal))
                {
                    return Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Adding backlog item '{id}' with status '{status}' requires owner decision. Generic mutations only allow status='open'.",
                        docPath);
                }
            }
        }

        foreach (var (id, beforeElem) in beforeItems)
        {
            if (!afterItems.ContainsKey(id))
            {
                // Removed item
                var beforeStatus = beforeElem.Attribute("status")?.Value ?? string.Empty;
                if (!string.Equals(beforeStatus, "open", StringComparison.Ordinal))
                {
                    return Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Removing backlog item '{id}' with non-open status '{beforeStatus}' requires owner decision.",
                        docPath);
                }
            }
        }

        return null;
    }

    private static Dictionary<string, XElement> IndexById(XDocument doc, string elementName)
    {
        var dict = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var elem in doc.Descendants(elementName))
        {
            var id = elem.Attribute("id")?.Value;
            if (!string.IsNullOrEmpty(id) && !dict.ContainsKey(id))
            {
                dict[id] = elem;
            }
        }
        return dict;
    }

    private static bool AreProtectedContentEqual(XElement before, XElement after)
    {
        var beforeClone = new XElement(before);
        var afterClone = new XElement(after);
        beforeClone.Elements("records").Remove();
        afterClone.Elements("records").Remove();
        return GenericAppender.AreElementsCanonicallyEqual(beforeClone, afterClone);
    }
}
