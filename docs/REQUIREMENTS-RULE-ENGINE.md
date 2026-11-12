# Requirements Quality Rule Engine

`DoorsIntegration.Processing.RequirementsQualityRuleEngine` operates on read-only QualityArtifact streams, independently of Core/COM/DXL. It introduces **Rule, RuleContext, RuleResult, RuleProfile and RuleSeverity**. New configuration uses **JSON**; the existing XML QualityProfile/QualityEngine and XML batch gate remain compatible. YAML is not implemented.

**Automation is review assistance.** Passed, FullyEvaluated, an approved profile or an accepted/waived finding is not a certification decision. Summaries keep CertificationDecision=NotAssessed, ComplianceAssessed=false and ToolQualified=false. Each new result carries a review-only disclosure.

## Starter rules

| Kind | Meaning |
|---|---|
| EmptyRequirement | At least one mapped content field must be nonblank. Whitespace/format-only content is blank. Scope the rule to actual requirement objects; structural headings can use a different map or reviewer NotApplicable. |
| AmbiguousWords | Configured literal Unicode words/phrases with invariant case-insensitive boundaries. Terms are escaped, not executable regex. Exact UTF-16 offsets/excerpts and value hashes are retained. This is a lexical indicator, not semantic ambiguity detection. |
| MultipleObligations | Counts non-overlapping modal/obligation indicators, longer phrases first, with an effective minimum of two. A conjunction alone does not prove independent obligations. Conditional/alternative clauses can be flagged for human review. |
| Verifiability | Requires minimumCount nonempty mapped verification-evidence fields. Templates require both method and acceptance criteria. Their presence does not prove semantic adequacy. |
| RequiredAttributes | Checks only fields selected by the project profile. Native DOORS attribute catalogs do not automatically become a mandatory-field list. |
| Traceability | Requires distinct confirmed counterparts within an exact link-module/direction/repository/version scope. Duplicate observations do not increase coverage. Optional target-module, version and native link-module ID constraints apply. |

A dictionary entry absent from supplied fields means **NotRequested**, not proof that a DOORS attribute is missing. Explicit QualityAvailability.Missing is known absence. Empty/Missing may produce a finding; Denied/NotRequested/Failed/TooLarge evidence produces NotEvaluated. A positive finding can coexist with incomplete evidence, which is recorded separately.

Traceability cannot be declared missing from unresolved/inaccessible/reference/deleted endpoints or incomplete scope. Enough positive counterparts may satisfy a minimum while EvidenceComplete stays false. Unknown source/baseline context is not guessed. SameVersion, CurrentTarget and AnyExplicitVersion are explicit choices. A native-ID-required rule cannot be satisfied by a path-only observation.

## JSON profiles

Bundled unapproved templates:

- scripts/quality/general-rules.example.json
- scripts/quality/do178c-rules.example.json
- scripts/quality/do200b-rules.example.json
- scripts/quality/do330-rules.example.json

DO-178C, DO-200B and DO-330 are selectable **review-context labels**. No complete standard, normative mandatory-field list or objective/section mapping is claimed. All templates are ExampleOnly with no approval; their trace link-module path is intentionally unconfigured. Their field names and lexicons are project examples.

Root schema: schemaVersion (1), id, revision, standard (General/DO178C/DO200B/DO330), projectId, projectReference, exampleOnly, rules and optional approval. Rule entries require id, revision, kind, severity and enabled. Optional fields are fields, terms, evidenceFields, minimumCount, linkModulePath, direction, targetModules, versionPolicy, requiredNativeLinkModuleId and suggestion. Example:

```json
{
  "id": "ambiguity",
  "revision": "1",
  "kind": "AmbiguousWords",
  "severity": "Warning",
  "enabled": true,
  "fields": ["Object Text"],
  "terms": ["TBD", "as needed", "appropriate", "gerektiğinde"],
  "suggestion": "Use an approved measurable criterion or record a reviewer rationale."
}
```

The loader rejects unknown/duplicate properties, unknown enums/kinds, wrong JSON types, excessive collections and stale approvals. Files are capped at 1 MiB; strings/arrays are bounded. JSON does not load arbitrary classes, regex, DXL or executable scripts. Custom Rule implementations are registered by trusted host code and cannot be serialized as executable profile definitions.

## Project approval

1. Copy/select a template. Map content, verification, required fields and link paths to the approved project checklist. Review severity, terms, thresholds and applicability.
2. Bind a project ID and project-plan/reference; project profiles are ExampleOnly=false. At least one enabled rule and all enabled mappings must be configured.
3. Review the exact profile and acknowledge its full fingerprint, reviewer and approval reference. Approval is immutable and specific to that project/configuration. API expiry is optional UTC.
4. Save a new JSON file. Editing any rule/field/threshold/revision invalidates the content fingerprint. A stale/foreign approval is rejected; rebinding the project drops approval.

ApprovedProfileOnly is the default execution mode. DraftPreview must be selected explicitly for pre-approval analysis and cannot produce a FullyEvaluated summary. A profile bound to another project is blocked even in preview. These approvals are local attestations; reviewer authority is not authenticated and no digital certification signature is provided.

```csharp
var loaded = RuleProfile.LoadJson(@"C:\Profiles\project-draft.json");
// First explicitly map/review loaded.Rules for this project.
var project = loaded.ForProject("FLIGHT-A", "ORG-QUALITY-PLAN-7", loaded.Rules);
var approved = project.Approve(project.Fingerprint, "reviewer-id",
    "PROFILE-REVIEW-17", DateTime.UtcNow, DateTime.UtcNow.AddYears(1));
approved.SaveJson(@"C:\Profiles\approved-v1.json"); // New file only.

var run = new RuleRunContext("FLIGHT-A", "snapshot-reference", sourceComplete,
    DateTime.UtcNow, RuleExecutionMode.ApprovedProfileOnly, traceProvider);
using (var report = new FileRuleReport(@"C:\Reports\quality-run-001"))
    new RequirementsQualityRuleEngine().Evaluate(artifacts, approved, run,
        report, cancellationToken);
```

A host must not call Approve automatically on a user's behalf. WPF requires exact-fingerprint acknowledgement before saving the approved file.

## Evidence and reviewer disposition

RuleResult retains severity, automatic state/code, explanation, suggestion, field availability, bounded excerpts, UTF-16 offset/length, full value hashes (including opaque rich text), profile/input/context/result fingerprints and UTC evaluation time. Rich text is not executed, normalized or used to replace source values. EvidenceTruncated marks limits; a cap cannot turn an unmet obligation threshold into Passed.

New automatic results start **Pending**. ReviewerDisposition supports Pending, Accepted, Rejected, Waived, NeedsClarification and NotApplicable. A human decision keeps reviewer, reason, reference, UTC time and revision hash; it does not rewrite the automatic finding or grant certification.

```csharp
var store = new FileRuleReviewStore(@"C:\Reviews\dispositions.jsonl",
    new ProcessingClock());
var current = store.ApplyLatest(originalResult);
var reviewed = store.Save(originalResult, originalResult.ResultFingerprint,
    ReviewerDisposition.Accepted, "reviewer-id",
    "Finding confirmed; change request opened", "REVIEW-42",
    current.ReviewerDecision?.RevisionHash ?? "");
```

The store locks the local ledger, checks exact result acknowledgement and the expected prior review revision, appends and flushes a chained record. Concurrent/stale updates, incomplete tails and invalid chains are rejected without overwriting prior records. Changed source/rich text/profile/context/evaluation metadata cannot silently reuse a decision. VerifyIntegrity checks the automatic result evidence fingerprint. Ledger chaining detects accidental edits; it is not authentication or a tamper-proof signature. Do not import untrusted reports as authoritative review records.

results.jsonl is the original automatic-run snapshot. Decisions persist in a separate ledger; ApplyLatest overlays an exact matching decision and returns a new immutable result, which a host can write to a new report. An interactive per-finding disposition editor is not added in this version; the decision workflow is an API.

## WPF and adapter compatibility

**İşlemler & Settings → Kalite kontrolü** accepts XML or JSON. XML uses the existing legacy path. JSON adds project ID, ApprovedProfileOnly/DraftPreview and General/DO178C/DO200B/DO330 selection. **Örnek JSON profili kaydet** writes a draft. Configure its fields/link path before **Proje profilini incele ve onayla…**. The modal displays rule settings and requires the full fingerprint plus acknowledgement to save a new approved file. Local profile operations work in Mock/read-only mode; they do not enable native writes.

Analysis uses retained requirement snapshots. SnapshotRuleTraceabilityProvider indexes matching retained links once, without a COM call. Repository and baseline label are caller-bound; foreign identities are rejected. Unbound cross-module opaque baseline labels remain uncertain under SameVersion. Missing trace scope or native link-module ID is not fabricated as complete coverage.

Console/legacy Desktop DI also exposes RequirementsQualityRuleEngine. The WinForms XML screen and existing XML batch gate remain unchanged; JSON profiles are not silently installed as native write gates.

## Limits and extension

The engine streams artifacts/results, with defaults of 100,000 artifacts, 65,536 evaluated field characters, 32 evidence items/result and 256 characters/excerpt. Input hashing streams values without concatenating the dataset. FileRuleReport creates a new directory with results.jsonl and summary.json and a default 256 MiB budget. Provider/report/ledger limits are separate. Cancellation, source failure, duplicate/unknown identity, truncation, RuleError and incomplete evidence are visible in the summary. A sink failure propagates without publishing a successful summary. FullyEvaluated can coexist with Findings; it means checks were evaluated, not that requirements are acceptable.

Custom Rule classes must provide a semantic configuration fingerprint and declare RequiresTraceability where needed. The engine verifies result identity/profile/context binding, isolates failures and rejects pre-attached reviewer decisions. It retries neither rules nor providers. The module has no dependency on the DOORS adapter and performs no database mutations.
