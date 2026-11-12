using System;
using System.Text;
using DoorsIntegration.Core;

namespace DoorsIntegration.Dxl
{
    internal static class ModuleCreationProtocol
    {
        public static FormalModuleCreationResult Parse(string text, Guid id, FormalModuleCreateRequest request, FormalModuleCreationPreview approved)
        {
            try
            {
                if (string.IsNullOrEmpty(text) || text.Length > 32768 || !text.EndsWith("\n", StringComparison.Ordinal)) throw Failure();
                var lines = text.Split('\n');
                if (lines.Length != 5) throw Failure();
                for (int i = 0; i < lines.Length - 1; i++)
                {
                    if (lines[i].EndsWith("\r", StringComparison.Ordinal)) lines[i] = lines[i].Substring(0, lines[i].Length - 1);
                    if (lines[i].IndexOf('\r') >= 0) throw Failure();
                }
                if (lines[0] != "DOORS_MODULE_CREATE|1|" + id.ToString("N") || lines[3] != "END") throw Failure();
                var p = lines[1].Split('\t');
                var r = lines[2].Split('\t');
                if (p.Length != 11 || p[0] != "P" || r.Length != 5 || r[0] != "R" || Decode(p[1]) != request.FullPath) throw Failure();
                var checks = new FormalModuleCreationChecks(Identity(p[2]), Identity(p[3]), Identity(p[4]),
                    ReadEnum<CreationFolderStatus>(p[5]), ReadEnum<CreationPermissionStatus>(p[6]), ReadEnum<CreationInputStatus>(p[7]),
                    ReadEnum<CreationInputStatus>(p[8]), ReadEnum<CreationNameStatus>(p[9]), ReadEnum<CreationLockStatus>(p[10]));
                var outcome = ReadEnum<ModuleCreationOutcome>(r[1]);
                string moduleId = Identity(r[2]), qualifiedId = Identity(r[3]);
                var cleanup = ReadEnum<ModuleCreationCleanup>(r[4]);
                bool identitiesPresent = moduleId.Length != 0 && qualifiedId.Length != 0;
                if ((moduleId.Length == 0) != (qualifiedId.Length == 0)) throw Failure();
                if (approved == null)
                {
                    if (outcome != ModuleCreationOutcome.Preview || identitiesPresent || cleanup != ModuleCreationCleanup.NotRequired) throw Failure();
                }
                else
                {
                    if (outcome == ModuleCreationOutcome.Preview) throw Failure();
                    if (outcome == ModuleCreationOutcome.Created && (!checks.CanCreate || !identitiesPresent || cleanup == ModuleCreationCleanup.NotRequired || !SameContext(checks, approved.Checks))) throw Failure();
                    if (outcome == ModuleCreationOutcome.Blocked && checks.CanCreate) throw Failure();
                    if (outcome != ModuleCreationOutcome.Created && outcome != ModuleCreationOutcome.OutcomeUnknown && (identitiesPresent || cleanup != ModuleCreationCleanup.NotRequired)) throw Failure();
                }
                return new FormalModuleCreationResult(id, request.FullPath, checks, outcome, moduleId, qualifiedId, cleanup);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IntegrationException)
            {
                if (ex is IntegrationException known && known.Code == IntegrationErrorCode.ModuleCreationProtocolFailure) throw;
                // No raw external field values are retained as exception messages/inner exceptions.
                throw Failure();
            }
        }

        private static bool SameContext(FormalModuleCreationChecks left, FormalModuleCreationChecks right) =>
            left.DatabaseId == right.DatabaseId && left.UserName == right.UserName && left.FolderId == right.FolderId;
        private static T ReadEnum<T>(string value) where T : struct
        {
            T result;
            if (!Enum.TryParse(value, false, out result) || !Enum.IsDefined(typeof(T), result) || result.ToString() != value) throw Failure();
            return result;
        }
        private static string Identity(string value)
        {
            string decoded = Decode(value);
            if (decoded.Length > 1024) throw Failure();
            foreach (char c in decoded) if (char.IsControl(c)) throw Failure();
            return decoded;
        }
        private static string Decode(string value)
        {
            var result = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsControl(c)) throw Failure();
                if (c == '\\')
                {
                    if (++i >= value.Length) throw Failure();
                    switch (value[i])
                    {
                        case '\\': c = '\\'; break;
                        case 't': c = '\t'; break;
                        case 'r': c = '\r'; break;
                        case 'n': c = '\n'; break;
                        default: throw Failure();
                    }
                }
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])) throw Failure();
                    result.Append(c).Append(value[++i]);
                }
                else if (char.IsLowSurrogate(c)) throw Failure();
                else result.Append(c);
            }
            return result.ToString();
        }
        public static IntegrationException Failure() => new IntegrationException(IntegrationErrorCode.ModuleCreationProtocolFailure,
            "Modül oluşturma yanıtı eksik, tutarsız veya başka isteğe ait; başarı kabul edilmedi.");
    }
}
