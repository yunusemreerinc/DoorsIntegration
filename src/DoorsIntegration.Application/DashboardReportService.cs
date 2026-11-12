using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;

namespace DoorsIntegration.ConfigurationManagement
{
    public enum DashboardReportKind { ConfigurationSummary, BaselineHealth, TraceabilityHealth, RequirementQualitySummary, ChangeImpactSummary, AuditActivity }
    public sealed class DashboardReportService
    {
        private readonly IExportService export;
        public DashboardReportService(IExportService export) { this.export=export; }
        public Task<ExportResult> ExportAsync(DashboardSnapshot snapshot,DashboardReportKind kind,ExportRequest request,IProgress<ExportProgress> progress=null,CancellationToken token=default(CancellationToken))
        { if(snapshot?.RequestKey!=snapshot?.Request?.Key || snapshot==null) throw new ArgumentException("Dashboard report scope binding invalid."); return export.ExportAsync(request,new EnumerableExportSource(Rows(DashboardSerialization.Copy(snapshot),kind,token)),progress,token); }
        private static IEnumerable<ExportRecord> Rows(DashboardSnapshot s,DashboardReportKind kind,CancellationToken token)
        {
            string id=s.SnapshotId.ToString("N");
            yield return Row(ExportTableKind.DashboardMetadata,"snapshotId",id,"report",kind,"generatedOn",DateTime.UtcNow,"environment",s.Request.Environment,"databaseIdentity",s.Request.DatabaseIdentity,"isMock",s.Request.IsMock,"snapshotTimestamp",s.CollectedAtUtc,"scopeKey",s.RequestKey,"scopeJson",Encoding.UTF8.GetString(DashboardSerialization.Bytes(s.Request)),"traceabilityPolicy",s.Request.TraceabilityPolicyVersion,"qualityProfile",s.Request.QualityProfileVersion,"sourceAnalysisIds",string.Join(";",s.Sections.SelectMany(v=>v.AnalysisIds).Distinct()),"completeness",s.DataQuality.Completeness,"isStale",s.DataQuality.IsStale,"warnings",string.Join("\n",s.DataQuality.Warnings),"disclosure","Read-only aggregation of identified source snapshots. Not a direct audit-evidence certification, DO compliance decision or substitute for DOORS history. Mock is not native COM/DXL validation.");
            DashboardSections selected=kind==DashboardReportKind.ConfigurationSummary?DashboardSections.All:kind==DashboardReportKind.BaselineHealth?DashboardSections.Baseline:kind==DashboardReportKind.TraceabilityHealth?DashboardSections.Traceability:kind==DashboardReportKind.RequirementQualitySummary?DashboardSections.Quality:kind==DashboardReportKind.ChangeImpactSummary?DashboardSections.Impact:DashboardSections.Audit;
            if(kind==DashboardReportKind.ConfigurationSummary) foreach(var m in s.Summary.Metrics.Where(m=>!s.Sections.SelectMany(v=>v.Metrics).Any(v=>v.Id==m.Id))) yield return Row(ExportTableKind.DashboardMetrics,"snapshotId",id,"section","Summary","metricId",m.Id,"name",m.Name,"value",m.Value,"unit",m.Unit,"scope",m.Scope,"source",m.Source,"asOfUtc",m.TimestampUtc,"completeness",m.Completeness,"isStale",m.IsStale,"isLowerBound",m.IsLowerBound,"warning",m.Warning);
            foreach(var section in s.Sections.Where(x=>(x.Section&selected)!=0))
            {
                foreach(var m in section.Metrics) { token.ThrowIfCancellationRequested(); yield return Row(ExportTableKind.DashboardMetrics,"snapshotId",id,"section",section.Section,"metricId",m.Id,"name",m.Name,"value",m.Value,"unit",m.Unit,"scope",m.Scope,"source",m.Source,"asOfUtc",m.TimestampUtc,"completeness",m.Completeness,"isStale",m.IsStale,"isLowerBound",m.IsLowerBound,"warning",m.Warning); }
                foreach(var m in section.Baseline?.Modules??Enumerable.Empty<DashboardModuleHealth>()) { token.ThrowIfCancellationRequested(); yield return Row(ExportTableKind.DashboardModules,"snapshotId",id,"moduleIdentity",m.ModuleIdentity,"path",m.ModulePath,"project",m.Project,"baselineIdentity",m.Version?.ModuleBaselineIdentity,"version",m.Version?.Version,"access",m.Access,"requirements",m.Requirements,"deleted",m.DeletedRequirements,"latestBaseline",m.LatestBaseline,"latestBaselineDate",m.LatestBaselineDateUtc,"baselineHealth",m.BaselineHealth,"currentState",m.CurrentModuleState,"complete",m.Complete,"evidence",m.Evidence); }
                foreach(var f in section.Findings) { token.ThrowIfCancellationRequested(); yield return Row(ExportTableKind.DashboardFindings,"snapshotId",id,"findingId",f.Id,"category",f.Category,"code",f.Code,"severity",f.Severity,"moduleIdentity",f.ModuleIdentity,"requirementIdentity",f.RequirementIdentity,"requirementVersionIdentity",f.RequirementVersionIdentity,"absoluteNumber",f.AbsoluteNumber,"analysisId",f.AnalysisIdentity,"analysisSnapshot",f.AnalysisSnapshotIdentity,"asOfUtc",f.TimestampUtc,"rule",f.Rule,"reviewStatus",f.ReviewStatus,"uncertain",f.Uncertain,"evidence",f.Evidence,"explanation",f.Explanation); }
                foreach(var c in section.Charts) yield return Row(ExportTableKind.DashboardCharts,"snapshotId",id,"section",section.Section,"group",c.Group,"label",c.Label,"value",c.Value,"percentage",c.Percentage,"quality",c.Quality);
                foreach(var job in section.Audit?.Recent??Enumerable.Empty<DashboardAuditEntry>()) yield return Row(ExportTableKind.DashboardActivity,"snapshotId",id,"jobId",job.JobId,"jobType",job.JobType,"operator",job.Operator,"startTime",job.StartTimeUtc,"endTime",job.EndTimeUtc,"status",job.Status,"moduleCount",job.AffectedModuleCount,"findingCount",job.FindingCount,"errorSummary",job.ErrorSummary);
            }
            foreach(var trend in s.Trends) yield return Row(ExportTableKind.DashboardTrends,"snapshotId",id,"timestamp",trend.TimestampUtc,"comparableScopeKey",trend.ComparableScopeKey,"metricId",trend.MetricId,"value",trend.Value,"sourceAnalysisId",trend.SourceAnalysisIdentity);
            yield return Row(ExportTableKind.DashboardScore,"snapshotId",id,"score",s.Summary.ConfigurationHealthScore,"status",s.Summary.ScoreStatus,"explanation",string.Join("\n",s.Summary.ScoreExplanation));
        }
        private static ExportRecord Row(ExportTableKind kind,params object[] values)
        { var fields=new Dictionary<string,object>(StringComparer.Ordinal); for(int i=0;i<values.Length;i+=2) fields[(string)values[i]]=values[i+1] is Enum?values[i+1].ToString():values[i+1]; return new ExportRecord(kind,fields); }
    }
}
