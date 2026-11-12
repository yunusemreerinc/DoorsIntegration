using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DoorsIntegration.Core
{
    [DataContract]
    public sealed class NativeModuleReadState
    {
        [DataMember] public string FullPath { get; set; }
        [DataMember] public string ModuleIdentity { get; set; }
        [DataMember] public DoorsItemKind Kind { get; set; }
        [DataMember] public BaselineCheckState Exists { get; set; }
        [DataMember] public BaselineCheckState CanRead { get; set; }
        [DataMember] public BaselineCheckState CanCreate { get; set; }
        [DataMember] public BaselineCheckState CanModify { get; set; }
        [DataMember] public BaselineEditState EditState { get; set; }
        [DataMember] public BaselineLockState LockState { get; set; }
        [DataMember] public BaselineUnsavedState Unsaved { get; set; }
        [DataMember] public BaselineShareableConfiguration Shareable { get; set; }
        [DataMember] public string Reason { get; set; }
    }
    public interface IImpactReadContextProvider
    { Task<BaselineVerificationContext> ObserveAsync(ImpactSnapshotSelection selection, CancellationToken token); }
}
