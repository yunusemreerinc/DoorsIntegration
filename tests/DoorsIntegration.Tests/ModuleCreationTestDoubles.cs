using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DoorsIntegration.Core;
using DoorsIntegration.Dxl;

namespace DoorsIntegration.Tests
{
    internal sealed class CreationConfirmation : IModuleCreationConfirmation
    {
        public bool Approved;
        public int Calls;
        public Action OnConfirm;
        public Task<bool> ConfirmAsync(FormalModuleCreationPreview preview, CancellationToken cancellationToken)
        {
            Calls++;
            OnConfirm?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Approved);
        }
    }

    internal sealed class ModuleCreationTestContext : IDisposable
    {
        public readonly ExecutorTestContext Transport = new ExecutorTestContext();
        public readonly CreationConfirmation Confirmation = new CreationConfirmation();
        public readonly List<string> Modes = new List<string>();
        public readonly FormalModuleCreateRequest Request = new FormalModuleCreateRequest("/Test", "NewModule", "Test description");
        public FormalModuleCreationChecks Checks = Ready();
        public FormalModuleCreationChecks CommitChecks;
        public ModuleCreationCleanup Cleanup = ModuleCreationCleanup.Closed;
        public ModuleCreationOutcome? ForcedCommitOutcome;
        public Func<string, string> AlterResponse;
        public DxlModuleCreationGateway Gateway { get; }
        public ModuleService Service { get; }

        public ModuleCreationTestContext()
        {
            File.Copy(new ModuleCreationScriptOptions().ScriptPath, Transport.SourcePath, true);
            Gateway = new DxlModuleCreationGateway(Transport.Executor, new ModuleCreationScriptOptions(Transport.SourcePath), Transport.Log);
            Service = new ModuleService(new RecordingHierarchyReader(), Gateway, Confirmation);
            Transport.Sessions.Session.OnRun = session =>
            {
                string mode = DiscoveryTestContext.Parameter(session.Script, "mode");
                Modes.Add(mode);
                string folder = DiscoveryTestContext.Parameter(session.Script, "folder");
                string name = DiscoveryTestContext.Parameter(session.Script, "name");
                string path = (folder == "/" ? "/" : folder + "/") + name;
                var checks = mode == "commit" ? CommitChecks ?? Checks : Checks;
                var outcome = ModuleCreationOutcome.Preview;
                if (mode == "commit")
                {
                    bool sameContext = checks.DatabaseId == DiscoveryTestContext.Parameter(session.Script, "expectedDatabase") &&
                        checks.UserName == DiscoveryTestContext.Parameter(session.Script, "expectedUser") &&
                        checks.FolderId == DiscoveryTestContext.Parameter(session.Script, "expectedFolderId");
                    outcome = ForcedCommitOutcome ?? (!sameContext ? ModuleCreationOutcome.ContextChanged :
                        checks.CanCreate ? ModuleCreationOutcome.Created : ModuleCreationOutcome.Blocked);
                }
                var id = Guid.ParseExact(session.Prefix.Split('|')[2], "N");
                session.Payload = Batch(id, path, checks, outcome, Cleanup);
                if (AlterResponse != null) session.Payload = AlterResponse(session.Payload);
            };
        }

        public static FormalModuleCreationChecks Ready(CreationFolderStatus folder = CreationFolderStatus.Readable,
            CreationPermissionStatus permission = CreationPermissionStatus.Allowed, CreationInputStatus name = CreationInputStatus.Valid,
            CreationInputStatus description = CreationInputStatus.Valid, CreationNameStatus collision = CreationNameStatus.NoVisibleConflict,
            CreationLockStatus locks = CreationLockStatus.NoBlockingLockObserved, string databaseId = "database-1", string user = "test-user", string folderId = "folder-1") =>
            new FormalModuleCreationChecks(databaseId, user, folderId, folder, permission, name, description, collision, locks);

        public static string Batch(Guid id, string path, FormalModuleCreationChecks checks, ModuleCreationOutcome outcome,
            ModuleCreationCleanup cleanup = ModuleCreationCleanup.Closed)
        {
            bool created = outcome == ModuleCreationOutcome.Created;
            return "DOORS_MODULE_CREATE|1|" + id.ToString("N") + "\nP\t" + Escape(path) + "\t" + Escape(checks.DatabaseId) + "\t" +
                Escape(checks.UserName) + "\t" + Escape(checks.FolderId) + "\t" + checks.Folder + "\t" + checks.Permission + "\t" +
                checks.Name + "\t" + checks.Description + "\t" + checks.Collision + "\t" + checks.Locks + "\nR\t" + outcome + "\t" +
                (created ? "module-123\tdatabase-1:module-123\t" + cleanup : "\t\tNotRequired") + "\nEND\n";
        }
        private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
        public void Dispose() => Transport.Dispose();
    }
}
