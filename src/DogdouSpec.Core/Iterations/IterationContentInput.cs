using System.Text;
using System.Xml;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Tasks;
using DogdouSpec.Core.XPath;

namespace DogdouSpec.Core.Iterations;

internal static class IterationContentInput
{
    public static (XDocument? Document, Diagnostic? Error) Apply(string generated, string input, string kind, bool activate, bool suppliedCriteria)
    {
        if (Encoding.UTF8.GetByteCount(input) > XPathQueryLimits.MaxDocumentBytes)
            return (null, Diagnostic.Error(DiagnosticCodes.LimitExceeded, "Iteration content exceeds 16 MiB."));
        XDocument content;
        try
        {
            using var text = new StringReader(input);
            using var reader = SecureXmlReaderFactory.CreateReader(text);
            content = XDocument.Load(reader);
        }
        catch (XmlException error) { return (null, Diagnostic.Error(DiagnosticCodes.XmlParseError, error.Message)); }
        var root = content.Root;
        var bodyName = kind == "feature" ? "product" : "research";
        if (root?.Name != "iteration-content" || root.HasAttributes || root.Elements().Any(e => e.Name != "index" && e.Name != bodyName && e.Name != "constraints" && e.Name != "design") ||
            root.Elements().GroupBy(e => e.Name).Any(g => g.Count() != 1) || root.Element(bodyName) == null ||
            root.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
            return (null, Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Use <iteration-content> with one matching product/research body and optional index, constraints and design; lifecycle metadata and confirmations are not accepted."));
        if (root.Descendants().Attributes("operation_id").Any())
            return (null, Diagnostic.Error(DiagnosticCodes.OwnerDecisionRequired, "Creation content cannot import operation receipts."));
        foreach (var element in root.Descendants())
        {
            var expected = element.Name.LocalName switch { "requirement" => "proposed", "question" => "open", "decision" => "proposed", "criterion" => "pending", _ => null };
            if (expected == null) continue;
            var attribute = element.Name == "criterion" ? "decision" : "status";
            if ((string?)element.Attribute(attribute) != expected)
                return (null, Diagnostic.Error(DiagnosticCodes.OwnerDecisionRequired, $"Creation content requires {element.Name}/@{attribute}='{expected}'. Product decisions belong to explicit owner-authorized activation or confirmation."));
        }
        var document = XDocument.Parse(generated);
        var target = document.Root!;
        var body = new XElement(root.Element(bodyName)!);
        var hasExplicitAcceptance = body.Element("acceptance") != null;
        if (hasExplicitAcceptance && suppliedCriteria)
            return (null, Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Supply acceptance criteria in the content file or through --criterion, not both."));
        if (!hasExplicitAcceptance) body.Add(new XElement(target.Element(bodyName)!.Element("acceptance")!));
        if (activate || hasExplicitAcceptance)
        {
            foreach (var criterion in body.Element("acceptance")!.Elements("criterion"))
            {
                var (isValid, reason) = IterationCriterionPolicy.Validate(criterion.Value, (string?)criterion.Attribute("id"));
                if (!isValid) return (null, Diagnostic.Error(DiagnosticCodes.CriterionUndefined, reason!));
            }
        }
        target.Element(bodyName)!.ReplaceWith(body);
        if (root.Element("index") is { } index) target.Element("index")!.ReplaceWith(new XElement(index));
        StatusTermHelper.SynchronizeStatusTerm(target, activate ? "active" : "draft");
        if (root.Element("constraints") is { } constraints)
        {
            if (constraints.HasAttributes || constraints.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
                return (null, Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Constraints must contain only schema-valid constraint elements, without wrapper attributes or narrative text."));
            if (target.Element("constraints") is { } existing) existing.Add(constraints.Elements().Select(e => new XElement(e)));
            else target.Element("confirmations")!.AddBeforeSelf(new XElement(constraints));
        }
        if (root.Element("design") is { } design) target.Element("confirmations")!.AddBeforeSelf(new XElement(design));
        if (activate)
        {
            var confirmation = target.Element("confirmations")!.Element("confirmation")!;
            confirmation.Elements("requirements").Remove();
            if (body.Element("requirements") is { } requirements)
            {
                var decisions = new XElement("requirements");
                foreach (var requirement in requirements.Elements("requirement"))
                {
                    requirement.SetAttributeValue("status", "approved");
                    StatusTermHelper.SynchronizeStatusTerm(requirement, "approved");
                    if (requirement.Attribute("id") is { } id) decisions.Add(new XElement("requirement", new XAttribute("target", id.Value), new XAttribute("decision", "approved")));
                }
                if (decisions.HasElements) confirmation.Add(decisions);
            }
            var designDecisions = new XElement("design");
            foreach (var decision in target.Element("design")?.Element("decisions")?.Elements("decision") ?? Enumerable.Empty<XElement>())
            {
                decision.SetAttributeValue("status", "accepted");
                StatusTermHelper.SynchronizeStatusTerm(decision, "accepted");
                if (decision.Attribute("id") is { } id) designDecisions.Add(new XElement("decision", new XAttribute("target", id.Value), new XAttribute("decision", "accepted")));
            }
            if (designDecisions.HasElements) confirmation.Add(designDecisions);
        }
        return (document, null);
    }
}
