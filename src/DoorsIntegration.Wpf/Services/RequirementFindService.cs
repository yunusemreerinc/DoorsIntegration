using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using DoorsIntegration.Core;

namespace DoorsIntegration.Wpf.Services
{
    public enum RequirementFindDirection { Next, Previous }
    public sealed class RequirementFindOptions
    {
        public RequirementFindOptions(string text, bool matchCase = false, bool wholeWord = false, bool wrap = true)
        { if(string.IsNullOrEmpty(text)||text.Length>256) throw new ArgumentException("Find text must contain 1–256 characters."); Text=text; MatchCase=matchCase; WholeWord=wholeWord; Wrap=wrap; }
        public string Text { get; }
        public bool MatchCase { get; }
        public bool WholeWord { get; }
        public bool Wrap { get; }
    }
    public sealed class RequirementFindHit
    {
        public RequirementRecord Record { get; internal set; }
        public string Field { get; internal set; }
        public int ViewIndex { get; internal set; }
        public int Ordinal { get; internal set; }
    }
    public sealed class RequirementFindResult
    {
        public RequirementFindHit Hit { get; internal set; }
        public int Matches { get; internal set; }
        public bool Wrapped { get; internal set; }
    }
    // Literal, read-only search over a retained snapshot. Only a few candidate records are held;
    // the full object inventory is never materialized and no COM/DXL call is made.
    public sealed class RequirementFindService
    {
        public RequirementFindResult Find(IEnumerable<RequirementRecord> records, RequirementFindOptions options,
            RequirementFindDirection direction, int? anchor, bool includeAnchor, Func<RequirementRecord,bool> visible,
            CancellationToken token, IProgress<string> progress = null)
        {
            if(records==null||options==null||visible==null||!Enum.IsDefined(typeof(RequirementFindDirection),direction)) throw new ArgumentException("Invalid Find request.");
            RequirementFindHit first=null,last=null,before=null,after=null,at=null;
            bool anchorSeen=false; int index=0,matches=0,scanned=0;
            foreach(var record in records)
            {
                token.ThrowIfCancellationRequested(); if(++scanned%1000==0) progress?.Report("Find: "+scanned+" nesne tarandı");
                if(!visible(record)) continue;
                bool isAnchor=record.AbsoluteNumber==anchor; if(isAnchor) anchorSeen=true;
                string field=MatchedField(record,options,token);
                if(field!=null)
                {
                    var hit=new RequirementFindHit { Record=record,Field=field,ViewIndex=index,Ordinal=++matches };
                    if(first==null) first=hit; last=hit;
                    if(isAnchor) at=hit; else if(!anchorSeen) before=hit; else if(after==null) after=hit;
                }
                index++;
            }
            token.ThrowIfCancellationRequested();
            var result=new RequirementFindResult { Matches=matches };
            if(!anchorSeen) result.Hit=direction==RequirementFindDirection.Next?first:last;
            else if(includeAnchor&&at!=null) result.Hit=at;
            else result.Hit=direction==RequirementFindDirection.Next?after:before;
            if(result.Hit==null&&matches>0&&anchorSeen&&options.Wrap) { result.Hit=direction==RequirementFindDirection.Next?first:last; result.Wrapped=true; }
            return result;
        }
        public static string MatchedField(RequirementRecord record,RequirementFindOptions options,CancellationToken token)
        {
            if(Matches(record.ObjectIdentifier,options)) return "Object Identifier";
            if(record.NumberStatus==RequirementValueStatus.Readable&&Matches(record.ObjectNumber,options)) return "Object Number";
            foreach(var value in record.Values.Values) { token.ThrowIfCancellationRequested(); if(value.PlainStatus==RequirementValueStatus.Readable&&Matches(value.PlainText,options)) return value.Name; }
            return null;
        }
        public static bool Matches(string text,RequirementFindOptions options)
        {
            if(text==null) return false;
            var comparison=options.MatchCase?StringComparison.Ordinal:StringComparison.OrdinalIgnoreCase;
            int start=0;
            while(start<=text.Length-options.Text.Length)
            {
                int found=text.IndexOf(options.Text,start,comparison); if(found<0) return false;
                if(!options.WholeWord||!IsWord(text,found-1)&&!IsWord(text,found+options.Text.Length)) return true;
                start=found+1;
            }
            return false;
        }
        private static bool IsWord(string text,int index)
        {
            if(index<0||index>=text.Length) return false;
            if(char.IsLowSurrogate(text[index])&&index>0&&char.IsHighSurrogate(text[index-1])) index--;
            var category=CharUnicodeInfo.GetUnicodeCategory(text,index);
            return char.IsLetterOrDigit(text,index)||category==UnicodeCategory.NonSpacingMark||category==UnicodeCategory.SpacingCombiningMark||category==UnicodeCategory.EnclosingMark||category==UnicodeCategory.ConnectorPunctuation||category==UnicodeCategory.LetterNumber||category==UnicodeCategory.OtherNumber;
        }
    }
}
