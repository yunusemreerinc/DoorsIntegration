using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace DoorsIntegration.Core
{
    [DataContract]
    public sealed class BaselineVerificationPolicyRule
    {
        [OnDeserializing]
        private void Defaults(StreamingContext context) { Direction = "Outgoing"; MinimumLinks = 1; VersionPolicy = "SameVersion"; }
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "anchorType")] public string AnchorType { get; set; }
        [DataMember(Name = "counterpartTypes")] public string[] CounterpartTypes { get; set; }
        [DataMember(Name = "linkModulePath")] public string LinkModulePath { get; set; }
        [DataMember(Name = "direction")] public string Direction { get; set; } = "Outgoing";
        [DataMember(Name = "minimumLinks")] public int MinimumLinks { get; set; } = 1;
        [DataMember(Name = "maximumLinks")] public int? MaximumLinks { get; set; }
        [DataMember(Name = "versionPolicy")] public string VersionPolicy { get; set; } = "SameVersion";
        [DataMember(Name = "nativeLinkModuleId")] public string NativeLinkModuleId { get; set; }
    }
    [DataContract]
    public sealed class BaselineVerificationPolicyProfile
    {
        [DataMember(Name = "projectId")] public string ProjectId { get; set; }
        [DataMember(Name = "policyId")] public string PolicyId { get; set; }
        [DataMember(Name = "rules")] public List<BaselineVerificationPolicyRule> Rules { get; set; } = new List<BaselineVerificationPolicyRule>();
        [DataMember(Name = "moduleRoles")] public Dictionary<string, string> ModuleRoles { get; set; } = new Dictionary<string, string>();
        [DataMember(Name = "cycleBehavior")] public string CycleBehavior { get; set; } = "Report";
        [DataMember(Name = "typeAttribute")] public string TypeAttribute { get; set; } = "";
        [DataMember(Name = "statusAttribute")] public string StatusAttribute { get; set; } = "";
        public TraceabilityPolicy ToPolicy()
        {
            GraphGuard.Text(ProjectId, 256); TypeAttribute = TypeAttribute ?? ""; StatusAttribute = StatusAttribute ?? ""; ModuleRoles = ModuleRoles ?? new Dictionary<string, string>();
            return new TraceabilityPolicy(PolicyId, (Rules ?? new List<BaselineVerificationPolicyRule>()).Select(r => new TraceabilityLinkRule(r.Id, r.AnchorType, r.CounterpartTypes, r.LinkModulePath,
                Parse<TraceabilityDirection>(r.Direction ?? "Outgoing"), r.MinimumLinks, r.MaximumLinks, Parse<TraceabilityVersionPolicy>(r.VersionPolicy ?? "SameVersion"), r.NativeLinkModuleId)),
                new[] { new TraceabilityCycleRule(behavior: Parse<TraceabilityCycleBehavior>(CycleBehavior ?? "Report")) }, Array.Empty<string>());
        }
        private static T Parse<T>(string text) where T : struct => Enum.TryParse(text, false, out T value) && Enum.IsDefined(typeof(T), value) ? value : throw new ArgumentException("Invalid policy enum.");
        public static BaselineVerificationPolicyProfile ReadJson(string json)
        {
            if (json == null || json.Length > 131072) throw new ArgumentException("Policy JSON must be bounded.");
            using (var input = new MemoryStream(new UTF8Encoding(false, true).GetBytes(json)))
            { var profile = (BaselineVerificationPolicyProfile)new DataContractJsonSerializer(typeof(BaselineVerificationPolicyProfile), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true, MaxItemsInObjectGraph = 20000 }).ReadObject(input); profile.ToPolicy(); return profile; }
        }
        public static string MockExampleJson => "{\"projectId\":\"Mock project\",\"policyId\":\"SYS-HLR-LLR-TEST\",\"cycleBehavior\":\"Report\",\"moduleRoles\":{\"EXT\":\"HLR\"},\"rules\":[" +
            string.Join(",", new[] { new[] { "SYS", "HLR" }, new[] { "HLR", "LLR" }, new[] { "LLR", "TEST" } }.Select(pair => "{\"id\":\"" + pair[0] + "-" + pair[1] + "\",\"anchorType\":\"" + pair[0] + "\",\"counterpartTypes\":[\"" + pair[1] + "\"],\"linkModulePath\":\"/Traceability/Standard links\",\"direction\":\"Outgoing\",\"minimumLinks\":1,\"versionPolicy\":\"SameVersion\"}")) + "]}";
    }
}
