using System;
using System.Collections.Generic;
using System.Linq;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;
using DoorsIntegration.Processing;

namespace DoorsIntegration.Desktop
{
    public sealed class DesktopWorkspaceServices
    {
        public DesktopWorkspaceServices(IRequirementService requirements, IModuleService modules, ILinkService links,
            IExportService export, ITraceabilityExporter traceability, IDxlExecutor dxl, IDxlScriptLoader scripts,
            IDoorsHealthChecks health, IStaOperationRunner sta, IntegrationErrorHandler errors, DesktopWriteConfirmation confirmation,
            DoorsBatchServiceFactory batches, QualityEngine quality, TraceabilityCoverageService coverage, ProcessingStorageOptions processingStorage)
        {
            Requirements = requirements; Modules = modules; Links = links; Export = export; Traceability = traceability;
            Dxl = dxl; Scripts = scripts; Health = health; Sta = sta; Errors = errors; Confirmation = confirmation;
            Batches = batches; Quality = quality; Coverage = coverage; ProcessingStorage = processingStorage;
        }
        public IRequirementService Requirements { get; }
        public IModuleService Modules { get; }
        public ILinkService Links { get; }
        public IExportService Export { get; }
        public ITraceabilityExporter Traceability { get; }
        public IDxlExecutor Dxl { get; }
        public IDxlScriptLoader Scripts { get; }
        public IDoorsHealthChecks Health { get; }
        public IStaOperationRunner Sta { get; }
        public IntegrationErrorHandler Errors { get; }
        public DesktopWriteConfirmation Confirmation { get; }
        public DoorsBatchServiceFactory Batches { get; }
        public QualityEngine Quality { get; }
        public TraceabilityCoverageService Coverage { get; }
        public ProcessingStorageOptions ProcessingStorage { get; }
    }

    // Snapshots keep their private backing files alive through paging and export. One UI operation owns them at a time.
    public sealed class DesktopSnapshotStore : IDisposable
    {
        public IRequirementSnapshot Requirements { get; private set; }
        public ILinkSnapshot Links { get; private set; }
        public event Action RequirementsChanged;
        public event Action LinksChanged;
        public void SetRequirements(IRequirementSnapshot value) { var old = Requirements; Requirements = value; old?.Dispose(); RequirementsChanged?.Invoke(); }
        public void SetLinks(ILinkSnapshot value) { var old = Links; Links = value; old?.Dispose(); LinksChanged?.Invoke(); }
        public void Dispose() { SetRequirements(null); SetLinks(null); }
    }

    public static class DesktopSnapshotPages
    {
        public const int PageSize = 100;
        public static IReadOnlyList<string[]> Requirements(IRequirementSnapshot snapshot, int page)
        {
            if (page < 0 || page > 10000) throw new ArgumentOutOfRangeException(nameof(page));
            return snapshot.ReadObjects().Skip(checked(page * PageSize)).Take(PageSize).Select(o => new[]
            { o.AbsoluteNumber.ToString(), o.ObjectIdentifier, o.ObjectNumber, o.ParentAbsoluteNumber?.ToString() ?? "",
                o.HierarchyLevel.ToString(), o.IsDeleted.ToString(), WorkspaceUi.Clip(o.Heading.PlainText), WorkspaceUi.Clip(o.Text.PlainText) }).ToArray();
        }
        public static IReadOnlyList<string[]> Links(ILinkSnapshot snapshot, int page)
        {
            if (page < 0 || page > 10000) throw new ArgumentOutOfRangeException(nameof(page));
            return snapshot.ReadLinks().Skip(checked(page * PageSize)).Take(PageSize).Select(l => new[]
            { l.Sequence.ToString(), l.Direction.ToString(), WorkspaceUi.Clip(l.Source.ModulePath), l.Source.AbsoluteNumber?.ToString() ?? "",
                WorkspaceUi.Clip(l.Target.ModulePath), l.Target.AbsoluteNumber?.ToString() ?? "", l.Resolution.ToString(),
                l.Source.SurrogateStatus + " / " + l.Target.SurrogateStatus }).ToArray();
        }
    }
}
