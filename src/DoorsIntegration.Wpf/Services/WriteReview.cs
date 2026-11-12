using System;
using System.Linq;
using System.Text;
using DoorsIntegration.Core;
namespace DoorsIntegration.Wpf.Services
{
    public sealed class WriteReview
    {
        public WriteReview(string title, string target, string body, DateTime expiresAtUtc, bool canApply)
        { Title = title; Target = target; Body = body; ExpiresAtUtc = expiresAtUtc; CanApply = canApply; }
        public string Title { get; }
        public string Target { get; }
        public string Body { get; }
        public DateTime ExpiresAtUtc { get; }
        public bool CanApply { get; }
        public static WriteReview From(RequirementWritePreview preview)
        {
            var request = preview.Request; var inspection = preview.Inspection; var schema = inspection.Schema;
            var text = new StringBuilder().AppendLine("This operation changes the DOORS database. No automatic rollback or retry.")
                .AppendLine("Module: " + request.Target.ModulePath).AppendLine("Database: " + schema.DatabaseId + "; Actor: " + schema.UserName)
                .AppendLine("Module ID: " + schema.QualifiedModuleId).AppendLine("Profile: " + DoorsVersions.Format(request.Target.ClientVersion) + "; Mode: " + request.Target.ConnectionMode)
                .AppendLine("Object: " + (request.AbsoluteNumber?.ToString() ?? "CREATE NEW") + "; Position: " + request.Position.Kind + "; Anchor: " + request.Position.AnchorAbsoluteNumber)
                .AppendLine("Required fields: " + string.Join(", ", request.RequiredAttributes)).AppendLine("Fingerprint: " + preview.ApprovalFingerprint)
                .AppendLine("Expires UTC: " + preview.ExpiresAtUtc.ToString("O"));
            foreach (var issue in schema.Issues) text.AppendLine(issue.Severity + ": " + issue.Code + " " + issue.AttributeName);
            foreach (var change in request.Changes)
            {
                var before = inspection.BeforeValues.FirstOrDefault(v => v.Name == change.Name);
                text.AppendLine("\nATTRIBUTE: " + change.Name + "; Format: " + change.Format);
                if (before != null) text.AppendLine("BEFORE PLAIN:\n" + before.PlainText).AppendLine("BEFORE RICH/OLE (raw):\n" + before.RichTextWithOle);
                text.AppendLine("REQUESTED:\n" + change.Value);
                if (change.ExpectedPlainText != null) text.AppendLine("EXPECTED PLAIN:\n" + change.ExpectedPlainText);
            }
            text.AppendLine("\nSteps: " + string.Join(" → ", inspection.Steps.Select(s => s.Kind + " " + s.AttributeName)));
            return new WriteReview("Review requirement write", request.Target.ModulePath, text.ToString(), preview.ExpiresAtUtc, preview.CanApply);
        }
        public static WriteReview From(FormalModuleCreationPreview preview) => new WriteReview("Review formal module creation",
            preview.Request.FullPath, "This operation creates a formal module. No automatic rollback or retry.\nTarget: " + preview.Request.FullPath +
            "\nDescription:\n" + preview.Request.Description + "\nDatabase: " + preview.Checks.DatabaseId + "; Actor: " + preview.Checks.UserName +
            "\nFolder ID: " + preview.Checks.FolderId + "\nProfile: " + DoorsVersions.Format(preview.Request.Target) + "; Mode: " + preview.Request.ConnectionMode +
            "\n" + preview.Checks.Summary + "\nFingerprint: " + preview.ApprovalFingerprint + "\nExpires UTC: " + preview.ExpiresAtUtc.ToString("O"),
            preview.ExpiresAtUtc, preview.CanCreate);
        public static WriteReview From(LinkWritePreview preview, bool removal = false)
        {
            var request = preview.Request; var inspection = preview.Inspection;
            string target = (removal ? "DELETE " : "") + request.DisplayTarget;
            var body = new StringBuilder().AppendLine(removal ? "SEPARATE REMOVAL APPROVAL: permanently remove this single link. Link deletion cannot be undone." :
                "Review link change: " + request.Action + ". Applying changes the DOORS database.")
                .AppendLine(request.DisplayTarget).AppendLine("Current versions only. Existing linkset required. No automatic rollback or retry.")
                .AppendLine("Database: " + inspection.DatabaseId + "; Actor: " + inspection.UserName)
                .AppendLine("Module IDs: " + inspection.SourceModuleId + " -> " + inspection.TargetModuleId + "; Link module: " + inspection.LinkModuleId)
                .AppendLine("Object identifiers: " + inspection.SourceIdentifier + " -> " + inspection.TargetIdentifier)
                .AppendLine("Profile: " + DoorsVersions.Format(request.ClientVersion) + "; Mode: " + request.ConnectionMode)
                .AppendLine("Exclusive source/link-module locks and object permissions are rechecked at commit.")
                .AppendLine("Trace scope: " + inspection.TraceabilityScope).AppendLine("Before edges: " + inspection.Before.Count)
                .AppendLine("Expected change: " + (request.Action == LinkWriteAction.Create ? "one edge added; none removed" : "one edge removed; none added"))
                .AppendLine("Fingerprint: " + preview.ApprovalFingerprint).AppendLine("Expires UTC: " + preview.ExpiresAtUtc.ToString("O"));
            foreach (var issue in inspection.Issues) body.AppendLine("BLOCKED: " + issue);
            foreach (var edge in inspection.Before) body.AppendLine("BEFORE: " + edge.TargetModulePath + " #" + edge.TargetAbsoluteNumber +
                "; ID=" + edge.TargetModuleId + "; " + edge.VersionKind + " " + edge.Version);
            return new WriteReview(removal ? "Explicit link removal approval" : "Review link change", target, body.ToString(),
                preview.ExpiresAtUtc, preview.CanApply && (!removal || request.Action == LinkWriteAction.Remove));
        }
    }

}
