using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DoorsIntegration.Core
{
    public enum TraceabilityDirection { Incoming, Outgoing, Both }
    public enum TraceabilityVersionPolicy { SameVersion, CurrentCounterpart, AnyExplicitVersion }
    public enum TraceabilityCycleBehavior { Allow, Report, Prohibit }
    public enum TraceabilityState { Satisfied, Observed, Violation, Unknown, Excluded }
    public enum TraceabilitySeverity { Information, Warning, Error }
    public enum TraceabilityEdgeState { Confirmed, Possible, Reference }
    public enum TraceabilityGraphFormat { Json, Csv, GraphMl }

    public sealed class TraceabilityModuleScope
    {
        public TraceabilityModuleScope(string modulePath, string role = "Requirement", RequirementBaseline baseline = null, string observedBaselineVersion = null)
        {
            ModulePath = GraphGuard.Path(modulePath); Role = GraphGuard.Text(role, 128); Baseline = baseline;
            if (observedBaselineVersion != null && baseline == null) throw new ArgumentException("A raw baseline label requires a selected baseline.");
            ObservedBaselineVersion = observedBaselineVersion == null ? null : GraphGuard.Text(observedBaselineVersion, 512);
            Version = baseline == null ? "" : baseline.Major.ToString(CultureInfo.InvariantCulture) + "." + baseline.Minor.ToString(CultureInfo.InvariantCulture) + "[" + baseline.Suffix + "]";
            Key = GraphGuard.Hash(ModulePath, baseline == null ? "Current" : "SelectedBaseline", Version);
        }
        public string ModulePath { get; }
        public string Role { get; }
        public RequirementBaseline Baseline { get; }
        // Optional caller binding of the native opaque versionString. It is exported as caller supplied evidence.
        public string ObservedBaselineVersion { get; }
        public string Version { get; }
        public string Key { get; }
    }

    public sealed class TraceabilityLinkRule
    {
        public TraceabilityLinkRule(string id, string anchorRole, IEnumerable<string> counterpartRoles, string linkModulePath,
            TraceabilityDirection direction = TraceabilityDirection.Outgoing, int minimumLinks = 1, int? maximumLinks = null,
            TraceabilityVersionPolicy versionPolicy = TraceabilityVersionPolicy.SameVersion, string requiredNativeLinkModuleId = null,
            bool rejectUnexpectedCounterparts = true)
        {
            Id = GraphGuard.Text(id, 128); AnchorRole = GraphGuard.Text(anchorRole, 128); LinkModulePath = GraphGuard.Path(linkModulePath);
            CounterpartRoles = GraphGuard.Strings(counterpartRoles, 64, 128);
            if (CounterpartRoles.Count == 0 || direction == TraceabilityDirection.Both || !Enum.IsDefined(typeof(TraceabilityDirection), direction) ||
                !Enum.IsDefined(typeof(TraceabilityVersionPolicy), versionPolicy) || minimumLinks < 0 || minimumLinks > 100000 || maximumLinks < minimumLinks || maximumLinks > 100000)
                throw new ArgumentException("Invalid expected link rule.");
            Direction = direction; MinimumLinks = minimumLinks; MaximumLinks = maximumLinks; VersionPolicy = versionPolicy;
            RequiredNativeLinkModuleId = requiredNativeLinkModuleId == null ? null : GraphGuard.Text(requiredNativeLinkModuleId, 1024);
            RejectUnexpectedCounterparts = rejectUnexpectedCounterparts;
        }
        public string Id { get; }
        public string AnchorRole { get; }
        public IReadOnlyList<string> CounterpartRoles { get; }
        public string LinkModulePath { get; }
        public TraceabilityDirection Direction { get; }
        public int MinimumLinks { get; }
        public int? MaximumLinks { get; }
        public TraceabilityVersionPolicy VersionPolicy { get; }
        public string RequiredNativeLinkModuleId { get; }
        public bool RejectUnexpectedCounterparts { get; }
    }

    public sealed class TraceabilityCycleRule
    {
        public TraceabilityCycleRule(string id = "observed-cycles", TraceabilityCycleBehavior behavior = TraceabilityCycleBehavior.Report,
            IEnumerable<string> roles = null, IEnumerable<string> linkModulePaths = null)
        {
            Id = GraphGuard.Text(id, 128);
            if (!Enum.IsDefined(typeof(TraceabilityCycleBehavior), behavior)) throw new ArgumentException("Invalid cycle behavior.");
            Behavior = behavior; Roles = GraphGuard.Strings(roles, 64, 128);
            LinkModulePaths = Array.AsReadOnly(GraphGuard.Strings(linkModulePaths, 32, 4096).Select(GraphGuard.Path).ToArray());
            if (LinkModulePaths.Distinct(StringComparer.Ordinal).Count() != LinkModulePaths.Count) throw new ArgumentException("Duplicate normalized cycle link-module path.");
        }
        public string Id { get; }
        public TraceabilityCycleBehavior Behavior { get; }
        public IReadOnlyList<string> Roles { get; }
        public IReadOnlyList<string> LinkModulePaths { get; }
    }

    public sealed class TraceabilityPolicy
    {
        public TraceabilityPolicy(string id, IEnumerable<TraceabilityLinkRule> linkRules = null, IEnumerable<TraceabilityCycleRule> cycleRules = null,
            IEnumerable<string> orphanRoles = null, TraceabilitySeverity orphanSeverity = TraceabilitySeverity.Warning)
        {
            Id = GraphGuard.Text(id, 128);
            var links = (linkRules ?? Enumerable.Empty<TraceabilityLinkRule>()).Take(129).ToArray();
            var cycles = (cycleRules ?? new[] { new TraceabilityCycleRule() }).Take(33).ToArray();
            if (links.Length > 128 || cycles.Length > 32 || links.Any(r => r == null) || cycles.Any(r => r == null) ||
                links.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != links.Length || cycles.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != cycles.Length ||
                !Enum.IsDefined(typeof(TraceabilitySeverity), orphanSeverity)) throw new ArgumentException("Invalid traceability policy.");
            LinkRules = Array.AsReadOnly(links); CycleRules = Array.AsReadOnly(cycles);
            OrphanRoles = GraphGuard.Strings(orphanRoles ?? new[] { "Requirement" }, 64, 128); OrphanSeverity = orphanSeverity;
            Fingerprint = GraphGuard.Hash(new[] { Id, orphanSeverity.ToString(), string.Join("\n", OrphanRoles) }.Concat(links.SelectMany(r => new[] {
                r.Id, r.AnchorRole, string.Join("\n", r.CounterpartRoles), r.LinkModulePath, r.Direction.ToString(), r.MinimumLinks.ToString(CultureInfo.InvariantCulture),
                r.MaximumLinks?.ToString(CultureInfo.InvariantCulture) ?? "", r.VersionPolicy.ToString(), r.RequiredNativeLinkModuleId ?? "", r.RejectUnexpectedCounterparts.ToString() }))
                .Concat(cycles.SelectMany(r => new[] { r.Id, r.Behavior.ToString(), string.Join("\n", r.Roles), string.Join("\n", r.LinkModulePaths) })).ToArray());
        }
        public string Id { get; }
        public string Fingerprint { get; }
        public IReadOnlyList<TraceabilityLinkRule> LinkRules { get; }
        public IReadOnlyList<TraceabilityCycleRule> CycleRules { get; }
        public IReadOnlyList<string> OrphanRoles { get; }
        public TraceabilitySeverity OrphanSeverity { get; }
    }

    public sealed class TraceabilityGraphRequest
    {
        public TraceabilityGraphRequest(string repositoryId, IEnumerable<TraceabilityModuleScope> modules, IEnumerable<string> linkModulePaths,
            TraceabilityPolicy policy, int maxNodes = 100000, int maxObservations = 250000, int maxFindings = 500000,
            TimeSpan? readTimeout = null, DoorsVersion target = DoorsVersion.V97, DoorsConnectionMode connectionMode = DoorsConnectionMode.AttachExisting, long maxMetadataCharacters = 67108864)
        {
            RepositoryId = GraphGuard.Text(repositoryId, 1024); Policy = policy ?? throw new ArgumentNullException(nameof(policy));
            var scopes = (modules ?? throw new ArgumentNullException(nameof(modules))).Take(65).ToArray();
            LinkModulePaths = Array.AsReadOnly(GraphGuard.Strings(linkModulePaths, 32, 4096).Select(GraphGuard.Path).ToArray());
            ReadTimeout = readTimeout ?? TimeSpan.FromMinutes(2);
            if (scopes.Length == 0 || scopes.Length > 64 || scopes.Any(s => s == null) || scopes.Select(s => s.Key).Distinct().Count() != scopes.Length ||
                scopes.GroupBy(s => s.ModulePath, StringComparer.Ordinal).Any(g => g.Select(s => s.Role).Distinct().Count() != 1) || LinkModulePaths.Count == 0 || LinkModulePaths.Distinct(StringComparer.Ordinal).Count() != LinkModulePaths.Count ||
                policy.LinkRules.Any(r => !LinkModulePaths.Contains(r.LinkModulePath, StringComparer.Ordinal)) ||
                policy.CycleRules.Any(r => r.LinkModulePaths.Any(p => !LinkModulePaths.Contains(p, StringComparer.Ordinal))) ||
                maxNodes < 1 || maxNodes > 1000000 || maxObservations < 1 || maxObservations > 1000000 || maxFindings < 1 || maxFindings > 2000000 ||
                maxMetadataCharacters < 1024 || maxMetadataCharacters > 1073741824 ||
                ReadTimeout < TimeSpan.FromMilliseconds(1) || ReadTimeout > TimeSpan.FromMinutes(30) ||
                !Enum.IsDefined(typeof(DoorsConnectionMode), connectionMode)) throw new ArgumentException("Invalid graph scope, policy or limits.");
            DoorsVersions.Format(target); Modules = Array.AsReadOnly(scopes); MaxNodes = maxNodes; MaxObservations = maxObservations; MaxFindings = maxFindings;
            Target = target; ConnectionMode = connectionMode;
            MaxMetadataCharacters = maxMetadataCharacters;
        }
        public string RepositoryId { get; }
        public IReadOnlyList<TraceabilityModuleScope> Modules { get; }
        public IReadOnlyList<string> LinkModulePaths { get; }
        public TraceabilityPolicy Policy { get; }
        public int MaxNodes { get; }
        public int MaxObservations { get; }
        public int MaxFindings { get; }
        public long MaxMetadataCharacters { get; }
        public TimeSpan ReadTimeout { get; }
        public DoorsVersion Target { get; }
        public DoorsConnectionMode ConnectionMode { get; }
        public bool ReadOnly => true;
    }

    internal static class GraphGuard
    {
        internal static string Text(string value, int max, bool empty = false)
        {
            if (value == null || value.Length > max || (!empty && string.IsNullOrWhiteSpace(value)) || value.Any(char.IsControl)) throw new ArgumentException("Invalid graph metadata.");
            RequirementBaseline.ValidateUnicode(value); return value;
        }
        internal static string Path(string path) { var value = DoorsItemPath.Normalize(path); if (value == "/" || value.IndexOfAny(new[] { '*', '?' }) >= 0) throw new ArgumentException("Exact module path required."); return value; }
        internal static IReadOnlyList<string> Strings(IEnumerable<string> source, int max, int characters)
        { var values = (source ?? Enumerable.Empty<string>()).Take(max + 1).ToArray(); if (values.Length > max || values.Distinct(StringComparer.Ordinal).Count() != values.Length) throw new ArgumentException("Invalid graph collection."); foreach (var v in values) Text(v, characters); return Array.AsReadOnly(values); }
        internal static string Hash(params string[] values)
        { using (var buffer = new MemoryStream()) using (var writer = new BinaryWriter(buffer, new UTF8Encoding(false, true), true)) using (var sha = SHA256.Create()) { foreach (var v in values) writer.Write(v ?? ""); writer.Flush(); return BitConverter.ToString(sha.ComputeHash(buffer.ToArray())).Replace("-", "").ToLowerInvariant(); } }
        internal static IntegrationException Failure(string message) => new IntegrationException(IntegrationErrorCode.TraceabilityGraphFailed, message);
        internal static void Limit(bool exceeded) { if (exceeded) throw new IntegrationException(IntegrationErrorCode.TraceabilityGraphLimitReached, "Traceability graph limit reached; no complete graph was returned."); }
    }
}
