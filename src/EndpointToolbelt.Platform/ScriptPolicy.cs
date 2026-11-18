using System;
using System.Collections.Generic;
using System.Linq;

namespace EndpointToolbelt.Platform;

/// <summary>How a script-policy refusal by Windows PowerShell shows on its standard error.</summary>
public static class ScriptPolicy
{
    /// <summary>
    /// Standard-error markers of a script-policy refusal: an execution policy that blocks the
    /// files, or ConstrainedLanguage mode (App Control for Business or AppLocker script
    /// enforcement), in which the engine fails before it can report this itself. Error IDs are
    /// not translated; the English texts cover the messages on English systems.
    /// </summary>
    private static readonly string[] PolicyFailureMarkers =
    {
        "PSSecurityException",
        "FullyQualifiedErrorId : UnauthorizedAccess",
        "is not digitally signed",
        "running scripts is disabled on this system",
        "MethodInvocationNotSupportedInConstrainedLanguage",
        "CannotCreateTypeConstrainedLanguage",
        "ConversionSupportedOnlyToCoreTypes",
        "PropertySetterNotSupportedInConstrainedLanguage",
        "Method invocation is supported only on core types in this language mode",
        "Only core types are supported in this language mode",
        "Cannot set property. Property setting is supported only on core types in this language mode",
    };

    /// <summary>True when standard error shows that PowerShell refused a script for policy reasons.</summary>
    public static bool IsPolicyFailure(IEnumerable<string> errorLines) =>
        errorLines.Any(l => l != null && PolicyFailureMarkers.Any(m => l.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0));
}
