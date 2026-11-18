using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace EndpointToolbelt.App.Core;

/// <summary>
/// One unauthenticated HTTPS request of Check Microsoft connections: to one host, directly or
/// through one proxy, without cookies or credentials for the server, never following a redirect.
/// </summary>
public sealed class HttpsRequest
{
    public string Host { get; set; } = "";

    /// <summary>The path to ask for: "/" (a HEAD), or the sign-in service's OpenID document (a GET).</summary>
    public string Path { get; set; } = "/";

    /// <summary>GET reads at most <see cref="ReadLimit"/> bytes of the answer; HEAD reads none.</summary>
    public bool Get { get; set; }

    /// <summary>The proxy to go through, or null for a direct request.</summary>
    public Uri Proxy { get; set; }

    /// <summary>
    /// The user's proxy may get the user's Windows sign-in, as the user's apps give it; the
    /// machine proxy never does. The server never gets credentials.
    /// </summary>
    public bool ProxyCredentials { get; set; }

    public int TimeoutMs { get; set; }

    public const int ReadLimit = 64 * 1024;
}

/// <summary>
/// What one HTTPS request showed, as raw facts without judgment: a transport failure, the TLS
/// certificate checks, the HTTP status, the host a redirect points to, and (for a GET) the start of
/// the body. The real one is <see cref="SystemProxyRoutes.Https"/>; tests make their own.
/// </summary>
public sealed class HttpsObservation
{
    /// <summary>The request failed before an HTTP answer: time-out, name, refused, TLS or other; null when an answer came.</summary>
    public RouteOutcome? Failure { get; set; }

    /// <summary>A TLS handshake took place with whoever answered for the host (the certificate checks ran).</summary>
    public bool TlsSeen { get; set; }

    /// <summary>The certificate is not valid for the host's name.</summary>
    public bool NameMismatch { get; set; }

    /// <summary>The certificate chain does not end in a root this PC trusts, or does not validate.</summary>
    public bool ChainUntrusted { get; set; }

    /// <summary>The chain ends in a root that Microsoft's services use (see <see cref="MicrosoftChains"/>).</summary>
    public bool MicrosoftChain { get; set; }

    /// <summary>The organization of the chain's root (its O, else its CN); empty when there is no chain.</summary>
    public string IssuerOrganization { get; set; } = "";

    public int? Status { get; set; }

    /// <summary>The host of a redirect's Location, never the rest of the address; empty without one.</summary>
    public string LocationHost { get; set; } = "";

    public string ContentType { get; set; } = "";

    /// <summary>The start of the body of a GET (for the OpenID document only); never logged.</summary>
    public string Body { get; set; } = "";

    public static HttpsObservation Failed(RouteOutcome outcome) => new() { Failure = outcome };
}

/// <summary>
/// Which certificate chains and hosts belong to Microsoft. The roots are the ones Microsoft
/// lists for its service endpoints in "Azure Certificate Authority details" (learn.microsoft.com,
/// read 2026-11-13), by SHA-1 thumbprint; a chain also counts when its root is published by
/// Microsoft or DigiCert under another thumbprint (a new or cross-signed root). Anything else that
/// this PC still trusts is an inspecting proxy's own certificate authority.
/// </summary>
public static class MicrosoftChains
{
    public static readonly IReadOnlyCollection<string> RootThumbprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "A8985D3A65E5E5C4B2D7D66D40C6DD2FB19C5436", // DigiCert Global Root CA
        "DF3C24F9BFD666761B268073FE06D1CC8D4F82A4", // DigiCert Global Root G2
        "7E04DE896A3E666D00E687D33FFAD93BE83D349E", // DigiCert Global Root G3
        "17F3DE5E9F0F19E98EF61F32266E20C407AE30EE", // DigiCert TLS ECC P384 Root G5
        "A78849DC5D7C758C8CDE399856B3AAD0B2A57135", // DigiCert TLS RSA 4096 Root G5
        "999A64C37FF47D9FAB95F14769891460EEC4C3C5", // Microsoft ECC Root Certificate Authority 2017
        "73A5E64A3BFF8316FF0EDCCC618A906E4EAE4D74", // Microsoft RSA Root Certificate Authority 2017
    };

    private static readonly string[] Publishers = { "Microsoft Corporation", "DigiCert Inc" };

    /// <summary>The chain's root is one Microsoft's services use: by thumbprint, or published by Microsoft or DigiCert.</summary>
    public static bool IsMicrosoftRoot(string thumbprint, string organization)
        => (!string.IsNullOrEmpty(thumbprint) && RootThumbprints.Contains(thumbprint))
            || Publishers.Any(p => string.Equals((organization ?? "").Trim(), p, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] Domains =
    {
        "microsoft.com", "microsoftonline.com", "microsoftonline-p.com", "windows.net", "office.com", "office365.com",
        "office.net", "outlook.com", "live.com", "msauth.net", "msftauth.net", "azure.com", "azure.net", "windowsazure.com",
    };

    /// <summary>A host in one of Microsoft's own domains: a redirect there is the service's own (never followed).</summary>
    public static bool IsMicrosoftHost(string host)
    {
        if (string.IsNullOrEmpty(host)) { return false; }
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        return Domains.Any(d => h == d || h.EndsWith("." + d, StringComparison.Ordinal));
    }

    /// <summary>An organization name for IT-facing text: printable, one line, at most 64 characters.</summary>
    public static string CleanOrganization(string text)
    {
        var clean = new string((text ?? "").Where(c => !char.IsControl(c) && c != '"').ToArray()).Trim();
        return clean.Length <= 64 ? clean : clean.Substring(0, 64);
    }
}

/// <summary>The rules that turn what an HTTPS request showed into an outcome, without system calls (testable).</summary>
public static class HttpsRules
{
    [DataContract]
    private sealed class OpenIdDocument
    {
        [DataMember(Name = "issuer")] public string Issuer { get; set; }
    }

    public const string IssuerPrefix = "https://login.microsoftonline.com/";

    /// <summary>The OpenID document parses as JSON and its issuer starts with https://login.microsoftonline.com/.</summary>
    public static bool IsOpenIdDocument(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.TrimStart()[0] != '{') { return false; }
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
            var document = (OpenIdDocument)new DataContractJsonSerializer(typeof(OpenIdDocument)).ReadObject(stream);
            return document?.Issuer != null && document.Issuer.StartsWith(IssuerPrefix, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is SerializationException or InvalidCastException or FormatException or ArgumentException or IOException)
        {
            return false;
        }
    }

    /// <summary>The answer is an HTML page (a login page of the network), by its type or its first character.</summary>
    public static bool IsHtml(HttpsObservation o)
        => (o.ContentType ?? "").IndexOf("html", StringComparison.OrdinalIgnoreCase) >= 0 || (o.Body ?? "").TrimStart().StartsWith("<", StringComparison.Ordinal);

    /// <summary>
    /// One request's outcome. "Answered" means a TLS handshake with a certificate valid for
    /// exactly that host (the system's trust) and any HTTP status from the server; for the sign-in
    /// service only its OpenID document counts. Through a proxy without a TLS handshake with the host,
    /// the proxy answered itself (407: sign-in), also with a redirect: a captive portal shows only
    /// after the handshake (a redirect away from Microsoft, or HTML where JSON was expected).
    /// </summary>
    public static ProbeAnswer Classify(HttpsObservation o, ConnectionTarget target, bool viaProxy)
    {
        if (o == null) { return ProbeAnswer.Failed(RouteOutcome.Unknown); }
        if (o.Failure is RouteOutcome failure)
        {
            if (o.TlsSeen && o.NameMismatch) { return ProbeAnswer.Failed(RouteOutcome.CertificateWrongHost); }
            if (o.TlsSeen && o.ChainUntrusted) { return ProbeAnswer.Failed(RouteOutcome.CertificateUntrusted); }
            return ProbeAnswer.Failed(failure);
        }
        var status = o.Status ?? 0;
        var redirectAway = status is >= 300 and < 400 && o.LocationHost.Length > 0 && !MicrosoftChains.IsMicrosoftHost(o.LocationHost);
        if (!o.TlsSeen)
        {
            // Only a proxy can answer without a TLS handshake with the host.
            if (status == 407) { return new ProbeAnswer(RouteOutcome.ProxyAuthRequired, status); }
            // A proxy's redirect to CONNECT is the proxy answering itself, not a sign-in page seen.
            return new ProbeAnswer(viaProxy ? RouteOutcome.ProxyAnswered : RouteOutcome.Unknown, o.Status);
        }
        // The certificate checks ran and passed (a failed check ends as a failure above).
        if (redirectAway) { return new ProbeAnswer(RouteOutcome.CaptivePortal, status) { RedirectHost = o.LocationHost }; }
        if (viaProxy && status is 502 or 504) { return new ProbeAnswer(RouteOutcome.ProxyAnswered, status); }
        if (target.ExpectsOpenId)
        {
            if (status == 200 && IsOpenIdDocument(o.Body)) { /* answered */ }
            else if (IsHtml(o)) { return new ProbeAnswer(RouteOutcome.CaptivePortal, status); }
            else { return new ProbeAnswer(RouteOutcome.UnexpectedAnswer, status); }
        }
        return o.MicrosoftChain
            ? new ProbeAnswer(RouteOutcome.Answered, status)
            : new ProbeAnswer(RouteOutcome.Inspected, status) { IssuerOrganization = MicrosoftChains.CleanOrganization(o.IssuerOrganization) };
    }
}
