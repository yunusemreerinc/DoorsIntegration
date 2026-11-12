using System;
using System.Collections.Generic;
using System.Linq;
using DoorsIntegration.Core;

namespace DoorsIntegration.Export
{
    public static class ExportSchemas
    {
        private static ExportColumn T(string name, bool required = false) => new ExportColumn(name, ExportColumnType.Text, required);
        private static ExportColumn I(string name, bool required = false) => new ExportColumn(name, ExportColumnType.Integer, required);
        private static ExportColumn N(string name) => new ExportColumn(name, ExportColumnType.Number);
        private static ExportColumn B(string name) => new ExportColumn(name, ExportColumnType.Boolean);
        private static ExportColumn D(string name) => new ExportColumn(name, ExportColumnType.DateTime);
        private static ExportTableSchema Schema(ExportTableKind kind, params ExportColumn[] columns) => new ExportTableSchema(kind,
            new[] { I("recordId", true), T("chunkedFields"), T("nullFields") }.Concat(columns));
        public static IReadOnlyList<ExportTableSchema> All { get; } = Array.AsReadOnly(new[]
        {
            Schema(ExportTableKind.Projects, T("scopeId"), T("fullPath", true), T("name"), T("type"), T("accessStatus"), B("metadataVerified"), D("collectedAtUtc")),
            Schema(ExportTableKind.Modules, T("scopeId"), T("fullPath", true), T("name"), T("type"), T("accessStatus"), B("metadataVerified"), D("collectedAtUtc")),
            Schema(ExportTableKind.Requirements, T("scopeId"), T("modulePath", true), T("versionKind"), T("version"), I("absoluteNumber", true),
                T("objectIdentifier"), T("objectNumber"), T("numberStatus"), I("sourceOrder"), I("hierarchyLevel"), I("parentAbsoluteNumber"), T("parentStatus"), B("isDeleted"), T("heading"), T("text")),
            Schema(ExportTableKind.Attributes, T("scopeId"), T("modulePath", true), T("versionKind"), T("version"), T("ownerKind"), I("absoluteNumber"), T("name", true), T("typeName"),
                T("plainStatus"), T("plainText"), T("richTextStatus"), T("richTextWithOle"), T("richTextFormat"), new ExportColumn("typedValue", ExportColumnType.Number), D("typedDateUtc")),
            Schema(ExportTableKind.Links, new[] { T("scopeId"), I("sequence"), T("direction"), T("recordKind"), T("resolution"), B("isEchoed"), T("linkModulePath"), T("linkModuleResolution") }
                .Concat(Endpoint("source")).Concat(Endpoint("target")).ToArray()),
            Schema(ExportTableKind.Scopes, T("scopeId", true), T("sourceKind"), T("modulePath"), T("linkModulePath"), T("versionKind"), T("version"), T("targetProfile"), T("visibility"),
                B("isComplete"), B("isTruncated"), B("enumerationFailed"), T("readStatus"), T("linkModuleStatus"), T("ownership"), T("cleanupStatus"),
                I("itemCount"), I("visitedCount"), I("deniedCount"), I("deletedExcludedCount"), I("unavailableCount"), I("unresolvedCount"), I("sourceReferenceCount"),
                I("fieldProblemCount"), I("surrogateProblemCount"), I("openedModuleCount"), I("cleanupFailureCount"), D("collectedAtUtc"), T("details")),
            Schema(ExportTableKind.Errors, T("code"), T("severity"), I("sourceRecordId"), T("table"), T("column"), I("errorCode"), T("exceptionType"), T("message")),
            Schema(ExportTableKind.ValueChunks, T("ownerTable"), I("ownerRecordId"), T("column"), I("chunkIndex"), T("value")),
            Schema(ExportTableKind.BaselineComparisons, T("comparisonId", true), T("qualifiedModuleId", true), T("modulePath", true),
                I("beforeMajor"), I("beforeMinor"), T("beforeSuffix"), I("afterMajor"), I("afterMinor"), T("afterSuffix"), T("normalization"),
                B("isConclusive"), I("added"), I("deleted"), I("modified"), I("unchanged"), I("indeterminate"), I("normalizationDifferences"), D("comparedAtUtc")),
            Schema(ExportTableKind.BaselineObjects, T("comparisonId", true), T("identity", true), T("qualifiedModuleId", true), I("absoluteNumber", true), T("status", true),
                B("isConclusive"), B("hierarchyChanged"), T("beforeIdentifier"), T("afterIdentifier"), T("beforeObjectNumber"), T("afterObjectNumber"),
                I("beforeOrder"), I("afterOrder"), I("beforeParentId"), I("afterParentId"), I("beforeLevel"), I("afterLevel"),
                T("beforeParentStatus"), T("afterParentStatus"), B("beforeDeleted"), B("afterDeleted")),
            Schema(ExportTableKind.BaselineAttributes, T("comparisonId", true), T("identity", true), T("name", true), T("status", true), B("isConclusive"),
                B("plainChanged"), B("richChanged"), B("typeChanged"), B("normalizationDifference"), T("beforeType"), T("afterType"),
                T("beforePlainStatus"), T("afterPlainStatus"), T("beforePlainText"), T("afterPlainText"), T("beforeRichStatus"), T("afterRichStatus"),
                T("beforeRichTextWithOle"), T("afterRichTextWithOle"), T("richComparison")),
            Schema(ExportTableKind.BaselineEvents, I("eventSchemaVersion"), T("eventId", true), T("comparisonId", true), T("identity", true), T("status", true),
                B("requiresReview"), B("isImpactCandidate"), T("beforeVersion"), T("afterVersion"), T("changedAttributes"), B("hierarchyChanged")),
            Schema(ExportTableKind.BulkBaselineJobs, T("jobId", true), I("revision"), T("operator"), T("databaseIdentifier"), T("environmentIdentifier"), T("clientVersion"), T("clientVersionEvidence"),
                T("source"), T("baselineType"), T("suffix"), T("annotation"), D("createdAtUtc"), D("startedAtUtc"), D("endedAtUtc"), T("outcome"), T("status"), B("isFullSuccess"),
                I("moduleCount"), I("successCount"), I("failedCount"), I("skippedCount"), I("lockedCount"), I("unknownCount"), T("sourcePlanId"), T("sourcePlanFingerprint"), T("inputFingerprint"), T("auditCoverage"), T("auditHeadHash"), T("disclosure")),
            Schema(ExportTableKind.BulkBaselineModules, T("jobId", true), T("stableIdentity", true), T("fullPath", true), T("previousBaseline"), T("expectedBaseline"), T("actualBaseline"), T("actualEvidence"),
                T("group", true), T("resultStatus", true), T("durationSeconds"), T("errorMessage"), T("suffix"), T("annotation"), I("attempts"), T("approvalId"), T("operationId"), D("startedAtUtc"), D("endedAtUtc")),
            Schema(ExportTableKind.BulkBaselineApprovedPlans, T("jobId", true), T("approvalId", true), T("planId"), T("fingerprint"), D("approvedAtUtc"), T("operator"), T("scope"), B("secondRealConfirmation"),
                T("baselineType"), T("stableIdentity", true), T("fullPath"), T("previousBaseline"), T("expectedBaseline"), T("suffix"), T("annotation"), T("preconditionFingerprint"), T("warning")),
            Schema(ExportTableKind.BulkBaselineAuditEvents, T("jobId", true), I("revision"), D("recordedAtUtc"), T("action"), T("status"), T("previousHash"), T("hash"), T("stableIdentity"), T("fullPath"),
                T("previousBaseline"), T("expectedBaseline"), T("actualBaseline"), T("resultStatus"), T("errorMessage"), T("approvalId"), T("operationId")),
            Schema(ExportTableKind.NativeBaselineSetJobs, T("jobId", true), I("revision"), T("action"), T("source"), T("status"), T("reason"), T("definitionId"), T("setId"), T("operator"), T("databaseId"), T("clientVersion"), D("startedAtUtc"), D("endedAtUtc"), T("planFingerprint"), B("explicitApproval"), B("secondRealApproval"), T("requestedModules"), T("name"), T("description"), T("suffix"), T("annotation"), T("disclosure")),
            Schema(ExportTableKind.NativeBaselineSetDefinitions, T("jobId"), I("revision"), T("phase"), T("definitionId"), T("identityEvidence"), T("folderPath"), T("name"), T("description"), T("moduleId"), T("modulePath")),
            Schema(ExportTableKind.NativeBaselineSets, T("jobId"), I("revision"), T("phase"), T("setId"), T("definitionId"), I("setMajor"), I("setMinor"), T("setSuffix"), T("annotation"), T("creator"), D("createdAtUtc"), B("isOpen")),
            Schema(ExportTableKind.NativeBaselineSetMembers, T("jobId"), I("revision"), T("phase"), T("setId"), T("moduleId"), T("modulePath"), T("moduleBaselineVersion"), T("moduleBaselineIdentity"), T("resolution"), B("missing")),
            Schema(ExportTableKind.NativeBaselineSetLinks, T("jobId"), I("revision"), T("phase"), T("sourceModuleId"), I("sourceObjectId"), T("targetModuleId"), I("targetObjectId"), T("linkModuleId"), T("sourceVersion"), T("targetVersion"), B("referenceComplete"), B("verified"), T("problem")),
            Schema(ExportTableKind.BaselineVerificationJobs, T("verificationJobId", true), T("projectId"), T("source"), T("environment"), T("operator"), T("clientVersion"), T("definitionIdentity"), T("definitionName"), T("setIdentity"), I("setMajor"), I("setMinor"), T("setSuffix"), B("isOpen"), I("moduleCount"), I("selectedModuleCount"), I("missingBaselineCount"), D("startedAtUtc"), D("completedAtUtc"), T("overallStatus"), T("policyId"), T("policyFingerprint"), T("snapshotFingerprint"), B("referenceAvailable"), T("limitations"), T("disclosure")),
            Schema(ExportTableKind.BaselineVerificationModules, T("verificationJobId"), T("moduleIdentity"), T("fullPath"), T("moduleBaselineIdentity"), I("major"), I("minor"), T("suffix"), T("version"), B("inDefinition"), B("inSet"), B("selectedForVerification"), T("status"), T("reason"), T("shareableConfiguration"), B("inventoryComplete"), B("linksComplete"), I("knownObjectCount")),
            Schema(ExportTableKind.BaselineVerificationRequirements, T("verificationJobId"), T("nodeIdentity"), T("objectIdentity"), T("moduleIdentity"), T("moduleBaselineIdentity"), T("version"), I("absoluteNumber"), T("displayIdentifier"), T("requirementType"), T("requirementStatus"), T("access"), B("isDeleted"), T("heading"), T("text"), T("richHeading"), T("richTextWithOle")),
            Schema(ExportTableKind.BaselineVerifiedLinks, T("verificationJobId"), T("linkIdentity"), T("observationId"), T("sourceModuleIdentity"), T("sourceModuleBaselineIdentity"), T("sourceVersion"), I("sourceObject"), T("targetModuleIdentity"), T("targetModuleBaselineIdentity"), T("targetVersion"), I("targetObject"), T("linkModuleIdentity"), T("linkModulePath"), T("direction"), B("echoed"), T("status"), T("underlyingStatus"), B("countsAsValid"), T("reason"), T("evidence")),
            Schema(ExportTableKind.BaselineTraceabilityCoverage, T("verificationJobId"), I("totalRequirements"), I("requirementsRequiringLinks"), I("requirementsWithValidLinks"), I("requirementsMissingLinks"), I("unresolvedRequirements"), I("accessDeniedRequirements"), I("unknownPolicyEligibility"), I("unenumeratedModules"), I("brokenChains"), B("denominatorComplete"), new ExportColumn("coveragePercentage", ExportColumnType.Number), new ExportColumn("knownScopeLowerBoundPercentage", ExportColumnType.Number), new ExportColumn("knownScopeUpperBoundPercentage", ExportColumnType.Number), B("conclusive")),
            Schema(ExportTableKind.BaselineTraceabilityFindings, T("verificationJobId"), T("code"), T("severity"), T("moduleIdentity"), T("nodeIdentity"), T("linkIdentity"), T("ruleId"), T("evidence"), T("explanation"), T("recommendation"), B("uncertain")),
            Schema(ExportTableKind.BaselineTraceabilityChanges, T("verificationJobId"), T("changeKind"), T("beforeIdentity"), T("afterIdentity"), B("confirmed"), B("suspectedLinkLoss"), T("evidence")),
            Schema(ExportTableKind.ImpactAnalyses, T("analysisId", true), T("environment"), T("repository"), T("source"), T("target"), T("comparisonKind"), T("graphChoice"), T("status"), T("snapshotIdentity"), T("policyFingerprint"), T("policyJson"), I("reviewDecisionCount"), D("startedAtUtc"), D("completedAtUtc"), I("elapsedMs"), I("managedMemoryDeltaBytes"), I("changedRequirements"), I("uniqueImpactedRequirements"), I("unverifiedImpactReferences"), I("impactPathCount"), B("truncated"), T("disclosure")),
            Schema(ExportTableKind.ImpactComparedModules, T("analysisId"), T("phase"), T("environment"), T("repository"), T("definitionIdentity"), T("setIdentity"), I("setMajor"), I("setMinor"), T("setSuffix"), B("setIsOpen"), T("moduleIdentity"), T("fullPath"), T("moduleBaselineIdentity"), T("version"), I("major"), I("minor"), T("suffix"), B("isCurrent"), T("access"), B("inventoryComplete"), B("linksComplete"), D("snapshotDate"), T("snapshotFingerprint"), B("stale")),
            Schema(ExportTableKind.ImpactRequirementChanges, T("analysisId"), T("requirementIdentity"), T("displayIdentifier"), T("module"), T("sourceVersionIdentity"), T("targetVersionIdentity"), T("primaryType"), T("flags"), T("severity"), T("criticality"), I("reviewPriority"), I("uniqueImpactCount"), I("sourceParentAbsolute"), I("targetParentAbsolute"), I("sourcePosition"), I("targetPosition"), B("conclusive")),
            Schema(ExportTableKind.ImpactAttributeChanges, T("analysisId"), T("requirementIdentity"), T("attribute"), T("sourceType"), T("targetType"), T("oldValue"), T("newValue"), T("oldRichText"), T("newRichText"), T("oldPlainStatus"), T("newPlainStatus"), T("oldRichStatus"), T("newRichStatus"), B("semanticChanged"), B("richRepresentationChanged"), B("normalizationOnly"), B("typeChanged"), B("comparable"), T("reason")),
            Schema(ExportTableKind.ImpactCandidates, T("analysisId"), T("rootChangeIdentity"), T("requirementIdentity"), T("requirementVersionIdentity"), T("referenceIdentity"), B("identityVerified"), T("kind"), I("minimumDepth"), B("external"), T("testRecommendation"), I("pathCount")),
            Schema(ExportTableKind.ImpactPaths, T("analysisId"), T("pathIdentity"), T("rootRequirement"), T("rootVersionIdentity"), T("impactedRequirement"), T("impactedVersionIdentity"), I("pathDepth"), I("stepDepth"), T("sourceNode"), T("targetNode"), T("linkIdentity"), T("linkModuleIdentity"), T("linkType"), T("direction"), T("baselineContext"), B("confirmed")),
            Schema(ExportTableKind.ImpactLinkChanges, T("analysisId"), T("kind"), T("beforeIdentity"), T("afterIdentity"), B("confirmed"), B("suspectedLinkLoss"), T("evidence"), T("beforeEndpointEvidence"), T("afterEndpointEvidence")),
            Schema(ExportTableKind.ImpactFindings, T("analysisId"), T("findingIdentity"), T("code"), T("rootChangeIdentity"), T("impactedRequirement"), T("impactedVersionIdentity"), T("pathIdentity"), I("depth"), T("kind"), T("severity"), T("changeFlags"), T("evidenceQuality"), T("evidenceFingerprint"), T("reviewStatus"), T("reviewerComments"), T("reason")),
            Schema(ExportTableKind.ImpactReviews, T("analysisId"), T("findingIdentity"), T("snapshotIdentity"), T("evidenceFingerprint"), T("reviewer"), D("reviewDate"), T("decision"), T("rationale")),
            Schema(ExportTableKind.ImpactLimitations, T("analysisId"), T("reason")),
            Schema(ExportTableKind.DashboardMetadata,T("snapshotId",true),T("report"),D("generatedOn"),T("environment"),T("databaseIdentity"),B("isMock"),D("snapshotTimestamp"),T("scopeKey"),T("scopeJson"),T("traceabilityPolicy"),T("qualityProfile"),T("sourceAnalysisIds"),T("completeness"),B("isStale"),T("warnings"),T("disclosure")),
            Schema(ExportTableKind.DashboardMetrics,T("snapshotId"),T("section"),T("metricId"),T("name"),N("value"),T("unit"),T("scope"),T("source"),D("asOfUtc"),T("completeness"),B("isStale"),B("isLowerBound"),T("warning")),
            Schema(ExportTableKind.DashboardModules,T("snapshotId"),T("moduleIdentity"),T("path"),T("project"),T("baselineIdentity"),T("version"),T("access"),I("requirements"),I("deleted"),T("latestBaseline"),D("latestBaselineDate"),T("baselineHealth"),T("currentState"),B("complete"),T("evidence")),
            Schema(ExportTableKind.DashboardFindings,T("snapshotId"),T("findingId"),T("category"),T("code"),T("severity"),T("moduleIdentity"),T("requirementIdentity"),T("requirementVersionIdentity"),I("absoluteNumber"),T("analysisId"),T("analysisSnapshot"),D("asOfUtc"),T("rule"),T("reviewStatus"),B("uncertain"),T("evidence"),T("explanation")),
            Schema(ExportTableKind.DashboardCharts,T("snapshotId"),T("section"),T("group"),T("label"),N("value"),N("percentage"),T("quality")),
            Schema(ExportTableKind.DashboardActivity,T("snapshotId"),T("jobId"),T("jobType"),T("operator"),D("startTime"),D("endTime"),T("status"),I("moduleCount"),I("findingCount"),T("errorSummary")),
            Schema(ExportTableKind.DashboardTrends,T("snapshotId"),D("timestamp"),T("comparableScopeKey"),T("metricId"),N("value"),T("sourceAnalysisId")),
            Schema(ExportTableKind.DashboardScore,T("snapshotId"),N("score"),T("status"),T("explanation"))
        });
        private static IEnumerable<ExportColumn> Endpoint(string prefix)
        {
            yield return T(prefix + "ModulePath"); yield return T(prefix + "VersionKind"); yield return T(prefix + "Version");
            yield return I(prefix + "AbsoluteNumber"); yield return T(prefix + "ObjectIdentifier"); yield return T(prefix + "Resolution");
            yield return T(prefix + "SurrogateStatus"); yield return T(prefix + "ExternalReference"); yield return T(prefix + "ReferenceStatus");
        }
        public static ExportTableSchema Get(ExportTableKind kind) => All.FirstOrDefault(schema => schema.Kind == kind) ?? throw new ArgumentOutOfRangeException(nameof(kind));
    }
}
