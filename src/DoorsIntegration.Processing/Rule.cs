using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace DoorsIntegration.Processing
{
    public abstract class Rule
    {
        protected Rule(string id, string revision, RuleSeverity severity, bool enabled = true)
        { Id = ProcessingGuard.Text(id, 128); Revision = ProcessingGuard.Text(revision, 64); if (!Enum.IsDefined(typeof(RuleSeverity), severity)) throw new ArgumentException(); Severity = severity; Enabled = enabled; }
        public string Id { get; }
        public string Revision { get; }
        public RuleSeverity Severity { get; }
        public bool Enabled { get; }
        public abstract string ConfigurationFingerprint { get; }
        public virtual string ConfigurationIssue => "";
        public virtual bool RequiresTraceability => false;
        public abstract RuleResult Evaluate(RuleContext context, CancellationToken token = default(CancellationToken));
    }

    public sealed class ConfiguredRule : Rule
    {
        private readonly Regex termsPattern;
        public ConfiguredRule(string id, string revision, RuleKind kind, RuleSeverity severity, IEnumerable<string> fields = null,
            IEnumerable<string> terms = null, IEnumerable<string> evidenceFields = null, int minimumCount = 1,
            string linkModulePath = "", CoverageDirection direction = CoverageDirection.Outgoing, IEnumerable<string> targetModules = null,
            CoverageVersionPolicy versionPolicy = CoverageVersionPolicy.SameVersion, string requiredNativeLinkModuleId = null,
            bool enabled = true, string suggestion = "") : base(id, revision, severity, enabled)
        {
            if (!Enum.IsDefined(typeof(RuleKind), kind) || !Enum.IsDefined(typeof(CoverageDirection), direction) || !Enum.IsDefined(typeof(CoverageVersionPolicy), versionPolicy) || minimumCount < 1 || minimumCount > 100000) throw new ArgumentException("Invalid rule options.");
            Kind = kind; Fields = RuleGuard.Strings(fields, 32, 256); Terms = RuleGuard.Strings(terms, 128, 128); EvidenceFields = RuleGuard.Strings(evidenceFields, 32, 256);
            TargetModules = Array.AsReadOnly(RuleGuard.Strings(targetModules, 64, 4096).Select(RuleGuard.Path).ToArray());
            LinkModulePath = string.IsNullOrEmpty(linkModulePath) ? "" : RuleGuard.Path(linkModulePath); Direction = direction; VersionPolicy = versionPolicy;
            RequiredNativeLinkModuleId = requiredNativeLinkModuleId == null ? null : ProcessingGuard.Text(requiredNativeLinkModuleId, 1024);
            MinimumCount = minimumCount; Suggestion = ProcessingGuard.Text(suggestion ?? "", 4096, true, true);
            ConfigurationIssue = kind == RuleKind.Traceability ? (LinkModulePath.Length == 0 ? "TraceScopeNotConfigured" : "") :
                kind == RuleKind.Verifiability ? (EvidenceFields.Count == 0 ? "VerificationMappingNotConfigured" : "") : Fields.Count == 0 ? "FieldMappingNotConfigured" : "";
            if (kind == RuleKind.Verifiability && EvidenceFields.Count > 0 && minimumCount > EvidenceFields.Count) ConfigurationIssue = "VerificationThresholdExceedsMappedFields";
            if (kind == RuleKind.AmbiguousWords || kind == RuleKind.MultipleObligations)
            {
                if (Terms.Count == 0) ConfigurationIssue = "LexiconNotConfigured";
                else termsPattern = new Regex(@"(?<![\p{L}\p{N}_])(?:" + string.Join("|", Terms.OrderByDescending(t => t.Length).ThenBy(t => t, StringComparer.Ordinal).Select(Regex.Escape)) + @")(?![\p{L}\p{N}_])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            }
            ConfigurationFingerprint = RuleGuard.Hash(new[] { "ConfiguredRule-v1", Id, Revision, Kind.ToString(), Severity.ToString(), Enabled.ToString(), string.Join("\n", Fields), string.Join("\n", Terms),
                string.Join("\n", EvidenceFields), ProcessingGuard.Number(minimumCount), LinkModulePath, direction.ToString(), string.Join("\n", TargetModules), versionPolicy.ToString(), requiredNativeLinkModuleId ?? "", Suggestion });
        }
        public RuleKind Kind { get; }
        public IReadOnlyList<string> Fields { get; }
        public IReadOnlyList<string> Terms { get; }
        public IReadOnlyList<string> EvidenceFields { get; }
        public int MinimumCount { get; }
        public string LinkModulePath { get; }
        public CoverageDirection Direction { get; }
        public IReadOnlyList<string> TargetModules { get; }
        public CoverageVersionPolicy VersionPolicy { get; }
        public string RequiredNativeLinkModuleId { get; }
        public string Suggestion { get; }
        public override string ConfigurationIssue { get; }
        public override string ConfigurationFingerprint { get; }
        public override bool RequiresTraceability => Kind == RuleKind.Traceability;

        public override RuleResult Evaluate(RuleContext context, CancellationToken token = default(CancellationToken))
        {
            if (context == null) throw new ArgumentNullException(nameof(context)); token.ThrowIfCancellationRequested();
            if (!Enabled) return Result(context, RuleState.Skipped, "RuleDisabled", "Kural bu profilde seçili değil.");
            if (context.Artifact.IsDeleted) return Result(context, RuleState.Skipped, "DeletedArtifact", "Silinmiş nesne aktif gereksinim kontrolüne alınmadı.");
            if (ConfigurationIssue.Length != 0) return Result(context, RuleState.NotEvaluated, ConfigurationIssue, "Kuralın proje alanları veya traceability kapsamı yapılandırılmalı.", complete: false);
            if (Kind == RuleKind.Traceability) return Trace(context, token);
            var names = Kind == RuleKind.Verifiability ? EvidenceFields : Fields;
            var fields = names.Select(context.Field).ToArray();
            if (fields.Any(f => f.Value?.Length > context.Run.MaxFieldCharacters || f.OpaqueRichText?.Length > context.Run.MaxFieldCharacters))
                return Result(context, RuleState.NotEvaluated, "FieldTooLarge", "Alan analiz sınırını aşıyor; kesilmiş metinden olumlu sonuç üretilmedi.", complete: false);
            if (Kind == RuleKind.EmptyRequirement || Kind == RuleKind.Verifiability)
            {
                var present = fields.Where(f => f.Availability == QualityAvailability.Available && !Blank(f.Value)).ToArray();
                int required = Kind == RuleKind.EmptyRequirement ? 1 : MinimumCount;
                if (present.Length >= required) return Result(context, RuleState.Passed, Kind == RuleKind.EmptyRequirement ? "ContentPresent" : "VerificationEvidencePresent",
                    Kind == RuleKind.EmptyRequirement ? "Seçili içerik alanlarından en az biri dolu." : "Doğrulama kanıtı dolu alan sayısı=" + present.Length + "; eşik=" + required + ". İçeriğin yeterliliği insan incelemesi gerektirir.",
                    present.Take(context.Run.MaxEvidencePerResult).Select(f => context.Evidence(f.Name, "Present")), truncated: present.Length > context.Run.MaxEvidencePerResult);
                bool unknown = fields.Any(Unavailable);
                return Result(context, unknown ? RuleState.NotEvaluated : RuleState.Finding, unknown ? "EvidenceUnavailable" : Kind == RuleKind.EmptyRequirement ? "EmptyRequirement" : "VerificationEvidenceMissing",
                    unknown ? "Okunamayan veya istenmemiş alanlar eksiklik kanıtı değildir." : Kind == RuleKind.EmptyRequirement ? "Gereksinim içeriği boş veya bilinen eksik durumda." : "Yapılandırılan doğrulama kanıt alanları boş veya bilinen eksik durumda.",
                    fields.Take(context.Run.MaxEvidencePerResult).Select(f => context.Evidence(f.Name, unknown ? "UnavailableOrEmpty" : "EmptyOrMissing")), !unknown, fields.Length > context.Run.MaxEvidencePerResult);
            }
            if (Kind == RuleKind.RequiredAttributes)
            {
                var missing = fields.Where(f => f.Availability == QualityAvailability.Missing || (f.Availability == QualityAvailability.Available && Blank(f.Value))).ToArray();
                bool unknown = fields.Any(Unavailable);
                return Result(context, missing.Length > 0 ? RuleState.Finding : unknown ? RuleState.NotEvaluated : RuleState.Passed,
                    missing.Length > 0 ? "RequiredAttributeMissing" : unknown ? "EvidenceUnavailable" : "RequiredAttributesPresent",
                    missing.Length > 0 ? "Proje profilinin zorunlu seçtiği alanlarda boş/eksik değer bulundu." : unknown ? "Alan erişimi veya okuma kapsamı zorunlu değerleri doğrulamaya yetmiyor." : "Yapılandırılan zorunlu alanlar dolu.",
                    fields.Take(context.Run.MaxEvidencePerResult).Select(f => context.Evidence(f.Name, "RequiredAttribute")), !unknown, fields.Length > context.Run.MaxEvidencePerResult);
            }
            var evidence = new List<RuleEvidence>(); bool truncated = false, unavailable = fields.Any(Unavailable); int count = 0;
            if (!fields.Any(f => f.Availability == QualityAvailability.Available)) return Result(context, RuleState.NotEvaluated, "NoReadableText", "Sözlük taraması için okunabilir metin yok.", complete: false);
            foreach (var field in fields.Where(f => f.Availability == QualityAvailability.Available))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var match = termsPattern.Match(field.Value);
                    while (match.Success)
                    {
                        token.ThrowIfCancellationRequested(); count++;
                        if (evidence.Count == context.Run.MaxEvidencePerResult) { truncated = true; break; }
                        evidence.Add(context.Evidence(field.Name, Kind == RuleKind.AmbiguousWords ? "AmbiguousTerm" : "ObligationIndicator", match.Index, match.Length)); match = match.NextMatch();
                    }
                }
                catch (RegexMatchTimeoutException) { return Result(context, RuleState.RuleError, "LexicalEvaluationTimeout", "Sözlük taraması süre sınırını aştı; başarılı kabul edilmedi.", evidence, false, true); }
                if (truncated) break;
            }
            bool found = Kind == RuleKind.AmbiguousWords ? count > 0 : count >= Math.Max(2, MinimumCount);
            if (truncated && !found) return Result(context, RuleState.NotEvaluated, "EvidenceLimit", "Kanıt sınırı nedeniyle yükümlülük eşiği hakkında sonuç üretilemedi.", evidence, false, true);
            return Result(context, found ? RuleState.Finding : unavailable ? RuleState.NotEvaluated : RuleState.Passed,
                found ? Kind == RuleKind.AmbiguousWords ? "AmbiguousWordsFound" : "MultipleObligationCandidates" : unavailable ? "EvidenceUnavailable" : "NoConfiguredIndicatorFound",
                found ? Kind == RuleKind.AmbiguousWords ? "Yapılandırılan belirsizlik sözlüğüyle eşleşme bulundu; bağlamı inceleyin." : "Birden çok yükümlülük göstergesi bulundu. Bağımsız yükümlülük oldukları anlamsal olarak doğrulanmadı." :
                    unavailable ? "Eksik okuma kanıtından temiz metin sonucu çıkarılmadı." : "Yapılandırılan göstergeler bulunmadı; bu, anlamsal kalite garantisi değildir.", evidence, !unavailable, truncated);
        }
        private RuleResult Trace(RuleContext context, CancellationToken token)
        {
            var scope = context.Traceability.SingleOrDefault(t => t.LinkModulePath == LinkModulePath && t.Direction == Direction);
            if (scope == null) return Result(context, RuleState.NotEvaluated, "TraceEvidenceUnavailable", "Seçili link module/yön/nesne sürümü için traceability kanıtı yok.", complete: false);
            if (RequiredNativeLinkModuleId != null && scope.NativeLinkModuleId != RequiredNativeLinkModuleId)
                return Result(context, RuleState.NotEvaluated, scope.NativeLinkModuleId == null ? "NativeLinkIdentityUnavailable" : "NativeLinkIdentityMismatch", "Link module native kimliği doğrulanamadı.", complete: false);
            var distinct = new HashSet<string>(StringComparer.Ordinal); bool uncertain = !scope.ScopeComplete;
            foreach (var link in scope.Links)
            {
                token.ThrowIfCancellationRequested(); var target = link.Counterpart;
                if (link.Resolution != RuleTraceResolution.Confirmed || target == null || target.VersionKind == ArtifactVersion.Unknown) { uncertain = true; continue; }
                if (target.RepositoryId != context.Artifact.Identity.RepositoryId) { uncertain = true; continue; }
                if (VersionPolicy == CoverageVersionPolicy.SameVersion && target.VersionKind == context.Artifact.Identity.VersionKind && !link.VersionContextKnown) { uncertain = true; continue; }
                bool version = VersionPolicy == CoverageVersionPolicy.AnyExplicitVersion || (VersionPolicy == CoverageVersionPolicy.CurrentTarget ? target.VersionKind == ArtifactVersion.Current :
                    target.VersionKind == context.Artifact.Identity.VersionKind && target.Version == context.Artifact.Identity.Version);
                if (version && (TargetModules.Count == 0 || TargetModules.Contains(target.ContainerId, StringComparer.Ordinal))) distinct.Add(target.Key);
            }
            bool enough = distinct.Count >= MinimumCount;
            var evidence = new[] { new RuleEvidence(LinkModulePath, QualityAvailability.Available, "DistinctConfirmedCounterparts=" + distinct.Count.ToString(CultureInfo.InvariantCulture),
                excerpt: "Direction=" + Direction + "; ScopeComplete=" + scope.ScopeComplete, valueFingerprint: scope.Fingerprint) };
            return Result(context, enough ? RuleState.Passed : uncertain ? RuleState.NotEvaluated : RuleState.Finding,
                enough ? "TraceabilityPresent" : uncertain ? "TraceabilityUnknown" : "TraceabilityMissing",
                enough ? "Yapılandırılan kapsamda yeterli farklı doğrulanmış karşı uç gözlendi." : uncertain ? "Eksik/çözümlenemeyen traceability kapsamında link yokluğu kanıtlanamaz." : "Tam olarak bildirilen kapsamda beklenen traceability sayısına ulaşılamadı.", evidence, !uncertain);
        }
        private static bool Unavailable(QualityField f) => f.Availability != QualityAvailability.Available && f.Availability != QualityAvailability.Missing;
        private static bool Blank(string value) => value.All(c => char.IsWhiteSpace(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format);
        private RuleResult Result(RuleContext context, RuleState state, string code, string explanation, IEnumerable<RuleEvidence> evidence = null, bool complete = true, bool truncated = false) =>
            RuleResult.Create(context, this, state, code, explanation, Suggestion.Length > 0 ? Suggestion : DefaultSuggestion(Kind), evidence, complete, truncated);
        private static string DefaultSuggestion(RuleKind kind)
        {
            switch (kind)
            { case RuleKind.EmptyRequirement: return "Gerçek gereksinim nesnelerine uygulanacak alan eşlemesini doğrulayın ve gereksinimi açıkça yazın.";
                case RuleKind.AmbiguousWords: return "Belirsiz ifadeyi proje bağlamında ölçülebilir, kesin bir ifadeyle değiştirin veya gerekçelendirin.";
                case RuleKind.MultipleObligations: return "Yükümlülükler bağımsızsa ayrı gereksinimlere ayırın; koşul ve alternatifleri insan incelemesiyle değerlendirin.";
                case RuleKind.Verifiability: return "Doğrulama yöntemi ve kabul ölçütünü projenin onaylı inceleme planına göre belirtin.";
                case RuleKind.RequiredAttributes: return "Projenin onayladığı zorunlu alanları doldurun veya uygulanamazlık gerekçesi kaydedin.";
                default: return "Doğru repository, baseline, link module ve erişim kapsamını doğrulayın; beklenen ilişkiyi insan incelemesine sunun."; }
        }
    }
}
