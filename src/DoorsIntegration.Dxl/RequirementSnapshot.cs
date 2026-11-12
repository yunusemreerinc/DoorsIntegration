using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    internal sealed class RequirementSnapshot : IRequirementSnapshot
    {
        private readonly DxlFileLease lease;
        public RequirementSnapshot(DxlExecutionResult result, RequirementReadSummary summary) { lease = new DxlFileLease(result); Summary = summary; }
        public RequirementReadSummary Summary { get; }
        public IEnumerable<RequirementRecord> ReadObjects()
        {
            using (var stream = OpenRawStream())
            {
                using (var records = RequirementProtocol.Parse(stream, Summary.Request, Summary.CorrelationId, CancellationToken.None, ignored => { }).GetEnumerator())
                {
                    while (true)
                    {
                        bool next;
                        try { next = records.MoveNext(); }
                        catch (System.Text.DecoderFallbackException ex) { throw RequirementProtocol.Failure(ex); }
                        catch (IOException ex) { throw DxlWorkspaceFactory.FileFailure(ex); }
                        if (!next) yield break;
                        yield return records.Current;
                    }
                }
            }
        }
        public Stream OpenRawStream() => lease.Open();
        public void Dispose() => lease.Dispose();
    }
}
