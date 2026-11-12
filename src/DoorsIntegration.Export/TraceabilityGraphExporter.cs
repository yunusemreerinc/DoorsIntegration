using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using DoorsIntegration.Core;

namespace DoorsIntegration.Export
{
    public sealed class TraceabilityGraphExporter : ITraceabilityGraphExporter
    {
        public void Export(Stream destination, TraceabilityGraph graph, TraceabilityGraphFormat format, TraceabilityImpactResult impact = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (destination == null || graph == null) throw new ArgumentNullException();
            if (!Enum.IsDefined(typeof(TraceabilityGraphFormat), format) || (impact != null && impact.GraphId != graph.GraphId)) throw new ArgumentException("Invalid graph export or foreign impact result.");
            cancellationToken.ThrowIfCancellationRequested();
            using (var writer = new StreamWriter(destination, new UTF8Encoding(false, true), 16384, true))
            {
                if (format == TraceabilityGraphFormat.GraphMl) WriteGraphMl(writer, graph, impact, cancellationToken);
                else if (format == TraceabilityGraphFormat.Json)
                {
                    writer.Write("{\"schemaVersion\":1,\"format\":\"doors-traceability-graph\",\"records\":["); bool first = true;
                    foreach (var record in Records(graph, impact, true)) { cancellationToken.ThrowIfCancellationRequested(); if (!first) writer.Write(','); first = false; JsonObject(writer, record); }
                    writer.Write("]}");
                }
                else
                {
                    CsvRow(writer, new[] { "RecordType", "Id", "SourceId", "TargetId", "NodeId", "RuleId", "Code", "DataJson" });
                    foreach (var record in Records(graph, impact, true))
                    {
                        cancellationToken.ThrowIfCancellationRequested(); string data; using (var text = new StringWriter(CultureInfo.InvariantCulture)) { JsonObject(text, record); data = text.ToString(); }
                        CsvRow(writer, new[] { Get(record, "kind"), Get(record, "id"), Get(record, "sourceId"), Get(record, "targetId"), Get(record, "nodeId"), Get(record, "ruleId"), Get(record, "code"), data });
                    }
                }
            }
        }
        public void ExportFile(string path, TraceabilityGraph graph, TraceabilityGraphFormat format, TraceabilityImpactResult impact = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A new output file is required.");
            string full = Path.GetFullPath(path); if (File.Exists(full) || Directory.Exists(full)) throw new IOException("Existing graph export is never overwritten.");
            string stage = Path.Combine(Path.GetDirectoryName(full), ".traceability-graph-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { Export(stream, graph, format, impact, cancellationToken); stream.Flush(true); }
                cancellationToken.ThrowIfCancellationRequested(); File.Move(stage, full);
            }
            finally { if (File.Exists(stage)) File.Delete(stage); }
        }
        private static Dictionary<string, object> Row(string kind, string id, params object[] values)
        { var row = new Dictionary<string, object>(StringComparer.Ordinal) { { "kind", kind }, { "id", id } }; for (int i = 0; i < values.Length; i += 2) row.Add((string)values[i], values[i + 1]); return row; }
        private static IEnumerable<Dictionary<string, object>> Records(TraceabilityGraph graph, TraceabilityImpactResult impact, bool elements)
        {
            yield return Row("Graph", graph.GraphId.ToString("N"), "builtAtUtc", graph.BuiltAtUtc.ToString("O"), "repositoryId", graph.Request.RepositoryId, "repositoryEvidence", graph.RepositoryEvidence,
                "policyId", graph.Request.Policy.Id, "policyFingerprint", graph.Request.Policy.Fingerprint, "readOnly", true, "atomicSnapshot", false, "scopeComplete", graph.IsCompleteWithinRequestedScope,
                "hasExternalBoundary", graph.HasExternalBoundary, "nodeCount", graph.Nodes.Count, "edgeCount", graph.Edges.Count, "observationCount", graph.Observations.Count, "target", DoorsVersions.Format(graph.Request.Target), "connection", graph.Request.ConnectionMode.ToString());
            foreach (var module in graph.Request.Modules) yield return Row("ModuleScope", module.Key, "modulePath", module.ModulePath, "role", module.Role, "versionKind", module.Baseline == null ? "Current" : "Baseline",
                "version", module.Version, "baselineMajor", module.Baseline?.Major, "baselineMinor", module.Baseline?.Minor, "baselineSuffix", module.Baseline?.Suffix, "callerObservedBaselineVersion", module.ObservedBaselineVersion);
            foreach (var rule in graph.Request.Policy.LinkRules) yield return Row("LinkRule", rule.Id, "ruleId", rule.Id, "anchorRole", rule.AnchorRole, "counterpartRoles", rule.CounterpartRoles,
                "linkModulePath", rule.LinkModulePath, "requiredNativeLinkModuleId", rule.RequiredNativeLinkModuleId, "direction", rule.Direction.ToString(), "minimum", rule.MinimumLinks, "maximum", rule.MaximumLinks,
                "versionPolicy", rule.VersionPolicy.ToString(), "rejectUnexpectedCounterparts", rule.RejectUnexpectedCounterparts);
            yield return Row("OrphanPolicy", graph.Request.Policy.Id, "roles", graph.Request.Policy.OrphanRoles, "severity", graph.Request.Policy.OrphanSeverity.ToString());
            foreach (var rule in graph.Request.Policy.CycleRules) yield return Row("CycleRule", rule.Id, "ruleId", rule.Id, "behavior", rule.Behavior.ToString(), "roles", rule.Roles, "linkModulePaths", rule.LinkModulePaths);
            foreach (var scope in graph.Scopes)
            {
                var links = scope.Links; var requirements = scope.Requirements;
                yield return Row("ReadScope", scope.Id, "moduleScopeId", scope.Module.Key, "complete", scope.IsComplete, "modulePath", scope.Module.ModulePath,
                    "sourceKind", links == null ? "Requirements" : "Links", "linkModulePath", links?.Request.LinkModulePath, "nativeAnchorModuleId", links?.QualifiedAnchorModuleId ?? requirements?.QualifiedModuleId,
                    "nativeLinkModuleId", links?.QualifiedLinkModuleId, "moduleStatus", links == null ? requirements.ModuleStatus.ToString() : links.ModuleStatus.ToString(), "linkModuleStatus", links?.LinkModuleStatus.ToString(),
                    "recordCount", links == null ? requirements.ObjectCount : links.RecordCount, "deniedObjects", links == null ? requirements.DeniedObjectCount : links.DeniedObjectCount,
                    "truncated", links == null ? requirements.IsTruncated : links.IsTruncated, "enumerationFailed", links == null ? requirements.EnumerationFailed : links.EnumerationFailed,
                    "unresolvedLinks", links?.UnresolvedLinkCount, "sourceReferences", links?.SourceReferenceCount, "cleanupFailures", links?.CleanupFailureCount, "surrogateProblems", links?.SurrogateProblemCount);
            }
            if (elements)
            { foreach (var node in graph.Nodes.Values.OrderBy(n => n.Id, StringComparer.Ordinal)) yield return Node(node); foreach (var edge in graph.Edges) yield return Edge(edge); }
            foreach (var observation in graph.Observations)
            {
                var r = observation.Record;
                var row = Row("Observation", observation.Id, "edgeId", observation.EdgeId, "scopeId", observation.ScopeId, "sequence", r.Sequence, "direction", r.Direction.ToString(), "recordKind", r.Kind.ToString(),
                    "resolution", r.Resolution.ToString(), "isEchoed", r.IsEchoed, "linkModulePath", r.LinkModulePath, "linkModuleResolution", r.LinkModuleResolution.ToString());
                Endpoint(row, "source", r.Source); Endpoint(row, "target", r.Target); yield return row;
            }
            foreach (var finding in graph.Findings) yield return Row("Finding", finding.Id, "nodeId", finding.NodeId, "edgeId", finding.EdgeId, "ruleId", finding.RuleId, "scopeId", finding.ScopeId,
                "code", finding.Code, "state", finding.State.ToString(), "severity", finding.Severity.ToString(), "message", finding.Message, "observedCount", finding.ObservedCount);
            foreach (var cycle in graph.Cycles)
            {
                yield return Row("Cycle", cycle.Id, "ruleId", cycle.RuleId, "behavior", cycle.Behavior.ToString(), "nodeCount", cycle.NodeIds.Count, "edgeCount", cycle.EdgeIds.Count);
                foreach (string node in cycle.NodeIds) yield return Row("CycleNode", cycle.Id + ":" + node, "cycleId", cycle.Id, "nodeId", node);
                foreach (string edge in cycle.EdgeIds) yield return Row("CycleEdge", cycle.Id + ":" + edge, "cycleId", cycle.Id, "edgeId", edge);
            }
            if (impact != null)
            {
                yield return Row("Impact", impact.GraphId.ToString("N"), "direction", impact.Direction.ToString(), "maxDepth", impact.MaxDepth, "includesPossible", impact.IncludesPossibleEdges,
                    "truncated", impact.IsTruncated, "graphScopeComplete", impact.GraphScopeComplete, "conclusiveWithinRequestedScope", impact.IsConclusiveWithinRequestedScope);
                foreach (var visit in impact.Visits) yield return Row("ImpactVisit", visit.NodeId, "nodeId", visit.NodeId, "distance", visit.Distance, "predecessorId", visit.PredecessorId, "edgeId", visit.EdgeId, "confirmedPath", visit.IsConfirmedPath);
                foreach (string node in impact.BoundaryNodeIds) yield return Row("ImpactBoundary", node, "nodeId", node);
                foreach (string edge in impact.TraversedEdgeIds) yield return Row("ImpactEdge", edge, "edgeId", edge);
            }
        }
        private static Dictionary<string, object> Node(TraceabilityNode n) => Row("Node", n.Id, "repositoryId", n.RepositoryId, "modulePath", n.ModulePath, "nativeModuleId", n.QualifiedModuleId,
            "identityEvidence", n.IdentityEvidence, "objectIdentityEvidence", n.ObjectIdentityEvidence, "versionKind", n.VersionKind.ToString(), "version", n.Version, "selectedVersionBinding", n.HasSelectedVersionBinding, "absoluteNumber", n.AbsoluteNumber,
            "objectIdentifier", n.ObjectIdentifier, "role", n.Role, "moduleScopeId", n.ScopeId, "inventoryObject", n.IsInventoryObject, "deleted", n.IsDeleted, "accessStatuses", n.AccessStatuses.Select(s => s.ToString()));
        private static Dictionary<string, object> Edge(TraceabilityEdge e) => Row("Edge", e.Id, "sourceId", e.SourceId, "targetId", e.TargetId, "linkModulePath", e.LinkModulePath,
            "nativeLinkModuleId", e.QualifiedLinkModuleId, "linkModuleIdentityEvidence", e.LinkModuleIdentityEvidence, "state", e.State.ToString(), "recordKind", e.Kind.ToString(), "isEchoed", e.IsEchoed,
            "observationCount", e.ObservationIds.Count, "observedDirections", e.ObservedDirections.Select(d => d.ToString()));
        private static void Endpoint(Dictionary<string, object> row, string prefix, LinkEndpoint endpoint)
        { row.Add(prefix + "ModulePath", endpoint.ModulePath); row.Add(prefix + "VersionKind", endpoint.VersionKind.ToString()); row.Add(prefix + "RawVersion", endpoint.Version); row.Add(prefix + "AbsoluteNumber", endpoint.AbsoluteNumber);
            row.Add(prefix + "Identifier", endpoint.ObjectIdentifier); row.Add(prefix + "Resolution", endpoint.Resolution.ToString()); row.Add(prefix + "SurrogateStatus", endpoint.SurrogateStatus.ToString()); row.Add(prefix + "ExternalReference", endpoint.ExternalReference); row.Add(prefix + "ReferenceStatus", endpoint.ReferenceStatus.ToString()); }
        private static void WriteGraphMl(TextWriter destination, TraceabilityGraph graph, TraceabilityImpactResult impact, CancellationToken token)
        {
            const string ns = "http://graphml.graphdrawing.org/xmlns";
            using (var writer = XmlWriter.Create(destination, new XmlWriterSettings { Indent = true, CloseOutput = false, CheckCharacters = true }))
            {
                writer.WriteStartDocument(); writer.WriteStartElement("graphml", ns);
                foreach (string key in new[] { "metadata", "auxiliary" }) { writer.WriteStartElement("key", ns); writer.WriteAttributeString("id", key); writer.WriteAttributeString("for", key == "metadata" ? "all" : "graph"); writer.WriteAttributeString("attr.name", key == "metadata" ? "metadataJson" : "evidenceJsonLines"); writer.WriteAttributeString("attr.type", "string"); writer.WriteEndElement(); }
                writer.WriteStartElement("graph", ns); writer.WriteAttributeString("id", "g" + graph.GraphId.ToString("N")); writer.WriteAttributeString("edgedefault", "directed");
                writer.WriteStartElement("data", ns); writer.WriteAttributeString("key", "auxiliary");
                foreach (var record in Records(graph, impact, false)) { token.ThrowIfCancellationRequested(); writer.WriteString(Json(record)); writer.WriteString("\n"); } writer.WriteEndElement();
                foreach (var node in graph.Nodes.Values.OrderBy(n => n.Id, StringComparer.Ordinal)) { token.ThrowIfCancellationRequested(); writer.WriteStartElement("node", ns); writer.WriteAttributeString("id", node.Id); Data(writer, ns, Node(node)); writer.WriteEndElement(); }
                foreach (var edge in graph.Edges) { token.ThrowIfCancellationRequested(); writer.WriteStartElement("edge", ns); writer.WriteAttributeString("id", edge.Id); writer.WriteAttributeString("source", edge.SourceId); writer.WriteAttributeString("target", edge.TargetId); Data(writer, ns, Edge(edge)); writer.WriteEndElement(); }
                writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndDocument();
            }
        }
        private static void Data(XmlWriter writer, string ns, Dictionary<string, object> row) { writer.WriteStartElement("data", ns); writer.WriteAttributeString("key", "metadata"); writer.WriteString(Json(row)); writer.WriteEndElement(); }
        private static string Get(Dictionary<string, object> row, string key) => row.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : "";
        private static string Json(Dictionary<string, object> row) { using (var text = new StringWriter(CultureInfo.InvariantCulture)) { JsonObject(text, row); return text.ToString(); } }
        private static void JsonObject(TextWriter writer, Dictionary<string, object> row)
        { writer.Write('{'); bool first = true; foreach (var field in row) { if (!first) writer.Write(','); first = false; String(writer, field.Key); writer.Write(':'); Value(writer, field.Value); } writer.Write('}'); }
        private static void Value(TextWriter writer, object value)
        {
            if (value == null) writer.Write("null"); else if (value is bool b) writer.Write(b ? "true" : "false"); else if (value is string s) String(writer, s);
            else if (value is IEnumerable<string> values) { writer.Write('['); bool first = true; foreach (string item in values) { if (!first) writer.Write(','); first = false; String(writer, item); } writer.Write(']'); }
            else writer.Write(Convert.ToString(value, CultureInfo.InvariantCulture));
        }
        private static void String(TextWriter writer, string value)
        { writer.Write('"'); foreach (char c in value) { if (c == '\\' || c == '"') { writer.Write('\\'); writer.Write(c); } else if (c < 32 || c > 126) { writer.Write("\\u"); writer.Write(((int)c).ToString("x4", CultureInfo.InvariantCulture)); } else writer.Write(c); } writer.Write('"'); }
        private static void CsvRow(TextWriter writer, IEnumerable<string> cells)
        {
            bool first = true; foreach (string cell in cells) { if (!first) writer.Write(','); first = false; string safe = cell ?? ""; if (CsvExportWriter.Dangerous(safe)) safe = "\t" + safe;
                writer.Write('"'); foreach (char c in safe) { if (c == '"') writer.Write('"'); writer.Write(c); } writer.Write('"'); } writer.Write("\r\n");
        }
    }
}
