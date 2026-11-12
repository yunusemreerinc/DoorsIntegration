using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DoorsIntegration.Core;
using DoorsIntegration.Processing;

namespace DoorsIntegration.ConfigurationManagement
{
    public static class DashboardResultMapper
    {
        public static DashboardRequirementFact Requirement(ImpactRequirementVersion r) => new DashboardRequirementFact { Version=r.Version,AbsoluteNumber=r.AbsoluteNumber,IsDeleted=r.IsDeleted,Readable=r.Access==BaselineVerificationReadState.Verified,Type=r.Value("Requirement Type"),Status=r.Value("Status")??r.Value("Requirement Status"),Criticality=r.Value("Criticality")??r.Value("Safety Level"),CustomAttributeNames=r.Attributes.Keys.Where(k=>k!="Object Heading"&&k!="Object Text").ToList() };
        public static DashboardAnalysisRecord Record(DashboardRequest r,DashboardAnalysisKind kind,string id,DateTime at,IEnumerable<DashboardRequirementFact> requirements)
        {
            var req=requirements.ToList(); return new DashboardAnalysisRecord { ClientVersion=r.ClientVersion,Kind=kind,AnalysisId=id,SnapshotIdentity=id,Environment=r.Environment,DatabaseIdentity=r.DatabaseIdentity,IsMock=r.IsMock,TimestampUtc=at,PolicyVersion=r.TraceabilityPolicyVersion,RequestedPolicyKey=DashboardAnalysisRecord.PolicyKey(r),ProfileVersion=r.QualityProfileVersion,RequestedProfileKey=DashboardAnalysisRecord.ProfileKey(r),Standard=r.QualityStandard,AnalysisVersion=r.AnalysisSnapshotVersion,Requirements=req,Modules=req.Select(q=>q.Version).GroupBy(v=>v.ModuleBaselineIdentity).Select(g=>g.First()).ToList(),Completeness=DashboardCompleteness.Partial };
        }
        public static DashboardAnalysisRecord Graph(DashboardRequest r,TraceabilityGraph graph,IEnumerable<DashboardRequirementFact> requirements)
        {
            var record=Record(r,DashboardAnalysisKind.Traceability,graph.GraphId.ToString("N"),graph.BuiltAtUtc,requirements); record.PolicyFingerprint=graph.Request.Policy.Fingerprint;
            record.Completeness=graph.IsCompleteWithinRequestedScope?DashboardCompleteness.Complete:DashboardCompleteness.Partial; record.Status=record.Completeness.ToString();
            var byObject=record.Requirements.ToDictionary(q=>q.ModuleIdentity+"/"+q.AbsoluteNumber.ToString(CultureInfo.InvariantCulture)); var nodes=new Dictionary<string,DashboardRequirementFact>();
            foreach(var node in graph.Nodes.Values) if(node.QualifiedModuleId!=null&&node.AbsoluteNumber.HasValue&&byObject.TryGetValue(node.QualifiedModuleId+"/"+node.AbsoluteNumber.Value.ToString(CultureInfo.InvariantCulture),out var req)) nodes[node.Id]=req;
            var findings=graph.Findings.Where(f=>f.NodeId!=null).GroupBy(f=>f.NodeId).ToDictionary(g=>g.Key,g=>g.ToArray());
            foreach(var node in graph.Nodes.Values.Where(n=>n.IsInventoryObject&&!n.IsDeleted))
            {
                if(!nodes.TryGetValue(node.Id,out var req)) { record.Completeness=DashboardCompleteness.Partial; continue; }
                findings.TryGetValue(node.Id,out var observations); observations=observations??Array.Empty<TraceabilityFinding>();
                var expected=observations.Where(f=>f.Code=="ExpectedLinkSatisfied"||f.Code=="MissingExpectedLink"||f.Code=="ExpectedLinkUnknown").ToArray();
                bool known=!string.IsNullOrEmpty(req.Type); bool applicable=expected.Length>0;
                record.Coverage.Add(new DashboardCoverageFact { RequirementVersionIdentity=req.VersionIdentity,EligibilityKnown=known,Applicable=applicable,State=!known?TraceabilityState.Unknown:!applicable?TraceabilityState.Excluded:expected.Any(f=>f.State==TraceabilityState.Unknown)?TraceabilityState.Unknown:expected.Any(f=>f.State==TraceabilityState.Violation)?TraceabilityState.Violation:TraceabilityState.Satisfied,AccessDenied=node.HasUncertainAccess,BrokenChain=observations.Any(f=>f.Code.IndexOf("Broken",StringComparison.OrdinalIgnoreCase)>=0) });
            }
            foreach(var edge in graph.Edges)
            {
                nodes.TryGetValue(edge.SourceId,out var source); nodes.TryGetValue(edge.TargetId,out var target);
                record.Links.Add(new DashboardLinkFact { Identity=edge.Id,SourceVersionIdentity=source?.VersionIdentity,TargetVersionIdentity=target?.VersionIdentity,LinkModuleIdentity=edge.QualifiedLinkModuleId,Resolution=edge.State==TraceabilityEdgeState.Confirmed?"Confirmed":"Unresolved",VerifiedBaseline=false });
            }
            foreach(var finding in graph.Findings.Where(f=>f.State==TraceabilityState.Violation||f.State==TraceabilityState.Unknown))
            { nodes.TryGetValue(finding.NodeId??"",out var req); record.Findings.Add(Finding(record,finding.Id,"Traceability",finding.Code,Severity(finding.Severity),req,finding.Message,finding.State==TraceabilityState.Unknown,DashboardDestination.TraceabilityExplorer,finding.RuleId)); }
            return record;
        }
        public static DashboardAnalysisRecord Verification(DashboardRequest r,BaselineSetVerificationResult result)
        {
            var requirements=result.Snapshot.Requirements.Select(q=>new DashboardRequirementFact { Version=q.Version,AbsoluteNumber=q.AbsoluteNumber,Type=q.RequirementType,Status=q.RequirementStatus,IsDeleted=q.IsDeleted,Readable=q.Access==BaselineVerificationReadState.Verified }).ToArray();
            var record=Record(r,DashboardAnalysisKind.BaselineVerification,result.VerificationJobId.ToString("N"),result.CompletedAtUtc,requirements);
            record.PolicyFingerprint=result.PolicyFingerprint; record.SnapshotIdentity=result.Snapshot.SnapshotId.ToString("N"); record.SetIdentity=result.Snapshot.Context.Set?.Id; record.DefinitionIdentity=result.Snapshot.Context.Definition?.Id; record.OpenSet=result.Snapshot.Context.Set?.IsOpen==true; record.Operator=result.Snapshot.Context.Operator;
            record.MissingSetMembers=result.Snapshot.Context.Modules.LongCount(m=>m.State==BaselineVerificationReadState.Missing); record.Completeness=result.Status==BaselineVerificationStatus.Passed||result.Status==BaselineVerificationStatus.PassedWithWarnings?DashboardCompleteness.Complete:DashboardCompleteness.Partial; record.Status=result.Status.ToString();
            record.BaselineLinkVerificationAvailable=true; record.BrokenChainCount=result.Coverage?.BrokenTraceabilityChains;
            record.MissingModuleIdentities=result.Snapshot.Context.Modules.Where(m=>m.State==BaselineVerificationReadState.Missing).Select(m=>m.ModuleIdentity).ToList();
            var graph=result.Graph; if(graph!=null) { var projected=Graph(r,graph,requirements); record.Coverage=projected.Coverage; }
            foreach(var link in result.Links)
            {
                var info=link.Link; record.Links.Add(new DashboardLinkFact { Identity=info.Identity?.VersionedKey??info.ObservationId,SourceVersionIdentity=info.SourceVersion!=null&&info.SourceObject.HasValue?info.SourceVersion.NodeIdentity(info.SourceObject.Value):null,TargetVersionIdentity=info.TargetVersion!=null&&info.TargetObject.HasValue?info.TargetVersion.NodeIdentity(info.TargetObject.Value):null,LinkModuleIdentity=info.LinkModuleIdentity,Resolution=link.Status==BaselineLinkStatus.AccessDenied?"AccessDenied":link.Status==BaselineLinkStatus.UnexpectedVersionTarget?"UnexpectedVersionTarget":link.Status==BaselineLinkStatus.UnresolvedSource||link.Status==BaselineLinkStatus.UnresolvedTarget||link.Status==BaselineLinkStatus.Unknown?"Unresolved":"Confirmed",IsEchoed=info.IsEchoed,VerifiedBaseline=link.Status==BaselineLinkStatus.ValidInternalBaselineLink });
            }
            foreach(var f in result.Findings) { var req=requirements.FirstOrDefault(q=>q.VersionIdentity==f.NodeIdentity); record.Findings.Add(Finding(record,DashboardSerialization.Hash(new[]{f.Code,f.NodeIdentity,f.LinkIdentity,f.RuleId}),"BaselineVerification",f.Code,Severity(f.Severity),req,f.Explanation,f.Uncertain,DashboardDestination.TraceabilityExplorer,f.RuleId)); }
            record.Warnings.AddRange(result.Snapshot.Limitations); return record;
        }
        public static DashboardAnalysisRecord Impact(DashboardRequest r,ChangeImpactResult result,bool sourceContext)
        {
            var snapshot=sourceContext?result.Source:result.Target; var record=Record(r,DashboardAnalysisKind.Impact,result.AnalysisId.ToString("N"),result.CompletedAtUtc,snapshot.Requirements.Select(Requirement));
            // Retain source-side deleted-object evidence without treating it as current inventory.
            record.Requirements=record.Requirements.Concat(result.Source.Requirements.Select(Requirement)).GroupBy(q=>q.VersionIdentity).Select(g=>g.First()).ToList();
            record.SnapshotIdentity=result.SnapshotIdentity; record.PolicyFingerprint=result.PolicyFingerprint; record.SourceVersions=result.Source.Links.Context.Modules.Where(m=>m.Version!=null).Select(m=>m.Version).ToList(); record.Completeness=result.Status==ImpactAnalysisStatus.Completed?DashboardCompleteness.Complete:DashboardCompleteness.Partial; record.ConclusiveComparison=result.Status==ImpactAnalysisStatus.Completed&&result.Changes.All(c=>c.IsConclusive); record.Status=result.Status.ToString(); record.Operator=snapshot.Links.Context.Operator; record.ImpactPathCount=result.Paths.Count;
            record.ComparedModuleIdentities=result.Source.ComparedModuleIdentities.Intersect(result.Target.ComparedModuleIdentities).ToList();
            foreach(var change in result.Changes) record.Changes.Add(new DashboardChangeFact { ModuleIdentity=(change.After??change.Before)?.Version.ModuleIdentity,RequirementIdentity=(change.After??change.Before)?.RequirementIdentity,RequirementVersionIdentity=(sourceContext?change.Before??change.After:change.After??change.Before)?.RequirementVersionIdentity,PrimaryType=change.PrimaryType,Flags=change.Flags,Severity=(DashboardSeverity)change.Severity });
            var byVersion=snapshot.Requirements.ToDictionary(q=>q.RequirementVersionIdentity);
            foreach(var f in result.Findings)
            {
                byVersion.TryGetValue(f.ImpactedVersionIdentity??"",out var req); var mapped=Finding(record,f.Identity,"Impact",f.Code,(DashboardSeverity)f.Severity,req==null?null:Requirement(req),f.Reason,f.EvidenceQuality!=ImpactEvidenceQuality.ObservedPath,DashboardDestination.ImpactAnalysis);
                mapped.RequirementIdentity=f.ImpactedRequirementIdentity; mapped.RequirementVersionIdentity=f.ImpactedVersionIdentity; mapped.RootRequirementIdentity=f.RootChangeIdentity; mapped.ImpactKind=f.Kind.ToString(); mapped.ReviewStatus=f.ReviewStatus.ToString();
                mapped.ReviewEvidenceFingerprint=f.EvidenceFingerprint;
                mapped.TestRecommendation=result.Impacted.FirstOrDefault(i=>i.RequirementIdentity==f.ImpactedRequirementIdentity)?.TestRecommendation.ToString(); record.Findings.Add(mapped);
            }
            record.Warnings.AddRange(result.Limitations); return record;
        }
        public static DashboardFinding Finding(DashboardAnalysisRecord record,string id,string category,string code,DashboardSeverity severity,DashboardRequirementFact req,string explanation,bool uncertain,DashboardDestination target,string rule=null)
            => new DashboardFinding { Id=id,Category=category,Code=code,Severity=severity,ModuleIdentity=req?.ModuleIdentity,RequirementIdentity=req?.Identity,RequirementVersionIdentity=req?.VersionIdentity,AbsoluteNumber=req?.AbsoluteNumber,AnalysisIdentity=record.AnalysisId,AnalysisSnapshotIdentity=record.SnapshotIdentity,TimestampUtc=record.TimestampUtc,Explanation=explanation,Uncertain=uncertain,Destination=target,Rule=rule };
        public static DashboardSeverity Severity(TraceabilitySeverity severity) => severity==TraceabilitySeverity.Error?DashboardSeverity.High:severity==TraceabilitySeverity.Warning?DashboardSeverity.Medium:DashboardSeverity.Informational;
    }
}
