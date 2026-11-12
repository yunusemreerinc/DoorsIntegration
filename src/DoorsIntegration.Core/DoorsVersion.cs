using System;

namespace DoorsIntegration.Core
{
    public enum DoorsVersion
    {
        V95 = 95,
        V97 = 97
    }

    public static class DoorsVersions
    {
        public static DoorsVersion Parse(string value)
        {
            switch (value)
            {
                case "9.5": return DoorsVersion.V95;
                case "9.7": return DoorsVersion.V97;
                default:
                    throw new IntegrationException(IntegrationErrorCode.InvalidConfiguration,
                        "Desteklenen DOORS hedefleri: 9.5 ve 9.7.");
            }
        }

        public static string Format(DoorsVersion value)
        {
            switch (value)
            {
                case DoorsVersion.V95: return "9.5";
                case DoorsVersion.V97: return "9.7";
                default:
                    throw new IntegrationException(IntegrationErrorCode.InvalidConfiguration,
                        "Desteklenmeyen DOORS hedef profili.");
            }
        }
    }
}
