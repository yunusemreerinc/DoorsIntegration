using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    public enum BaselineVerificationMockScenario { CompleteChain, MissingHlrLlr, WrongTargetVersion, InaccessibleTarget, MissingModuleBaseline, OpenComplete, OpenPartial, ValidExternalLink, EchoedExternalLink, TwoVersions, Cycle, AccessDenied, RiskyShareable, DuplicateLink }
    // Read fixtures only. No Native/Independent executor, write grant or mutation API is used.
    public sealed class MockBaselineSetVerificationAdapter : IBaselineSetVerificationSource, IBaselineLinkResolver
    {
        public const string DemoDefinitionId = "mock-verification-definition";
        public const string DemoSetId = "mock-verification-set-2.0";
        public const string DemoLinkPath = "/Traceability/Standard links";
        public const string DemoLinkId = "mock-standard-link-module";
        private readonly MockNativeBaselineSetAdapter native;
        public MockBaselineSetVerificationAdapter(int requirementsPerType = 1, int fanOut = 1, BaselineVerificationMockScenario scenario = BaselineVerificationMockScenario.CompleteChain, MockNativeBaselineSetAdapter native = null)
        { if (requirementsPerType < 1 || requirementsPerType > 25000 || fanOut < 1 || fanOut > requirementsPerType) throw new ArgumentOutOfRangeException(); this.native = native; Fixture = Demo(requirementsPerType, fanOut); Apply(scenario); }
        public BaselineMetadataSource Source => BaselineMetadataSource.Mock;
        public BaselineLinkSnapshot Fixture { get; }
        public int ContextReads { get; private set; }
        public bool ChangeContextDuringRead { get; set; }
        public static TraceabilityPolicy DemoPolicy(TraceabilityCycleBehavior cycles = TraceabilityCycleBehavior.Report) => new TraceabilityPolicy("Mock SYS-HLR-LLR-TEST",
            new[] { new TraceabilityLinkRule("SYS-HLR", "SYS", new[] { "HLR" }, DemoLinkPath), new TraceabilityLinkRule("HLR-LLR", "HLR", new[] { "LLR" }, DemoLinkPath), new TraceabilityLinkRule("LLR-TEST", "LLR", new[] { "TEST" }, DemoLinkPath) },
            new[] { new TraceabilityCycleRule(behavior: cycles) }, new[] { "SYS", "HLR", "LLR", "TEST" });
        public static BaselineSetVerificationRequest DemoRequest(BaselineLinkSnapshot reference = null, TraceabilityPolicy policy = null, IEnumerable<string> selected = null, int maxRequirements = 100000, int maxLinks = 250000) =>
            new BaselineSetVerificationRequest("Mock project", "/Traceability", DemoDefinitionId, DemoSetId, policy ?? DemoPolicy(), new[] { DemoLinkPath }, selected,
                new Dictionary<string, string> { ["EXT"] = "HLR" }, reference, maxRequirements: maxRequirements, maxLinks: maxLinks);
        public async Task<BaselineVerificationContext> ReadContextAsync(BaselineSetVerificationRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); ContextReads++;
            var context = request.DefinitionId == DemoDefinitionId ? BaselineSetSerialization.Clone(Fixture.Context) : await Existing(request, token).ConfigureAwait(false);
            if (ChangeContextDuringRead) context.Revision = ContextReads.ToString(CultureInfo.InvariantCulture);
            return context;
        }
        private async Task<BaselineVerificationContext> Existing(BaselineSetVerificationRequest request, CancellationToken token)
        {
            if (native == null) return new BaselineVerificationContext { Source = Source, EnvironmentId = "Mock", RepositoryId = "mock-verification-repository", Reason = "No selected Mock definition/set; use the read-only demo or select a Native Mock set." };
            var inspection = await native.InspectAsync(new BaselineSetRequest(BaselineSetAction.UpdateDefinition, request.FolderPath, request.DefinitionId, request.SetId, target: request.Target), token).ConfigureAwait(false);
            var d = inspection.Definitions.SingleOrDefault(x => x.Id == request.DefinitionId); var set = inspection.Sets.SingleOrDefault(s => s.Id == request.SetId);
            var result = new BaselineVerificationContext { Source = Source, EnvironmentId = inspection.DatabaseId, RepositoryId = d?.Modules.FirstOrDefault()?.RepositoryId ?? "mock", ClientVersion = inspection.ClientVersion,
                Operator = inspection.UserId, Revision = inspection.Revision, MetadataComplete = inspection.Complete && inspection.CanRead && d != null && set != null, Definition = d, Set = set, Reason = inspection.Reason };
            int order = 0;
            foreach (var m in d?.Modules ?? new List<BaselineSetModuleReference>())
            {
                var member = set?.Members.SingleOrDefault(x => x.Module.ModuleId == m.ModuleId); BaselineVersionReference version = null;
                if (member != null)
                { var match = Regex.Match(member.ModuleBaselineVersion ?? "", @"^(\d+)\.(\d+)\[(.*)\]$"); if (match.Success) version = new BaselineVersionReference(m.RepositoryId, m.ModuleId, m.FullPath, int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), match.Groups[3].Value); }
                var shareable = native.ModulePolicies.TryGetValue(m.ModuleId, out var policy) ? policy.ShareableConfigured : BaselineCheckState.Failed;
                result.Modules.Add(new ModuleBaselineVerificationResult { ModuleIdentity = m.ModuleId, FullPath = m.FullPath, InDefinition = true, InSet = member != null, Version = version, RequirementType = new[] { "SYS", "HLR", "LLR", "TEST" }[Math.Min(order++, 3)],
                    State = !inspection.CanRead ? BaselineVerificationReadState.AccessDenied : version != null ? BaselineVerificationReadState.Verified : member == null && result.MetadataComplete ? BaselineVerificationReadState.Missing : BaselineVerificationReadState.Unknown,
                    ShareableConfiguration = shareable == BaselineCheckState.Failed ? BaselineShareableConfiguration.NotConfigured : shareable == BaselineCheckState.Passed ? BaselineShareableConfiguration.Configured : BaselineShareableConfiguration.Unknown });
                if (version != null) result.ObservedVersionBindings.Add(version);
            }
            result.LinkModuleIdentities[DemoLinkPath] = DemoLinkId; return result;
        }
        public Task<BaselineLinkSnapshot> ResolveAsync(BaselineSetVerificationRequest request, BaselineVerificationContext context, IProgress<string> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var captured = request.DefinitionId == DemoDefinitionId ? BaselineVerificationCopy.Snapshot(Fixture) : ExistingSnapshot(context);
            captured.Context = context; captured.SnapshotId = Guid.NewGuid(); captured.CapturedAtUtc = DateTime.UtcNow;
            var selected = new HashSet<string>(context.Modules.Where(m => m.SelectedForVerification).Select(m => m.ModuleIdentity));
            captured.Requirements = captured.Requirements.Where(r => selected.Contains(r.Version.ModuleIdentity)).ToList();
            captured.Links = captured.Links.Where(l => selected.Contains(l.SourceVersion?.ModuleIdentity ?? "") || selected.Contains(l.TargetVersion?.ModuleIdentity ?? "")).ToList();
            if (captured.Requirements.Count > request.MaxRequirements || captured.Links.Count > request.MaxLinks)
            { captured.Truncated = true; captured.Requirements = captured.Requirements.Take(request.MaxRequirements).ToList(); captured.Links = captured.Links.Take(request.MaxLinks).ToList(); foreach (var m in context.Modules.Where(m => m.SelectedForVerification)) { m.InventoryComplete = false; m.LinksComplete = false; } captured.Limitations.Add("Mock explicit capture bounds reached."); }
            if (request.DefinitionId == DemoDefinitionId)
                foreach (var m in context.Modules) { var fixture = Fixture.Context.Modules.Single(x => x.ModuleIdentity == m.ModuleIdentity); m.InventoryComplete = !captured.Truncated && fixture.InventoryComplete; m.LinksComplete = !captured.Truncated && fixture.LinksComplete; }
            progress?.Report("MOCK only: captured " + captured.Requirements.Count + " requirements / " + captured.Links.Count + " observations");
            return Task.FromResult(captured);
        }
        private static BaselineLinkSnapshot ExistingSnapshot(BaselineVerificationContext context)
        {
            var s = new BaselineLinkSnapshot { Context = context }; var available = context.Modules.Where(m => m.State == BaselineVerificationReadState.Verified && m.Version != null).ToArray();
            foreach (var m in available) { m.InventoryComplete = m.LinksComplete = true; m.KnownObjectCount = 1; s.Requirements.Add(Requirement(m.Version, 1, m.RequirementType)); }
            for (int i = 0; i + 1 < available.Length; i++) s.Links.Add(Link(available[i].Version, 1, available[i + 1].Version, 1, "native-mock-fixture-" + i));
            s.Limitations.Add("Mock Native set metadata is reused; requirement/link content is an explicit synthetic fixture, not native DXL evidence."); return s;
        }
        private static BaselineLinkSnapshot Demo(int count, int fanOut)
        {
            var context = new BaselineVerificationContext { Source = BaselineMetadataSource.Mock, EnvironmentId = "mock-verification-database", RepositoryId = "mock-verification-repository", Operator = "mock-reviewer", ClientVersion = "Mock (not DOORS)", Revision = "fixture-1", MetadataComplete = true,
                Definition = new BaselineSetDefinitionDetails { Id = DemoDefinitionId, FolderPath = "/Traceability", Name = "SYS-HLR-LLR-TEST review", IdentityEvidence = "Mock stable identities" },
                Set = new NativeBaselineSetDetails { Id = DemoSetId, DefinitionId = DemoDefinitionId, Version = new NativeBaselineSetVersion(2, 0, "REVIEW"), IsOpen = false, Annotation = "Read-only verification fixture", Creator = "mock-reviewer", CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) } };
            context.LinkModuleIdentities[DemoLinkPath] = DemoLinkId;
            var snapshot = new BaselineLinkSnapshot { Context = context, CapturedLinkModuleIdentities = new List<string> { DemoLinkId } }; string[] types = { "SYS", "HLR", "LLR", "TEST" };
            for (int i = 0; i < types.Length; i++)
            {
                var version = new BaselineVersionReference(context.RepositoryId, types[i], "/Traceability/" + types[i], i == 0 ? 2 : 1, i, "R" + i);
                context.ObservedVersionBindings.Add(version); context.ObservedVersionLabels[ServiceBaselineLinkResolver.LabelKey(version.FullPath, LinkVersionKind.Baseline, version.Version)] = version.ModuleBaselineIdentity;
                context.Definition.Modules.Add(new BaselineSetModuleReference(new BaselineModuleReference(context.RepositoryId, types[i], version.FullPath)));
                context.Set.Members.Add(new BaselineSetModuleBaseline { Module = context.Definition.Modules.Last(), ModuleBaselineVersion = version.Version, BaselineIdentity = version.ModuleBaselineIdentity, Resolution = "Verified" });
                context.Modules.Add(new ModuleBaselineVerificationResult { ModuleIdentity = types[i], FullPath = version.FullPath, Version = version, InDefinition = true, InSet = true, State = BaselineVerificationReadState.Verified, RequirementType = types[i], InventoryComplete = true, LinksComplete = true, KnownObjectCount = count, ShareableConfiguration = BaselineShareableConfiguration.NotConfigured });
                for (int n = 1; n <= count; n++) snapshot.Requirements.Add(Requirement(version, n, types[i]));
            }
            for (int layer = 0; layer < 3; layer++) for (int n = 1; n <= count; n++) for (int offset = 0; offset < fanOut; offset++)
                snapshot.Links.Add(Link(context.Modules[layer].Version, n, context.Modules[layer + 1].Version, (n - 1 + offset) % count + 1, "physical-" + layer + "-" + n + "-" + offset));
            return snapshot;
        }
        private void Apply(BaselineVerificationMockScenario scenario)
        {
            var c = Fixture.Context;
            if (scenario == BaselineVerificationMockScenario.MissingHlrLlr) Fixture.Links.RemoveAll(l => l.SourceVersion.ModuleIdentity == "HLR");
            if (scenario == BaselineVerificationMockScenario.WrongTargetVersion || scenario == BaselineVerificationMockScenario.TwoVersions)
            { var link = scenario == BaselineVerificationMockScenario.TwoVersions ? Link(c.Modules[0].Version, 1, c.Modules[0].Version, 1, "older-version") : Fixture.Links.First(l => l.TargetVersion.ModuleIdentity == "LLR");
                var original = link.TargetVersion; link.TargetVersion = new BaselineVersionReference(original.RepositoryId, original.ModuleIdentity, original.FullPath, 0, 9, "OLD"); if (scenario == BaselineVerificationMockScenario.TwoVersions) Fixture.Links.Add(link); }
            if (scenario == BaselineVerificationMockScenario.MissingModuleBaseline || scenario == BaselineVerificationMockScenario.OpenPartial)
            { c.Modules[2].State = BaselineVerificationReadState.Missing; c.Modules[2].InSet = false; c.Modules[2].Version = null; c.Modules[2].InventoryComplete = c.Modules[2].LinksComplete = false; c.Set.Members.RemoveAt(2); Fixture.Requirements.RemoveAll(n => n.Version.ModuleIdentity == "LLR"); }
            if (scenario == BaselineVerificationMockScenario.OpenComplete || scenario == BaselineVerificationMockScenario.OpenPartial) c.Set.IsOpen = true;
            if (scenario == BaselineVerificationMockScenario.InaccessibleTarget || scenario == BaselineVerificationMockScenario.AccessDenied)
            { c.Modules[2].State = BaselineVerificationReadState.AccessDenied; c.Modules[2].Reason = "Mock permission denied"; c.Modules[2].InventoryComplete = c.Modules[2].LinksComplete = false; Fixture.Requirements.RemoveAll(n => n.Version.ModuleIdentity == "LLR"); foreach (var l in Fixture.Links.Where(l => l.TargetVersion.ModuleIdentity == "LLR")) l.TargetAccess = BaselineVerificationReadState.AccessDenied; foreach (var l in Fixture.Links.Where(l => l.SourceVersion.ModuleIdentity == "LLR")) l.SourceAccess = BaselineVerificationReadState.AccessDenied; }
            if (scenario == BaselineVerificationMockScenario.ValidExternalLink || scenario == BaselineVerificationMockScenario.EchoedExternalLink)
            { var external = new BaselineVersionReference(c.RepositoryId, "EXT", "/External/HLR", 4, 2, "EXT"); c.ObservedVersionBindings.Add(external); var link = Link(c.Modules[0].Version, 1, external, 1, "external-physical"); link.IsEchoed = scenario == BaselineVerificationMockScenario.EchoedExternalLink; Fixture.Links.Add(link); }
            if (scenario == BaselineVerificationMockScenario.Cycle) Fixture.Links.Add(Link(c.Modules[3].Version, 1, c.Modules[0].Version, 1, "cycle"));
            if (scenario == BaselineVerificationMockScenario.RiskyShareable) foreach (var m in c.Modules) m.ShareableConfiguration = BaselineShareableConfiguration.Configured;
            if (scenario == BaselineVerificationMockScenario.DuplicateLink) { var copy = BaselineSetSerialization.Clone(Fixture.Links[0]); copy.ObservationId = "duplicate-observation"; copy.PhysicalLinkIdentity = "another-physical-link"; Fixture.Links.Add(copy); }
        }
        private static BaselineRequirementInfo Requirement(BaselineVersionReference v, int absolute, string type) => new BaselineRequirementInfo { Version = v, AbsoluteNumber = absolute, DisplayIdentifier = "REQ-" + absolute, RequirementType = type, RequirementStatus = "Approved", Heading = type + " requirement " + absolute, Text = "The system shall satisfy requirement " + absolute + ".", RichText = @"{\rtf1\ansi The system \b shall\b0  satisfy this requirement.}", Access = BaselineVerificationReadState.Verified };
        private static BaselineLinkInfo Link(BaselineVersionReference source, int from, BaselineVersionReference target, int to, string physical) => new BaselineLinkInfo { ObservationId = physical, SourceVersion = source, TargetVersion = target, SourcePath = source.FullPath, TargetPath = target.FullPath, SourceObject = from, TargetObject = to, LinkModuleIdentity = DemoLinkId, LinkModulePath = DemoLinkPath, LinkType = "Standard", Direction = LinkDirection.Outgoing, SourceAccess = BaselineVerificationReadState.Verified, TargetAccess = BaselineVerificationReadState.Verified, PhysicalLinkIdentity = physical, PhysicalMultiplicityVerified = true, Evidence = "Explicit Mock fixture; not native integration evidence" };
    }
    public interface IBaselineSetVerificationMetadataProvider
    { Task<BaselineVerificationContext> BindAsync(BaselineSetVerificationRequest request, BaselineSetInspection inspection, CancellationToken token); }
    public sealed class UnvalidatedBaselineSetVerificationMetadataProvider : IBaselineSetVerificationMetadataProvider
    {
        public Task<BaselineVerificationContext> BindAsync(BaselineSetVerificationRequest request, BaselineSetInspection inspection, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var definition = inspection.Definitions.SingleOrDefault(d => d.Id == request.DefinitionId); var set = inspection.Sets.SingleOrDefault(s => s.Id == request.SetId);
            return Task.FromResult(new BaselineVerificationContext { Source = inspection.Source, EnvironmentId = inspection.DatabaseId, RepositoryId = inspection.RepositoryId, ClientVersion = inspection.ClientVersion, Operator = inspection.UserId, Revision = inspection.Revision,
                Definition = definition, Set = set, MetadataComplete = false, Reason = "NOT_RUN: release-specific baseline membership, typed version labels, native identities and shareable-configuration evidence is not validated. " + inspection.Reason,
                Modules = (definition?.Modules ?? new List<BaselineSetModuleReference>()).Select(m => new ModuleBaselineVerificationResult { ModuleIdentity = m.ModuleId, FullPath = m.FullPath, State = BaselineVerificationReadState.Unknown, Reason = "Unverified native membership/access; absence is not Missing." }).ToList() });
        }
    }
    public sealed class NativeBaselineSetVerificationSource : IBaselineSetVerificationSource
    {
        private readonly IBaselineSetAdapter adapter; private readonly IBaselineSetVerificationMetadataProvider metadata;
        public NativeBaselineSetVerificationSource(IBaselineSetAdapter adapter, IBaselineSetVerificationMetadataProvider metadata) { this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter)); this.metadata = metadata ?? throw new ArgumentNullException(nameof(metadata)); }
        public BaselineMetadataSource Source => adapter.Source;
        public async Task<BaselineVerificationContext> ReadContextAsync(BaselineSetVerificationRequest request, CancellationToken token)
        { var inspection = await adapter.InspectAsync(new BaselineSetRequest(BaselineSetAction.UpdateDefinition, request.FolderPath, request.DefinitionId, request.SetId, target: request.Target), token).ConfigureAwait(false); return await metadata.BindAsync(request, inspection, token).ConfigureAwait(false); }
    }
}
