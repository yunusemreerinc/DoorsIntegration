using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using DoorsIntegration.Core;

namespace DoorsIntegration.Wpf.Services
{
    public enum RequirementGoToKind { AbsoluteNumber, ObjectIdentifier, ObjectNumber }
    public enum RequirementGoToStatus { Found, HiddenByFilter, NotFound, Ambiguous }
    public sealed class RequirementGoToRequest
    {
        public RequirementGoToRequest(RequirementGoToKind kind,string value)
        {
            if(!Validate(kind,value,out var reason)) throw new ArgumentException(reason);
            Kind=kind; Value=value.Trim();
            if(kind==RequirementGoToKind.AbsoluteNumber) AbsoluteNumber=int.Parse(Value,NumberStyles.None,CultureInfo.InvariantCulture);
        }
        public RequirementGoToKind Kind { get; }
        public string Value { get; }
        public int? AbsoluteNumber { get; }
        public static bool Validate(RequirementGoToKind kind,string value,out string reason)
        {
            reason="";
            if(!Enum.IsDefined(typeof(RequirementGoToKind),kind)) { reason="Geçerli bir hedef türü seçin."; return false; }
            if(string.IsNullOrWhiteSpace(value)||value.Length>256) { reason="1–256 karakterlik bir hedef girin."; return false; }
            foreach(char c in value) if(char.IsControl(c)) { reason="Hedef kontrol karakteri içeremez."; return false; }
            if(kind==RequirementGoToKind.AbsoluteNumber&&(!int.TryParse(value.Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out int number)||number<1)) { reason="Absolute Number için pozitif bir tam sayı girin."; return false; }
            return true;
        }
    }
    public sealed class RequirementGoToResult
    {
        public RequirementGoToStatus Status { get; internal set; }
        public RequirementRecord Record { get; internal set; }
        public int ViewIndex { get; internal set; }
        public int MatchingObjects { get; internal set; }
        public bool NumberingUnavailable { get; internal set; }
    }
    // Lookup is local to the retained module/version snapshot. Display identifiers/numbers
    // are only lookup values: the selected row is bound to its actual Absolute Number.
    public sealed class RequirementGoToService
    {
        public RequirementGoToResult Locate(IEnumerable<RequirementRecord> records,RequirementGoToRequest request,
            Func<RequirementRecord,bool> visible,CancellationToken token,IProgress<string> progress=null)
        {
            if(records==null||request==null||visible==null) throw new ArgumentNullException();
            var result=new RequirementGoToResult { Status=RequirementGoToStatus.NotFound,ViewIndex=-1 };
            int index=0,scanned=0;
            foreach(var record in records)
            {
                token.ThrowIfCancellationRequested(); if(++scanned%1000==0) progress?.Report("Go To: "+scanned+" nesne tarandı");
                bool inView=visible(record);
                if(request.Kind==RequirementGoToKind.ObjectNumber&&record.NumberStatus!=RequirementValueStatus.Readable) result.NumberingUnavailable=true;
                bool matches=request.Kind==RequirementGoToKind.AbsoluteNumber?record.AbsoluteNumber==request.AbsoluteNumber:
                    request.Kind==RequirementGoToKind.ObjectIdentifier?string.Equals(record.ObjectIdentifier,request.Value,StringComparison.OrdinalIgnoreCase):
                    record.NumberStatus==RequirementValueStatus.Readable&&string.Equals(record.ObjectNumber,request.Value,StringComparison.Ordinal);
                if(matches)
                {
                    result.MatchingObjects++;
                    if(result.MatchingObjects==1) { result.Record=record; result.ViewIndex=inView?index:-1; result.Status=inView?RequirementGoToStatus.Found:RequirementGoToStatus.HiddenByFilter; }
                    else { result.Record=null; result.ViewIndex=-1; result.Status=RequirementGoToStatus.Ambiguous; }
                }
                if(inView) index++;
            }
            token.ThrowIfCancellationRequested(); return result;
        }
    }
}
