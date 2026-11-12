using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public sealed class MockChangeImpactSnapshotProvider : IImpactSnapshotProvider
    {
        public MockChangeImpactSnapshotProvider(int requirementsPerType = 1, int fanout = 1)
        {
            var fixture = new MockBaselineSetVerificationAdapter(requirementsPerType, fanout).Fixture.Copy();
            Before = Convert(fixture); After = ImpactSerialization.Clone(Before); Before.Label = "A"; After.Label = "B";
            SetAttribute(After.Requirements.First(v => v.Version.ModuleIdentity == "SYS"), "Object Text", "The system shall validate the revised input.");
        }
        public ImpactAnalysisSnapshot Before { get; }
        public ImpactAnalysisSnapshot After { get; }
        public Task<ImpactAnalysisSnapshot> CaptureAsync(ImpactSnapshotSelection selection, ImpactAnalysisPolicy policy, IProgress<string> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); progress?.Report("Mock impact snapshot " + selection.MockSnapshotKey + "; no native integration evidence.");
            var s = ImpactSerialization.Clone(selection.MockSnapshotKey == "A" ? Before : selection.MockSnapshotKey == "B" ? After : throw new ArgumentException("Choose Mock snapshot A or B explicitly."));
            s.Label = selection.Label; s.ComparedModuleIdentities = selection.BaselineSet != null ? s.Links.Context.Modules.Select(m => m.ModuleIdentity).ToList() : selection.Modules.Select(m => m.ModuleIdentity).ToList();
            var versions = s.Links.Context.Modules.ToDictionary(m => m.ModuleIdentity, m => m.Version);
            var observedVersions = new Dictionary<string, BaselineVersionReference>(versions);
            if (selection.BaselineSet != null && selection.MockSnapshotKey == "B")
                foreach (string id in versions.Keys.ToArray()) { var v = versions[id]; versions[id] = new BaselineVersionReference(v.RepositoryId, v.ModuleIdentity, v.FullPath, v.Major, v.Minor + 1, v.Suffix); }
            foreach (var v in selection.Modules) versions[v.ModuleIdentity] = v;
            if (selection.BaselineSet != null) { s.Links.Context.Set.Id = selection.BaselineSet.SetId; s.Links.Context.Definition.Id = selection.BaselineSet.DefinitionId; s.Links.Context.Set.DefinitionId = selection.BaselineSet.DefinitionId; }
            foreach (var m in s.Links.Context.Modules) { m.Version = versions[m.ModuleIdentity]; m.FullPath = m.Version.FullPath; }
            foreach (var r in s.Requirements) r.Version = versions[r.Version.ModuleIdentity];
            foreach (var l in s.Links.Links) { if (l.SourceVersion != null && observedVersions.TryGetValue(l.SourceVersion.ModuleIdentity, out var oldSource) && l.SourceVersion.ModuleBaselineIdentity == oldSource.ModuleBaselineIdentity) l.SourceVersion = versions[l.SourceVersion.ModuleIdentity]; if (l.TargetVersion != null && observedVersions.TryGetValue(l.TargetVersion.ModuleIdentity, out var oldTarget) && l.TargetVersion.ModuleBaselineIdentity == oldTarget.ModuleBaselineIdentity) l.TargetVersion = versions[l.TargetVersion.ModuleIdentity]; }
            if (selection.BaselineSet != null)
            {
                if (selection.MockSnapshotKey == "B") s.Links.Context.Set.Version = new NativeBaselineSetVersion(3, 0, "IMPACT");
                foreach (var member in s.Links.Context.Set.Members) if (versions.TryGetValue(member.Module.ModuleId, out var v)) { member.ModuleBaselineVersion = v.Version; member.BaselineIdentity = v.ModuleBaselineIdentity; }
            }
            else { s.Links.Context.Set = null; s.Links.Context.Definition = null; foreach (var m in s.Links.Context.Modules) m.InSet = false; }
            s.Links.Context.ObservedVersionBindings = versions.Values.ToList();
            s.Links.Requirements = s.Requirements.Select(v => ServiceImpactSnapshotProvider.ToBaseline(v, policy)).ToList();
            return Task.FromResult(s);
        }
        public static ImpactAnalysisSnapshot Convert(BaselineLinkSnapshot snapshot)
        {
            foreach (var m in snapshot.Context.Modules) m.SelectedForVerification = true;
            var result = new ImpactAnalysisSnapshot { CapturedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Links = snapshot, Label = "Mock" };
            foreach (var r in snapshot.Requirements)
            {
                var v = new ImpactRequirementVersion { Version = r.Version, AbsoluteNumber = r.AbsoluteNumber, DisplayIdentifier = r.DisplayIdentifier, ParentKnown = true, Position = r.AbsoluteNumber, Access = r.Access, IsDeleted = r.IsDeleted };
                SetAttribute(v, "Object Heading", r.Heading); SetAttribute(v, "Object Text", r.Text); v.Attributes["Object Text"].RichText = r.RichText; v.Attributes["Object Heading"].RichText = r.RichHeading;
                v.Attributes["Object Text"].RichStatus = r.RichText == null ? RequirementValueStatus.NotApplicable : RequirementValueStatus.Readable;
                v.Attributes["Object Heading"].RichStatus = r.RichHeading == null ? RequirementValueStatus.NotApplicable : RequirementValueStatus.Readable;
                SetAttribute(v, "Requirement Type", r.RequirementType); SetAttribute(v, "Status", r.RequirementStatus); SetAttribute(v, "Verification Method", "Test"); SetAttribute(v, "Safety Level", "B"); SetAttribute(v, "Priority", "2"); result.Requirements.Add(v);
            }
            return result;
        }
        public static void SetAttribute(ImpactRequirementVersion requirement, string name, string text, string type = "Text")
        {
            bool rich = requirement.Attributes.TryGetValue(name, out var existing) && existing.RichStatus == RequirementValueStatus.Readable;
            requirement.Attributes[name] = new ImpactAttributeValue { Name = name, TypeName = type, PlainText = text, PlainStatus = RequirementValueStatus.Readable,
                RichStatus = rich ? RequirementValueStatus.Readable : RequirementValueStatus.NotApplicable,
                RichText = rich ? "{\\rtf1 " + text.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}") + "}" : null };
        }
        public static ChangeImpactRequest DemoRequest(ImpactAnalysisPolicy policy = null, ChangeImpactComparisonKind kind = ChangeImpactComparisonKind.ModuleBaselines)
        {
            var mock = new MockBaselineSetVerificationAdapter(); var context = mock.Fixture.Context; var sys = context.Modules.First(m => m.ModuleIdentity == "SYS").Version;
            var a = new ImpactSnapshotSelection { Label = "Baseline A", MockSnapshotKey = "A", EnvironmentId = context.EnvironmentId, RepositoryId = context.RepositoryId, Modules = new List<BaselineVersionReference> { sys }, LinkModulePaths = new List<string> { MockBaselineSetVerificationAdapter.DemoLinkPath } };
            var b = new ImpactSnapshotSelection { Label = "Baseline B", MockSnapshotKey = "B", EnvironmentId = context.EnvironmentId, RepositoryId = context.RepositoryId, Modules = new List<BaselineVersionReference> { kind == ChangeImpactComparisonKind.BaselineToCurrent ? new BaselineVersionReference(sys.RepositoryId, sys.ModuleIdentity, sys.FullPath, 0, 0, "", true) : new BaselineVersionReference(sys.RepositoryId, sys.ModuleIdentity, sys.FullPath, 3, 0, "R1") }, LinkModulePaths = new List<string> { MockBaselineSetVerificationAdapter.DemoLinkPath } };
            if (kind == ChangeImpactComparisonKind.BaselineSets)
            {
                a.BaselineSet = MockBaselineSetVerificationAdapter.DemoRequest();
                b.BaselineSet = new BaselineSetVerificationRequest(a.BaselineSet.ProjectId, a.BaselineSet.FolderPath, a.BaselineSet.DefinitionId, "Mock-Set-B", a.BaselineSet.Policy, a.BaselineSet.LinkModulePaths);
            }
            return new ChangeImpactRequest(a, b, policy ?? new ImpactAnalysisPolicy(), kind);
        }
    }
}
