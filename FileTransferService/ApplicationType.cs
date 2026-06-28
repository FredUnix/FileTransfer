using System;
using System.Collections.Generic;

namespace FileTransfer
{
    /// <summary>
    /// Allowed values of the metadata <c>applicationType</c> field. It is used to
    /// route the file to the correct service. Managed by the IOT team; the current
    /// set is QCS, DFE and JRM.
    /// </summary>
    public static class ApplicationType
    {
        public const string Qcs = "QCS";
        public const string Dfe = "DFE";
        public const string Jrm = "JRM";

        /// <summary>The accepted application types (case-insensitive).</summary>
        public static readonly IReadOnlySet<string> Allowed =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Qcs, Dfe, Jrm };

        public static bool IsValid(string? value) =>
            !string.IsNullOrEmpty(value) && Allowed.Contains(value);
    }
}
