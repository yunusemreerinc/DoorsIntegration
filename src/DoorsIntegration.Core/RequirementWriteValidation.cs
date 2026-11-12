using System;
using System.Collections.Generic;
using System.Linq;

namespace DoorsIntegration.Core
{
    // Metadata checks are repeated by native DXL under the edit lock. Numeric/date/range rules remain native.
    internal static class RequirementWriteValidation
    {
        public static RequirementWriteResult Validate(RequirementWriteRequest request, RequirementWriteResult inspection)
        {
            var issues = new List<RequirementWriteIssue>(inspection.Schema.Issues);
            void Error(RequirementWriteIssueCode code, string name)
            {
                if (!issues.Any(i => i.Code == code && i.AttributeName == name && i.Severity == RequirementWriteIssueSeverity.Error))
                    issues.Add(new RequirementWriteIssue(RequirementWriteIssueSeverity.Error, code, name));
            }
            foreach (var change in request.Changes)
            {
                var definition = inspection.Schema.Attributes.FirstOrDefault(a => a.Name == change.Name);
                if (definition == null) { Error(RequirementWriteIssueCode.AttributeMissing, change.Name); continue; }
                if (!definition.IsObjectAttribute || definition.IsGeneratedByDxl ||
                    (definition.IsSystem && change.Name != "Object Heading" && change.Name != "Object Text"))
                    Error(RequirementWriteIssueCode.UnsupportedAttribute, change.Name);
                if (change.Format == RequirementWriteFormat.DoorsRichText && definition.BaseType != "Text" && definition.BaseType != "String")
                    Error(RequirementWriteIssueCode.InvalidValue, change.Name);
                if (definition.BaseType == "Enumeration" && change.Value.Length != 0)
                {
                    var values = change.Value.Replace("\r\n", "\n").Split('\n');
                    if ((!definition.IsMultiValued && values.Length != 1) || values.Distinct(StringComparer.Ordinal).Count() != values.Length ||
                        values.Any(v => !definition.EnumerationValues.Contains(v, StringComparer.Ordinal))) Error(RequirementWriteIssueCode.InvalidValue, change.Name);
                }
            }
            foreach (string name in request.RequiredAttributes)
            {
                if (!inspection.Schema.Attributes.Any(a => a.Name == name)) Error(RequirementWriteIssueCode.AttributeMissing, name);
                var supplied = request.Changes.FirstOrDefault(c => c.Name == name);
                if ((request.IsCreate && supplied == null) || (supplied != null && string.IsNullOrWhiteSpace(supplied.ExpectedPlainText ?? supplied.Value)))
                    Error(RequirementWriteIssueCode.RequiredValueMissing, name);
            }
            var old = inspection.Schema;
            var schema = new RequirementModuleSchema(old.CorrelationId, old.ModulePath, old.DatabaseId, old.UserName, old.ModuleId, old.QualifiedModuleId, old.Attributes, issues);
            return new RequirementWriteResult(schema, inspection.BeforeState, inspection.Outcome, inspection.Persistence, inspection.Cleanup,
                inspection.AbsoluteNumber, inspection.ObjectIdentifier, inspection.ParentAbsoluteNumber, inspection.HierarchyLevel,
                inspection.Steps, inspection.AuditRecorded, inspection.BeforeValues);
        }
    }
}
