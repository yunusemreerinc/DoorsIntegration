using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DoorsIntegration.Core
{
    internal static class BaselinePreflightFingerprint
    {
        private static string Hash(Action<BinaryWriter> write)
        {
            using (var sha = SHA256.Create()) using (var crypto = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
            {
                using (var writer = new BinaryWriter(crypto, new UTF8Encoding(false, true), true)) { write(writer); writer.Flush(); }
                crypto.FlushFinalBlock(); return BitConverter.ToString(sha.Hash).Replace("-", "");
            }
        }
        private static void Text(BinaryWriter writer, string value) { writer.Write(value != null); if (value != null) writer.Write(value); }
        private static void Module(BinaryWriter w, BaselineModuleReference m) { Text(w, m.RepositoryId); Text(w, m.ModuleId); Text(w, m.CanonicalFullPath); }
        private static void Version(BinaryWriter w, BaselineVersion v) { w.Write(v != null); if (v != null) { w.Write(v.Major); w.Write(v.Minor); Text(w, v.Suffix); } }
        internal static string Request(BulkBaselinePreflightRequest r) => Hash(w =>
        {
            w.Write(1); w.Write((int)r.Baseline.Type); w.Write((int)r.Baseline.Mode); Text(w, r.Baseline.Suffix); Text(w, r.Baseline.Annotation);
            w.Write((int)r.ExecutionPolicy); w.Write(r.MaxPlanAge.Ticks); w.Write(r.MaxObservationAge.Ticks); w.Write(r.Timeout.Ticks);
            w.Write((int)r.Target); w.Write((int)r.ConnectionMode); w.Write(r.Baseline.Modules.Count);
            foreach (var item in r.Baseline.Modules) { Module(w, item.Module); Text(w, item.SuffixOverride); Text(w, item.AnnotationOverride); }
        });
        internal static string Context(BaselinePreflightFacts f) => Hash(w =>
        { Text(w, f.DatabaseIdentity); Text(w, f.CurrentUserId); Text(w, f.SessionId); Text(w, f.ConnectionEpoch); });
        internal static string State(BaselinePreflightFacts f) => Hash(w =>
        {
            Module(w, f.Module); w.Write((int)f.Exists); w.Write((int)f.Type); w.Write((int)f.ReadAccess); w.Write((int)f.CreateAccess); w.Write((int)f.ModifyAccess);
            w.Write((int)f.EditState); w.Write((int)f.LockState); w.Write((int)f.ExclusiveAvailability); w.Write((int)f.UnsavedChanges); w.Write((int)f.SessionValid);
            Text(w, Context(f)); Text(w, f.ContentRevision); Text(w, f.PermissionRevision); Text(w, f.LockRevision); w.Write((int)f.RevisionsReliable);
            Text(w, f.LockOwnerSessionId); Text(w, f.LockOwnerUserId); Text(w, f.DirtyOwnerSessionId);
            w.Write((int)f.SuffixValid); w.Write((int)f.AnnotationValid); w.Write((int)f.Source); Text(w, f.Evidence);
        });
        internal static string Metadata(BaselineMetadataResult m) => Hash(w =>
        {
            var s = m.Snapshot; Module(w, s.Module); w.Write((int)s.Status); w.Write(s.HistoryComplete); w.Write((int)s.Source); Text(w, s.ApiEvidence);
            w.Write((int)s.LatestExisting.Status); Version(w, s.LatestExisting.Version); w.Write((int)s.LatestExisting.Existence);
            w.Write((int)s.LatestNumberingVersion.Status); Version(w, s.LatestNumberingVersion.Version); w.Write((int)s.LatestNumberingVersion.Existence);
            w.Write(s.History.Count);
            foreach (var e in s.History.OrderBy(e => e.BaselineKey, StringComparer.Ordinal))
            {
                Text(w, e.BaselineKey); Text(w, e.Identity.NativeIdentity); Text(w, e.Annotation); Text(w, e.Creator);
                w.Write(e.CreationDateUtc.HasValue); if (e.CreationDateUtc.HasValue) w.Write(e.CreationDateUtc.Value.Ticks);
                Text(w, e.CreationDateRaw); w.Write((int)e.Existence);
            }
            w.Write((int)m.Proposal.Status); w.Write((int)m.Proposal.Type); Version(w, m.Proposal.ExpectedVersion); Text(w, m.Proposal.Suffix);
            w.Write((int)m.Proposal.Source); Text(w, m.Proposal.Evidence); Version(w, m.Collision.Target); w.Write((int)m.Collision.Status);
        });
        internal static string Precondition(string request, BaselinePreflightProbe p, BaselinePreflightFacts f, string metadata) => Hash(w =>
        { w.Write(1); Text(w, request); Module(w, p.Module); w.Write((int)p.Type); Text(w, p.Suffix); Text(w, p.Annotation); Version(w, p.ExpectedVersion); Text(w, State(f)); w.Write((int)f.TargetAvailability); Text(w, metadata); });
        internal static string Plan(BulkBaselineDryRunPlan plan) => Hash(w =>
        {
            w.Write(1); Text(w, plan.PlanId.ToString("N")); w.Write(plan.CreatedAtUtc.Ticks); Text(w, plan.RequestFingerprint);
            foreach (var item in plan.Items) { Module(w, item.Module); w.Write((int)item.Status); w.Write((int)item.ReasonCode); Version(w, item.CurrentVersion); Version(w, item.ExpectedVersion); Text(w, item.Precondition?.Fingerprint); }
        });
    }
}
