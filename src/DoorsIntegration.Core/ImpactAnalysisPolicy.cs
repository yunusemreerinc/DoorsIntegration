using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace DoorsIntegration.Core
{
    [DataContract]
    public sealed class ImpactPropagationRule
    {
        [DataMember] public string SourceType { get; set; } = "*";
        [DataMember] public string TargetType { get; set; } = "*";
        [DataMember] public string LinkModuleIdentity { get; set; } = "*";
        [DataMember] public string LinkType { get; set; } = "*";
        [DataMember] public TraceabilityDirection Direction { get; set; } = TraceabilityDirection.Outgoing;
    }
    [DataContract]
    public sealed class ImpactAnalysisPolicy
    {
        public ImpactAnalysisPolicy() { Defaults(default(StreamingContext)); }
        [OnDeserializing] private void Defaults(StreamingContext unused)
        {
            Id = "project-impact-default"; MaximumDepth = 8; MaximumNodes = 100000; MaximumPaths = 20000; MaximumPathsPerRequirement = 3;
            TimeoutSeconds = 120; MaximumRequirements = 100000; MaximumLinks = 250000; MaximumSnapshotCharacters = 67108864;
            TypeAttribute = "Requirement Type"; StatusAttribute = "Status"; VerificationAttribute = "Verification Method"; CriticalityAttribute = "Safety Level";
            CustomAttributes = new List<string> { "Requirement Type", "Verification Method", "Status", "Safety Level", "Priority" };
            TestTypes = new List<string> { "TEST", "Test Case" }; TestTypeAttribute = "Requirement Type"; TestTypeValues = new List<string> { "TEST", "Test Case" };
            RequiredTestForTypes = new List<string> { "SYS", "HLR", "LLR" };
            PropagationRules = new List<ImpactPropagationRule> { new ImpactPropagationRule() };
            CycleBehavior = TraceabilityCycleBehavior.Report;
            AttributeKinds = new Dictionary<string, ImpactValueKind>(); AttributeSeverities = new Dictionary<string, ImpactSeverity>();
            AttributeSeverities[CriticalityAttribute] = ImpactSeverity.Critical; AttributeSeverities[VerificationAttribute] = ImpactSeverity.High;
            AttributeSeverities[StatusAttribute] = ImpactSeverity.Medium; AttributeSeverities["Object Heading"] = ImpactSeverity.Low;
            AttributeSeverities["Object Text"] = ImpactSeverity.High;
        }
        [DataMember] public string Id { get; set; }
        [DataMember] public int MaximumDepth { get; set; }
        [DataMember] public int MaximumNodes { get; set; }
        [DataMember] public int MaximumPaths { get; set; }
        [DataMember] public int MaximumPathsPerRequirement { get; set; }
        [DataMember] public int TimeoutSeconds { get; set; }
        [DataMember] public int MaximumRequirements { get; set; }
        [DataMember] public int MaximumLinks { get; set; }
        [DataMember] public long MaximumSnapshotCharacters { get; set; }
        [DataMember] public string TypeAttribute { get; set; }
        [DataMember] public string StatusAttribute { get; set; }
        [DataMember] public string VerificationAttribute { get; set; }
        [DataMember] public string CriticalityAttribute { get; set; }
        [DataMember] public string TestTypeAttribute { get; set; }
        [DataMember] public List<string> TestTypeValues { get; set; }
        [DataMember] public List<string> TestTypes { get; set; }
        [DataMember] public List<string> RequiredTestForTypes { get; set; }
        [DataMember] public List<string> CustomAttributes { get; set; }
        [DataMember] public List<ImpactPropagationRule> PropagationRules { get; set; }
        [DataMember] public TraceabilityCycleBehavior CycleBehavior { get; set; }
        [DataMember] public Dictionary<string, ImpactValueKind> AttributeKinds { get; set; }
        [DataMember] public Dictionary<string, ImpactSeverity> AttributeSeverities { get; set; }
        public string Fingerprint => ImpactSerialization.Hash(this);
        public void Validate()
        {
            GraphGuard.Text(Id, 256);
            if (!Enum.IsDefined(typeof(TraceabilityCycleBehavior), CycleBehavior)) throw new ArgumentException("Invalid cycle policy.");
            if (MaximumDepth < 0 || MaximumDepth > 100 || MaximumNodes < 1 || MaximumNodes > 1000000 || MaximumPaths < 1 || MaximumPaths > 1000000 || MaximumPathsPerRequirement < 1 || MaximumPathsPerRequirement > 20 || TimeoutSeconds < 1 || TimeoutSeconds > 1800 || MaximumRequirements < 1 || MaximumRequirements > 1000000 || MaximumLinks < 1 || MaximumLinks > 1000000 || MaximumSnapshotCharacters < 1024 || MaximumSnapshotCharacters > 1073741824) throw new ArgumentException("Invalid impact analysis resource limits.");
            if (CustomAttributes == null || CustomAttributes.Count > 64 || CustomAttributes.Distinct(StringComparer.Ordinal).Count() != CustomAttributes.Count || PropagationRules == null || PropagationRules.Count > 128 || TestTypes == null || TestTypeValues == null || RequiredTestForTypes == null || AttributeKinds == null || AttributeSeverities == null) throw new ArgumentException("Invalid impact policy collections.");
            foreach (var name in CustomAttributes) GraphGuard.Text(name, 256);
            foreach (var name in new[] { TypeAttribute, StatusAttribute, VerificationAttribute, CriticalityAttribute, TestTypeAttribute })
                if (string.IsNullOrWhiteSpace(name) || !CustomAttributes.Contains(name)) throw new ArgumentException("Mapped attributes must be included in CustomAttributes.");
            foreach (var rule in PropagationRules) { GraphGuard.Text(rule.SourceType, 128); GraphGuard.Text(rule.TargetType, 128); GraphGuard.Text(rule.LinkModuleIdentity, 1024); GraphGuard.Text(rule.LinkType, 128); if (!Enum.IsDefined(typeof(TraceabilityDirection), rule.Direction)) throw new ArgumentException("Invalid impact direction."); }
            if (AttributeKinds.Values.Any(v => !Enum.IsDefined(typeof(ImpactValueKind), v)) || AttributeSeverities.Values.Any(v => !Enum.IsDefined(typeof(ImpactSeverity), v))) throw new ArgumentException("Invalid impact rule value.");
        }
    }
    public sealed class JsonImpactPolicyProvider : IImpactPolicyProvider
    {
        public ImpactAnalysisPolicy ReadJson(string json)
        {
            if (json == null || json.Length > 131072) throw new ArgumentException("Impact JSON policy must be bounded.");
            var policy = ImpactSerialization.Read<ImpactAnalysisPolicy>(Encoding.UTF8.GetBytes(json)); policy.Validate(); return policy;
        }
    }
    public static class ImpactSerialization
    {
        private static DataContractJsonSerializer Serializer(Type type) => new DataContractJsonSerializer(type, new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true, MaxItemsInObjectGraph = 4000000 });
        public static byte[] Bytes<T>(T value) { using (var s = new MemoryStream()) { Serializer(typeof(T)).WriteObject(s, value); return s.ToArray(); } }
        public static T Read<T>(byte[] bytes) { using (var s = new MemoryStream(bytes, false)) return (T)Serializer(typeof(T)).ReadObject(s); }
        public static T Clone<T>(T value)
        {
            if (!(value is ImpactAnalysisSnapshot s)) return Read<T>(Bytes(value));
            return (T)(object)new ImpactAnalysisSnapshot { Label = s.Label, CapturedAtUtc = s.CapturedAtUtc, IsStale = s.IsStale, TransportUncertain = s.TransportUncertain, Links = s.Links.Copy(), ComparedModuleIdentities = s.ComparedModuleIdentities.ToList(), Limitations = s.Limitations.ToList(),
                Requirements = s.Requirements.Select(r => new ImpactRequirementVersion { Version = r.Version, AbsoluteNumber = r.AbsoluteNumber, DisplayIdentifier = r.DisplayIdentifier, ParentAbsoluteNumber = r.ParentAbsoluteNumber, ParentKnown = r.ParentKnown, Position = r.Position, IsDeleted = r.IsDeleted, Access = r.Access,
                    Attributes = r.Attributes.ToDictionary(p => p.Key, p => new ImpactAttributeValue { Name = p.Value.Name, TypeName = p.Value.TypeName, PlainStatus = p.Value.PlainStatus, PlainText = p.Value.PlainText, RichStatus = p.Value.RichStatus, RichText = p.Value.RichText }, StringComparer.Ordinal) }).ToList() };
        }
        public static string Hash<T>(T value) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Bytes(value))).Replace("-", ""); }
        internal static string SnapshotFingerprint(ImpactAnalysisSnapshot s)
        {
            using (var sha = SHA256.Create())
            {
                using (var sink = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
                using (var w = new BinaryWriter(sink, Encoding.UTF8, true))
                {
                    var c = s.Links.Context;
                    w.Write(c.EnvironmentId ?? ""); w.Write(c.RepositoryId ?? ""); w.Write(c.Source.ToString()); w.Write(c.Revision ?? "");
                    w.Write(c.Definition?.Id ?? ""); w.Write(c.Set?.Id ?? ""); w.Write(c.Set?.IsOpen ?? false); w.Write(c.MetadataComplete); w.Write(s.IsStale);
                    w.Write(c.Set?.Version?.Major ?? -1); w.Write(c.Set?.Version?.Minor ?? -1); Text(w, c.Set?.Version?.Suffix);
                    w.Write(s.Links.Truncated); w.Write(s.Links.ContextChanged); w.Write(s.TransportUncertain);
                    foreach (var module in s.ComparedModuleIdentities.OrderBy(x => x, StringComparer.Ordinal)) w.Write(module);
                    foreach (var m in c.Modules.OrderBy(m => m.ModuleIdentity, StringComparer.Ordinal)) { w.Write(m.ModuleIdentity); w.Write(m.Version?.ModuleBaselineIdentity ?? ""); w.Write(m.FullPath ?? ""); w.Write(m.State.ToString()); w.Write(m.InventoryComplete); w.Write(m.LinksComplete); w.Write(m.SelectedForVerification); w.Write(m.InDefinition); w.Write(m.InSet); w.Write(m.KnownObjectCount ?? -1); w.Write(m.ShareableConfiguration.ToString()); }
                    foreach (var p in c.LinkModuleIdentities.OrderBy(p => p.Key, StringComparer.Ordinal)) { Text(w, p.Key); Text(w, p.Value); }
                    foreach (var m in s.Links.CapturedLinkModuleIdentities.OrderBy(x => x, StringComparer.Ordinal)) w.Write(m);
                    foreach (var r in s.Requirements.OrderBy(r => r.RequirementVersionIdentity, StringComparer.Ordinal))
                    {
                        w.Write(r.RequirementVersionIdentity); Text(w, r.DisplayIdentifier); w.Write(r.ParentAbsoluteNumber ?? 0); w.Write(r.ParentKnown); w.Write(r.Position ?? -1); w.Write(r.IsDeleted); w.Write(r.Access.ToString());
                        foreach (var v in r.Attributes.OrderBy(p => p.Key, StringComparer.Ordinal)) { w.Write(v.Key); Text(w, v.Value.Name); Text(w, v.Value.TypeName); Text(w, v.Value.PlainText); Text(w, v.Value.RichText); w.Write((int)v.Value.PlainStatus); w.Write((int)v.Value.RichStatus); }
                    }
                    foreach (var l in s.Links.Links.OrderBy(l => l.Identity?.VersionedKey ?? l.ObservationId, StringComparer.Ordinal))
                    { Text(w, l.SourceVersion?.ModuleBaselineIdentity); w.Write(l.SourceObject ?? 0); Text(w, l.TargetVersion?.ModuleBaselineIdentity); w.Write(l.TargetObject ?? 0); Text(w, l.SourcePath); Text(w, l.TargetPath); Text(w, l.ObservedSourceModuleIdentity); Text(w, l.ObservedTargetModuleIdentity); Text(w, l.LinkModuleIdentity); Text(w, l.LinkModulePath); Text(w, l.LinkType); w.Write((int)l.SourceAccess); w.Write((int)l.TargetAccess); w.Write((int)l.Direction); w.Write((int)l.Kind); w.Write(l.IsEchoed); Text(w, l.Evidence); Text(w, l.PhysicalLinkIdentity); w.Write(l.PhysicalMultiplicityVerified); }
                    foreach (var limitation in s.Limitations.Concat(s.Links.Limitations).OrderBy(x => x, StringComparer.Ordinal)) w.Write(limitation ?? "");
                    w.Flush(); sink.FlushFinalBlock();
                }
                return BitConverter.ToString(sha.Hash).Replace("-", "");
            }
        }
        private static void Text(BinaryWriter writer, string value) { writer.Write(value != null); if (value != null) writer.Write(value); }
    }
    public sealed class ImpactClassificationService
    {
        public void Classify(RequirementChange change, ImpactAnalysisPolicy policy)
        {
            var severities = change.Attributes.Where(a => a.SemanticChanged || a.TypeChanged).Select(a => policy.AttributeSeverities.TryGetValue(a.Name, out var severity) ? severity : ImpactSeverity.Medium).ToList();
            if ((change.Flags & (RequirementChangeFlags.Added | RequirementChangeFlags.Deleted)) != 0) severities.Add(ImpactSeverity.High);
            if ((change.Flags & RequirementChangeFlags.Moved) != 0) severities.Add(ImpactSeverity.Medium);
            if ((change.Flags & (RequirementChangeFlags.LinkAdded | RequirementChangeFlags.LinkRemoved | RequirementChangeFlags.LinkTargetChanged)) != 0) severities.Add(ImpactSeverity.High);
            if ((change.Flags & RequirementChangeFlags.FormattingOnly) != 0) severities.Add(ImpactSeverity.Low);
            change.Severity = severities.Count > 0 ? severities.Min() : !change.IsConclusive ? ImpactSeverity.Unknown : ImpactSeverity.Informational;
            change.Criticality = (change.After ?? change.Before)?.Value(policy.CriticalityAttribute);
            change.ReviewPriority = change.Severity == ImpactSeverity.Critical ? 1 : change.Severity == ImpactSeverity.High ? 2 : !change.IsConclusive ? 2 : change.Severity == ImpactSeverity.Medium ? 3 : 4;
        }
    }
}
