using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace DoorsIntegration.Core
{
    // Both comparison services use the same established opaque-rich-text/status comparison.
    internal static class BaselineAttributeComparison
    {
        internal static BaselineAttributeChange Compare(string name, RequirementValue before, RequirementValue after, bool oldLive, bool newLive, BaselineTextNormalization normalization)
            => BaselineComparisonResult.CompareSharedAttribute(name, before, after, oldLive, newLive, normalization);
    }
    public sealed class RequirementChangeDetector : IRequirementChangeDetector
    {
        public IReadOnlyList<RequirementChange> Detect(ImpactAnalysisSnapshot before, ImpactAnalysisSnapshot after, ImpactAnalysisPolicy policy, CancellationToken token = default(CancellationToken))
        {
            if (before.Links.Context.EnvironmentId != after.Links.Context.EnvironmentId || before.Links.Context.RepositoryId != after.Links.Context.RepositoryId || before.Links.Context.Source != after.Links.Context.Source) throw new ArgumentException("Snapshots have different repository/environment/source contexts.");
            var left = Index(before); var right = Index(after);
            var common = new HashSet<string>(left.Keys.Intersect(right.Keys).Where(k => !left[k].IsDeleted && !right[k].IsDeleted), StringComparer.Ordinal);
            var leftRank = Ranks(left.Values.Where(v => common.Contains(v.RequirementIdentity)));
            var rightRank = Ranks(right.Values.Where(v => common.Contains(v.RequirementIdentity)));
            var result = new List<RequirementChange>();
            foreach (string id in left.Keys.Union(right.Keys).OrderBy(x => x, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested(); left.TryGetValue(id, out var a); right.TryGetValue(id, out var b);
                bool oldLive = a != null && !a.IsDeleted, newLive = b != null && !b.IsDeleted;
                bool beforeComplete = Complete(before, (a ?? b).Version.ModuleIdentity), afterComplete = Complete(after, (b ?? a).Version.ModuleIdentity);
                var c = new RequirementChange { Identity = id, Before = a, After = b };
                if (!beforeComplete || !afterComplete) c.Flags |= RequirementChangeFlags.Unknown;
                if (!oldLive && newLive) c.Flags |= beforeComplete ? RequirementChangeFlags.Added : RequirementChangeFlags.Unknown;
                if (oldLive && !newLive) c.Flags |= afterComplete ? RequirementChangeFlags.Deleted : RequirementChangeFlags.Unknown;
                if ((a != null && a.Access != BaselineVerificationReadState.Verified) || (b != null && b.Access != BaselineVerificationReadState.Verified)) c.Flags |= RequirementChangeFlags.Unknown;
                if (oldLive && newLive)
                {
                    if (!a.ParentKnown || !b.ParentKnown || !a.Position.HasValue || !b.Position.HasValue) c.Flags |= RequirementChangeFlags.Unknown;
                    else if (a.ParentAbsoluteNumber != b.ParentAbsoluteNumber || leftRank[id] != rightRank[id]) c.Flags |= RequirementChangeFlags.Moved;
                }
                foreach (string name in new[] { "Object Heading", "Object Text" }.Concat(policy.CustomAttributes).Distinct().OrderBy(x => x, StringComparer.Ordinal))
                {
                    ImpactAttributeValue av = null, bv = null; a?.Attributes.TryGetValue(name, out av); b?.Attributes.TryGetValue(name, out bv);
                    var attribute = Compare(name, av, bv, oldLive, newLive, policy); c.Attributes.Add(attribute);
                    if ((oldLive || newLive) && !attribute.IsComparable) c.Flags |= RequirementChangeFlags.Unknown;
                    if (oldLive && newLive && (attribute.SemanticChanged || attribute.TypeChanged)) c.Flags |= name == "Object Heading" || name == "Object Text" ? RequirementChangeFlags.Content : RequirementChangeFlags.Attribute;
                    if (attribute.NormalizationOnly) c.Flags |= RequirementChangeFlags.NormalizationOnly;
                }
                if (oldLive && newLive && c.Attributes.Any(v => v.RichRepresentationChanged) && !c.Attributes.Any(v => v.SemanticChanged || v.TypeChanged)) c.Flags |= RequirementChangeFlags.FormattingOnly;
                SetPrimary(c); new ImpactClassificationService().Classify(c, policy); result.Add(c);
            }
            return result;
        }
        private static Dictionary<string, ImpactRequirementVersion> Index(ImpactAnalysisSnapshot snapshot)
        {
            var result = new Dictionary<string, ImpactRequirementVersion>(StringComparer.Ordinal);
            foreach (var r in snapshot.Requirements)
            {
                if (snapshot.ComparedModuleIdentities.Count > 0 && !snapshot.ComparedModuleIdentities.Contains(r.Version.ModuleIdentity)) continue;
                if (r.Version == null || !r.Version.IdentityVerified || r.Version.RepositoryId != snapshot.Links.Context.RepositoryId) throw new ArgumentException("Unverified/mismatched module/object identity in impact snapshot.");
                if (result.ContainsKey(r.RequirementIdentity)) throw new ArgumentException("Duplicate module/object identity or multiple versions in one selected scope.");
                result.Add(r.RequirementIdentity, r);
            }
            return result;
        }
        private static Dictionary<string, int> Ranks(IEnumerable<ImpactRequirementVersion> values)
        {
            var result = new Dictionary<string, int>();
            foreach (var group in values.GroupBy(v => GraphGuard.Hash(v.Version.ModuleIdentity, v.ParentAbsoluteNumber?.ToString(CultureInfo.InvariantCulture) ?? "root")))
            { int rank = 0; foreach (var v in group.OrderBy(v => v.Position).ThenBy(v => v.AbsoluteNumber)) result[v.RequirementIdentity] = rank++; }
            return result;
        }
        internal static bool Complete(ImpactAnalysisSnapshot snapshot, string module) => !snapshot.IsStale && !snapshot.Links.Truncated && !snapshot.Links.ContextChanged && snapshot.Links.Context.MetadataComplete && snapshot.Links.Context.Modules.Any(m => m.ModuleIdentity == module && m.State == BaselineVerificationReadState.Verified && m.InventoryComplete);
        internal static void SetPrimary(RequirementChange c)
        {
            var f = c.Flags;
            c.PrimaryType = f.HasFlag(RequirementChangeFlags.Added) ? RequirementChangeType.Added : f.HasFlag(RequirementChangeFlags.Deleted) ? RequirementChangeType.Deleted :
                (f & (RequirementChangeFlags.Content | RequirementChangeFlags.FormattingOnly)) != 0 ? RequirementChangeType.Modified : f.HasFlag(RequirementChangeFlags.Attribute) ? RequirementChangeType.AttributeChanged :
                f.HasFlag(RequirementChangeFlags.Moved) ? RequirementChangeType.Moved : f.HasFlag(RequirementChangeFlags.LinkTargetChanged) ? RequirementChangeType.LinkTargetChanged :
                f.HasFlag(RequirementChangeFlags.LinkRemoved) ? RequirementChangeType.LinkRemoved : f.HasFlag(RequirementChangeFlags.LinkAdded) ? RequirementChangeType.LinkAdded :
                f.HasFlag(RequirementChangeFlags.Unknown) ? RequirementChangeType.Unknown : RequirementChangeType.Unchanged;
        }
        private static AttributeChange Compare(string name, ImpactAttributeValue a, ImpactAttributeValue b, bool oldLive, bool newLive, ImpactAnalysisPolicy policy)
        {
            var raw = BaselineAttributeComparison.Compare(name, a?.ToValue(), b?.ToValue(), oldLive, newLive, BaselineTextNormalization.UnicodeNfcAndLineEndings);
            var result = new AttributeChange { Name = name, Before = a, After = b, IsComparable = raw.IsConclusive, SemanticChanged = raw.PlainTextChanged, RichRepresentationChanged = raw.RichTextChanged, TypeChanged = raw.TypeChanged, NormalizationOnly = raw.HasNormalizationDifference };
            if (!oldLive || !newLive) return result;
            if (a?.PlainStatus == RequirementValueStatus.Readable && b?.PlainStatus == RequirementValueStatus.Readable)
            {
                var kind = policy.AttributeKinds.TryGetValue(name, out var configured) ? configured : Infer(a.TypeName);
                bool first = TryCanonical(a.PlainText, kind, out string av), second = TryCanonical(b.PlainText, kind, out string bv);
                if (first && second && !raw.TypeChanged) { result.SemanticChanged = av != bv; result.NormalizationOnly |= !result.SemanticChanged && a.PlainText != b.PlainText; }
                else { result.IsComparable = false; result.Reason = "Attribute type/value cannot be compared conclusively; raw values retained."; }
            }
            if (!result.IsComparable && result.Reason == null) result.Reason = "Attribute text/rich representation or type is unavailable; not silently unchanged.";
            return result;
        }
        private static ImpactValueKind Infer(string type)
        {
            switch ((type ?? "").ToLowerInvariant())
            {
                case "text": case "string": return ImpactValueKind.Text;
                case "integer": case "int": return ImpactValueKind.Integer;
                case "real": case "double": return ImpactValueKind.Real;
                case "boolean": case "bool": return ImpactValueKind.Boolean;
                case "date": return ImpactValueKind.Date;
                case "enumeration": case "enum": return ImpactValueKind.Enum;
                default: return ImpactValueKind.Unknown;
            }
        }
        private static bool TryCanonical(string text, ImpactValueKind kind, out string value)
        {
            value = null; if (text == null) return false;
            switch (kind)
            {
                case ImpactValueKind.Text: value = BaselineComparisonGuard.Normalize(text, BaselineTextNormalization.UnicodeNfcAndLineEndings); return true;
                case ImpactValueKind.Enum: value = text; return true;
                case ImpactValueKind.MultiEnum: value = string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Distinct().OrderBy(x => x, StringComparer.Ordinal)); return true;
                case ImpactValueKind.Integer: if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer)) { value = integer.ToString(CultureInfo.InvariantCulture); return true; } return false;
                case ImpactValueKind.Real: if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number)) { value = number.ToString("G29", CultureInfo.InvariantCulture); return true; } return false;
                case ImpactValueKind.Boolean: if (bool.TryParse(text, out bool flag)) { value = flag.ToString(); return true; } return false;
                case ImpactValueKind.Date: if (DateTimeOffset.TryParseExact(text, new[] { "O", "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) { value = date.UtcDateTime.ToString("O"); return true; } return false;
                default: return false;
            }
        }
    }
}
