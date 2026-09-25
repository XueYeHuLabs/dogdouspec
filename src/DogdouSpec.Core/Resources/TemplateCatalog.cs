using System;
using System.Collections.Generic;
using System.Linq;

namespace DogdouSpec.Core.Resources;

/// <summary>
/// Metadata descriptor for registered XML request and record templates.
/// </summary>
public sealed record TemplateDescriptor(
    string Name,
    string Summary,
    string RootElement,
    string MinimalShape);

/// <summary>
/// Catalog providing discovery of registered templates, descriptions, and expected XML shapes.
/// </summary>
public static class TemplateCatalog
{
    public static readonly IReadOnlyList<TemplateDescriptor> Templates = new[]
    {
        new TemplateDescriptor(
            "backlog.item",
            "Backlog item proposal request template",
            "backlog-item-propose",
            """
<backlog-item-propose id="20000101T000000Z-propose-item">
  <item id="YYYYMMDD-item-slug" kind="feature" severity="p2">
    <title>Item title</title>
    <summary>Item summary</summary>
  </item>
</backlog-item-propose>
"""),
        new TemplateDescriptor(
            "change.apply",
            "Change proposal application request template",
            "change-apply",
            """
<change-apply id="20000101T000000Z-changeapply" proposal="20000101-proposal" expected_spec_revision="1" expected_tasks_revision="1"/>
"""),
        new TemplateDescriptor(
            "change.propose",
            "Change proposal creation request template",
            "change-propose",
            """
<change-propose id="20000101T000000Z-changepropose" iteration="YYYYMMDD-name">
  <title>Proposal title</title>
  <intent>Proposal intent</intent>
</change-propose>
"""),
        new TemplateDescriptor(
            "iteration.confirmation",
            "Iteration human owner confirmation request template",
            "iteration-confirmation",
            """
<iteration-confirmation id="20000101T000000Z-confirm" iteration="YYYYMMDD-name" action="activate" expected_spec_revision="1" actor="owner" decided_at="2000-01-01T00:00:00Z">
  <summary>Confirm iteration activation.</summary>
  <requirements>
    <requirement target="REQ-ID" decision="approved"/>
  </requirements>
</iteration-confirmation>
"""),
        new TemplateDescriptor(
            "knowledge.entry",
            "Knowledge entry request template",
            "knowledge-entry",
            """
<knowledge-entry id="20000101T000000Z-entry">
  <entry id="YYYYMMDD-knowledge-slug" topic="architecture">
    <title>Knowledge title</title>
    <content>Knowledge content</content>
  </entry>
</knowledge-entry>
"""),
        new TemplateDescriptor(
            "record.discussion",
            "Task discussion record snippet template",
            "record",
            """
<record id="20000101T000000Z-rec-disc" kind="discussion" status="informational" created_at="2000-01-01T00:00:00Z" actor="agent">
  <summary>Discussion summary</summary>
</record>
"""),
        new TemplateDescriptor(
            "record.finding",
            "Task blocker or audit finding record snippet template",
            "record",
            """
<record id="20000101T000000Z-rec-find" kind="finding" status="active" created_at="2000-01-01T00:00:00Z" actor="agent">
  <summary>Finding summary</summary>
</record>
"""),
        new TemplateDescriptor(
            "record.verification",
            "Task verification record snippet template",
            "record",
            """
<record id="20000101T000000Z-rec-verify" kind="verification" status="informational" created_at="2000-01-01T00:00:00Z" actor="agent">
  <summary>Verification summary</summary>
  <covers>
    <ref scope="document" target="CRIT-ID" relation="covers"/>
  </covers>
</record>
"""),
        new TemplateDescriptor(
            "requirement.propose",
            "Requirement proposal request template",
            "requirement-propose",
            """
<requirement-propose id="20000101T000000Z-reqprop" iteration="YYYYMMDD-name" expected_spec_revision="1">
  <requirement id="YYYYMMDD-req-slug">
    <summary>Requirement summary</summary>
    <statement>Requirement statement</statement>
  </requirement>
</requirement-propose>
"""),
        new TemplateDescriptor(
            "task.add",
            "Task creation request template",
            "task-add",
            """
<task-add id="20000101T000000Z-taskadd" iteration="YYYYMMDD-name" expected_tasks_revision="1">
  <task id="YYYYMMDD-task-slug">
    <title>Task title</title>
    <objective>Task objective</objective>
    <scope>
      <repository path="."><include path="src/**"/></repository>
    </scope>
    <acceptance>
      <criterion id="YYYYMMDD-taskcrit-01">Criterion description</criterion>
    </acceptance>
  </task>
</task-add>
"""),
        new TemplateDescriptor(
            "task.review",
            "Task review approval or change request template",
            "task-review",
            """
<task-review id="20000101T000000Z-review" actor="reviewer" reviewed_at="2000-01-01T00:00:00Z" decision="approved">
  <summary>Review approval summary</summary>
</task-review>
"""),
        new TemplateDescriptor(
            "task.revise",
            "Task revision request template",
            "task-revise",
            """
<task-revise id="20000101T000000Z-revise" task="YYYYMMDD-task-slug" expected_tasks_revision="1">
  <title>Revised task title</title>
</task-revise>
"""),
        new TemplateDescriptor(
            "task.split",
            "Task split into subtasks request template",
            "task-split",
            """
<task-split id="20000101T000000Z-split" task="YYYYMMDD-task-slug" expected_tasks_revision="1">
  <parent-disposition transition="supersede"/>
  <subtasks>
    <task id="YYYYMMDD-task-sub1"><title>Subtask 1</title></task>
    <task id="YYYYMMDD-task-sub2"><title>Subtask 2</title></task>
  </subtasks>
</task-split>
"""),
        new TemplateDescriptor(
            "task.update",
            "Task update and state transition request template",
            "task-update",
            """
<task-update id="20000101T000000Z-update" transition="verify" actor="agent" occurred_at="2000-01-01T00:00:00Z">
  <records>
    <record id="20000101T000000Z-rec-verify" kind="verification" status="informational" created_at="2000-01-01T00:00:00Z" actor="agent">
      <summary>Verification summary</summary>
      <covers>
        <ref scope="document" target="CRIT-ID" relation="covers"/>
      </covers>
    </record>
  </records>
</task-update>
"""),
        new TemplateDescriptor(
            "transaction.apply",
            "Multi-document atomic transaction request template",
            "transaction",
            """
<transaction id="20000101T000000Z-tx">
  <operations>
    <document path="YYYYMMDD-name/spec.xml" previous_revision="1" revision="2">
      <!-- serialized content -->
    </document>
  </operations>
</transaction>
""")
    };

    public static TemplateDescriptor? FindByName(string name)
    {
        var normalized = EmbeddedResources.NormalizeTemplateName(name);
        return Templates.FirstOrDefault(t => string.Equals(t.Name, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static TemplateDescriptor? FindByRootElement(string rootElement)
    {
        return Templates.FirstOrDefault(t => string.Equals(t.RootElement, rootElement, StringComparison.OrdinalIgnoreCase));
    }

    public static string EnrichDiagnosticWithExpectedShape(string message, string? contextName = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return message;
        }

        if (message.Contains("Expected XML shape", StringComparison.Ordinal))
        {
            return message;
        }

        TemplateDescriptor? descriptor = null;
        if (!string.IsNullOrWhiteSpace(contextName))
        {
            descriptor = FindByRootElement(contextName) ?? FindByName(contextName);
        }

        if (descriptor == null)
        {
            foreach (var t in Templates)
            {
                if (message.Contains($"'{t.RootElement}'", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains($"<{t.RootElement}>", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains($"<{t.RootElement} ", StringComparison.OrdinalIgnoreCase))
                {
                    descriptor = t;
                    break;
                }
            }
        }

        if (descriptor == null)
        {
            if (message.Contains("'record'", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("<record>", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("<record ", StringComparison.OrdinalIgnoreCase))
            {
                descriptor = FindByName("record.verification");
            }
            else if (message.Contains("'requirements'", StringComparison.OrdinalIgnoreCase) ||
                     message.Contains("<requirements>", StringComparison.OrdinalIgnoreCase) ||
                     message.Contains("<requirements ", StringComparison.OrdinalIgnoreCase))
            {
                descriptor = FindByName("iteration.confirmation");
            }
        }

        if (descriptor == null)
        {
            return message;
        }

        return $"{message}\nExpected XML shape ({descriptor.Name}):\n{descriptor.MinimalShape}\n(Inspect full template: dogdouspec template show --name {descriptor.Name})";
    }
}
