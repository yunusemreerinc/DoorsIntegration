using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DoorsIntegration.Core
{
    internal static class BaselineVerificationCopy
    {
        // Versions and strings are immutable. Avoid serializing the same version for every dense edge.
        internal static BaselineLinkSnapshot Snapshot(BaselineLinkSnapshot s) => new BaselineLinkSnapshot
        {
            SnapshotId = s.SnapshotId, CapturedAtUtc = s.CapturedAtUtc, Context = BaselineSetSerialization.Clone(s.Context), Truncated = s.Truncated, ContextChanged = s.ContextChanged,
            CapturedLinkModuleIdentities = s.CapturedLinkModuleIdentities.ToList(), Limitations = s.Limitations.ToList(),
            Requirements = s.Requirements.Select(n => new BaselineRequirementInfo { Version = n.Version, AbsoluteNumber = n.AbsoluteNumber, DisplayIdentifier = n.DisplayIdentifier, RequirementType = n.RequirementType,
                RequirementStatus = n.RequirementStatus, Heading = n.Heading, Text = n.Text, RichHeading = n.RichHeading, RichText = n.RichText, IsDeleted = n.IsDeleted, Access = n.Access }).ToList(),
            Links = s.Links.Select(l => new BaselineLinkInfo { ObservationId = l.ObservationId, SourceVersion = l.SourceVersion, TargetVersion = l.TargetVersion, SourceObject = l.SourceObject, TargetObject = l.TargetObject,
                SourcePath = l.SourcePath, TargetPath = l.TargetPath, ObservedSourceModuleIdentity = l.ObservedSourceModuleIdentity, ObservedTargetModuleIdentity = l.ObservedTargetModuleIdentity, LinkModuleIdentity = l.LinkModuleIdentity, LinkModulePath = l.LinkModulePath, LinkType = l.LinkType, Direction = l.Direction, Kind = l.Kind, IsEchoed = l.IsEchoed,
                SourceAccess = l.SourceAccess, TargetAccess = l.TargetAccess, Evidence = l.Evidence, PhysicalLinkIdentity = l.PhysicalLinkIdentity, PhysicalMultiplicityVerified = l.PhysicalMultiplicityVerified }).ToList()
        };
        internal static string Fingerprint(BaselineLinkSnapshot s)
        {
            using (var sha = SHA256.Create())
            {
                using (var crypto = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
                using (var writer = new BinaryWriter(crypto, new UTF8Encoding(false, true), true))
                {
                    writer.Write(s.SnapshotId.ToByteArray()); writer.Write(s.CapturedAtUtc.ToUniversalTime().Ticks); writer.Write(BaselineSetSerialization.Hash(s.Context)); writer.Write(s.Truncated); writer.Write(s.ContextChanged);
                    writer.Write(s.CapturedLinkModuleIdentities.Count); foreach (var id in s.CapturedLinkModuleIdentities) Text(writer, id);
                    writer.Write(s.Requirements.Count);
                    foreach (var n in s.Requirements)
                    { Text(writer, n.Version?.ModuleBaselineIdentity); writer.Write(n.AbsoluteNumber); Text(writer, n.DisplayIdentifier); Text(writer, n.RequirementType); Text(writer, n.RequirementStatus); Text(writer, n.Heading); Text(writer, n.Text); Text(writer, n.RichHeading); Text(writer, n.RichText); writer.Write(n.IsDeleted); writer.Write((int)n.Access); }
                    writer.Write(s.Links.Count);
                    foreach (var l in s.Links)
                    { Text(writer, l.ObservationId); Text(writer, l.SourceVersion?.ModuleBaselineIdentity); Text(writer, l.TargetVersion?.ModuleBaselineIdentity); writer.Write(l.SourceObject ?? -1); writer.Write(l.TargetObject ?? -1); Text(writer, l.SourcePath); Text(writer, l.TargetPath); Text(writer, l.ObservedSourceModuleIdentity); Text(writer, l.ObservedTargetModuleIdentity); Text(writer, l.LinkModuleIdentity); Text(writer, l.LinkModulePath); Text(writer, l.LinkType); writer.Write((int)l.Direction); writer.Write((int)l.Kind); writer.Write(l.IsEchoed); writer.Write((int)l.SourceAccess); writer.Write((int)l.TargetAccess); Text(writer, l.Evidence); Text(writer, l.PhysicalLinkIdentity); writer.Write(l.PhysicalMultiplicityVerified); }
                    writer.Write(s.Limitations.Count); foreach (var limitation in s.Limitations) Text(writer, limitation); writer.Flush(); crypto.FlushFinalBlock();
                }
                return BitConverter.ToString(sha.Hash).Replace("-", "");
            }
        }
        private static void Text(BinaryWriter writer, string value) { writer.Write(value != null); if (value != null) writer.Write(value); }
    }
}
