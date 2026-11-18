using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace EndpointToolbelt.App.Core;

/// <summary>
/// The system's two proxy settings: the signed-in user's WinINet proxy (what the browser
/// uses) and the machine's WinHTTP proxy (what services running as SYSTEM use; Microsoft does
/// not document which one the IME uses, so both are tested).
/// </summary>
public interface IProxyRoutes
{
    /// <summary>The user's WinINet proxy for this URL (PAC and automatic detection included), or null when it goes direct.</summary>
    Uri BrowserProxy(Uri target);

    /// <summary>The machine's WinHTTP proxy and its bypass list, or null when there is none.</summary>
    (Uri Proxy, string Bypass)? MachineProxy();

    /// <summary>
    /// One unauthenticated HTTPS request to the host, directly or through a proxy, and what it
    /// showed as raw facts (<see cref="HttpsRules.Classify"/> judges them): the TLS certificate checks,
    /// the HTTP status, a redirect's host and, for a GET, the start of the body.
    /// </summary>
    HttpsObservation Https(HttpsRequest request);
}

/// <summary>The rules for WinHTTP's proxy settings, without system calls (testable).</summary>
public static class ProxyRules
{
    private static readonly char[] Separators = { ';', ' ' };

    /// <summary>
    /// The HTTPS proxy of a WinHTTP proxy string: "host:port", or per scheme as
    /// "http=host:port;https=host:port". Null when there is none for HTTPS.
    /// </summary>
    public static Uri ParseWinHttpProxy(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) { return null; }
        var entries = value.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        var chosen = entries.FirstOrDefault(e => e.StartsWith("https=", StringComparison.OrdinalIgnoreCase))
            ?? entries.FirstOrDefault(e => !e.Contains("="));
        if (chosen == null) { return null; }
        var hostPort = chosen.Contains("=") ? chosen.Substring(chosen.IndexOf('=') + 1) : chosen;
        if (!hostPort.Contains("://")) { hostPort = "http://" + hostPort; }
        return Uri.TryCreate(hostPort, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host) ? uri : null;
    }

    /// <summary>
    /// The host is on a WinHTTP bypass list: entries separated by ';' or spaces, '*' as a
    /// wildcard, and "&lt;local&gt;" for names without a dot.
    /// </summary>
    public static bool Bypassed(string host, string bypass)
    {
        if (string.IsNullOrWhiteSpace(bypass) || string.IsNullOrEmpty(host)) { return false; }
        foreach (var entry in bypass.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (entry.Equals("<local>", StringComparison.OrdinalIgnoreCase)) { if (!host.Contains('.')) { return true; } continue; }
            var pattern = "^" + Regex.Escape(entry.Trim()).Replace("\\*", ".*") + "$";
            if (Regex.IsMatch(host, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) { return true; }
        }
        return false;
    }

    /// <summary>A proxy for the log and the result: host and port only.</summary>
    public static string Label(Uri proxy) => proxy == null ? "" : proxy.Host + ":" + proxy.Port;
}

/// <summary>
/// What a proxy answered to CONNECT itself, where .NET Framework hides it (without system calls,
/// testable). HttpWebRequest makes the CONNECT with a request of its own that follows a 3xx answer
/// directly to the Location's host: the caller then sees a failure for that host
/// (NameResolutionFailure "The remote name could not be resolved: '&lt;portal host&gt;'", or
/// ConnectFailure with a refused socket), never the 302. A bare CONNECT shows the proxy's status.
/// </summary>
public static class ProxyConnect
{
    /// <summary>The bare CONNECT waits at most this long (the request's own time-out when shorter).</summary>
    public const int ProbeTimeoutMs = 4000;

    /// <summary>At most this much of the proxy's answer is read: its status line is enough.</summary>
    public const int HeadLimit = 1024;

    private static readonly Regex StatusLine = new(@"^HTTP/1\.[01] (\d{3})(?:[ \r\n]|$)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Through a proxy, before any TLS handshake, what a proxy's own answer can hide: a name that could
    /// not be resolved (through a proxy only the redirect's host is resolved here), a refusal, another
    /// failure, or a status from whoever answered (the redirect's host may answer instead of the
    /// proxy). A time-out and a 407 (the proxy itself asks for sign-in) are not asked again.
    /// </summary>
    public static bool MayHideAnswer(HttpsRequest request, HttpsObservation observation)
        => request?.Proxy != null && observation != null && !observation.TlsSeen
            && (observation.Failure is RouteOutcome.NameNotResolved or RouteOutcome.Refused or RouteOutcome.Unknown
                || (observation.Failure == null && observation.Status is int status && status != 407));

    /// <summary>The address is the proxy's own host and port (the only place the user's sign-in may go).</summary>
    public static bool SameEndpoint(string host, int port, Uri proxy)
        => proxy != null && !string.IsNullOrEmpty(host) && port == proxy.Port && string.Equals(host.TrimEnd('.'), proxy.Host.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The status code of an HTTP status line ("HTTP/1.1 302 Found"), or null.</summary>
    public static int? ParseStatus(string head)
    {
        var match = StatusLine.Match(head ?? "");
        return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// The request's observation with what the bare CONNECT showed: a proxy that answered anything
    /// but 2xx answered itself (407: sign-in), with its status and without the redirect's host (a
    /// proxy's redirect alone is no captive portal). A 407 to the bare CONNECT while the request
    /// gave the user's sign-in: the proxy answered, its status after the sign-in is unknown. No
    /// answer, or 2xx (the tunnel opens): the request's own failure stands.
    /// </summary>
    public static HttpsObservation Reconcile(HttpsObservation observation, int? connectStatus, bool proxyCredentials)
    {
        if (connectStatus is not int status || status is >= 200 and < 300) { return observation; }
        if (status == 407 && proxyCredentials) { return new HttpsObservation(); }
        return new HttpsObservation { Status = status };
    }
}

/// <summary>
/// The user's Windows sign-in for the user's proxy only, asked by its own host and port;
/// anyone else, such as the host a proxy's redirect points to, gets nothing.
/// </summary>
public sealed class ProxyOnlyCredentials : ICredentials, ICredentialsByHost
{
    private readonly Uri _proxy;

    public ProxyOnlyCredentials(Uri proxy) => _proxy = proxy;

    public NetworkCredential GetCredential(Uri uri, string authType)
        => uri != null && ProxyConnect.SameEndpoint(uri.Host, uri.Port, _proxy) ? CredentialCache.DefaultNetworkCredentials : null;

    public NetworkCredential GetCredential(string host, int port, string authenticationType)
        => ProxyConnect.SameEndpoint(host, port, _proxy) ? CredentialCache.DefaultNetworkCredentials : null;
}

/// <summary>The system's proxy settings and requests through them.</summary>
public sealed class SystemProxyRoutes : IProxyRoutes
{
    public Uri BrowserProxy(Uri target)
    {
        try
        {
            var proxy = WebRequest.GetSystemWebProxy();
            if (proxy.IsBypassed(target)) { return null; }
            var through = proxy.GetProxy(target);
            return through == null || through == target ? null : through;
        }
        catch (Exception ex) when (ex is WebException or InvalidOperationException or UriFormatException)
        {
            return null;
        }
    }

    public (Uri Proxy, string Bypass)? MachineProxy()
    {
        var info = new NativeMethods.WinHttpProxyInfo();
        try
        {
            if (!NativeMethods.WinHttpGetDefaultProxyConfiguration(ref info)) { return null; }
            // 3: WINHTTP_ACCESS_TYPE_NAMED_PROXY; anything else goes direct.
            if (info.AccessType != 3) { return null; }
            var proxy = ProxyRules.ParseWinHttpProxy(Marshal.PtrToStringUni(info.Proxy));
            return proxy == null ? null : (proxy, Marshal.PtrToStringUni(info.ProxyBypass) ?? "");
        }
        finally
        {
            if (info.Proxy != IntPtr.Zero) { NativeMethods.GlobalFree(info.Proxy); }
            if (info.ProxyBypass != IntPtr.Zero) { NativeMethods.GlobalFree(info.ProxyBypass); }
        }
    }

    private static readonly Regex OrganizationPart = new("(?:^|,\\s*)O=(\"[^\"]*\"|[^,]*)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>A certificate's organization (O), else its common name; empty for none.</summary>
    internal static string OrganizationOf(X509Certificate2 certificate)
    {
        if (certificate == null) { return ""; }
        var match = OrganizationPart.Match(certificate.Subject ?? "");
        var organization = match.Success ? match.Groups[1].Value.Trim().Trim('"') : "";
        return organization.Length > 0 ? organization : certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false) ?? "";
    }

    /// <summary>
    /// One HTTPS request the way the user's apps make it (WinINet's proxy when one is given),
    /// without cookies or credentials for the server and without following a redirect. The system's
    /// certificate validation decides; its verdict, the chain's root and the answer are recorded.
    /// Only the status, a redirect's host, the content type and the start of a GET's body are kept.
    /// </summary>
    public HttpsObservation Https(HttpsRequest request)
    {
        var observation = new HttpsObservation();
        try
        {
            var web = (HttpWebRequest)WebRequest.Create(new Uri("https://" + request.Host + request.Path));
            web.Method = request.Get ? "GET" : "HEAD";
            if (request.Get) { web.Accept = "application/json"; }
            web.AllowAutoRedirect = false;
            web.Timeout = request.TimeoutMs;
            web.ReadWriteTimeout = request.TimeoutMs;
            // No cookies and no credentials for the server; the user's proxy may get the user's
            // Windows sign-in, as the user's apps give it; the machine proxy never does.
            web.CookieContainer = null;
            web.Credentials = null;
            web.UseDefaultCredentials = false;
            web.PreAuthenticate = false;
            // A connection of its own, closed after the answer: a pooled tunnel would skip the handshake.
            web.KeepAlive = false;
            web.ConnectionGroupName = "probe-" + Guid.NewGuid().ToString("N");
            // The user's sign-in only for the proxy's own host and port: .NET follows a proxy's redirect
            // to CONNECT by itself and would otherwise answer a sign-in request from the redirect's host.
            web.Proxy = request.Proxy == null ? null : new WebProxy(request.Proxy) { Credentials = request.ProxyCredentials ? new ProxyOnlyCredentials(request.Proxy) : null };
            web.ServerCertificateValidationCallback = (_, certificate, chain, errors) =>
            {
                observation.TlsSeen = true;
                observation.NameMismatch = (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0;
                observation.ChainUntrusted = (errors & (SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0;
                var elements = chain?.ChainElements;
                var root = elements != null && elements.Count > 0 ? elements[elements.Count - 1].Certificate : certificate as X509Certificate2;
                var organization = OrganizationOf(root);
                observation.IssuerOrganization = organization;
                observation.MicrosoftChain = MicrosoftChains.IsMicrosoftRoot(root?.Thumbprint, organization);
                // The system's validation decides: a certificate that does not validate ends the request.
                return errors == SslPolicyErrors.None;
            };
            using var response = (HttpWebResponse)web.GetResponse();
            Read(response, request, observation);
        }
        catch (WebException ex) when (ex.Response is HttpWebResponse answer)
        {
            using (answer) { Read(answer, request, observation); }
        }
        catch (WebException ex)
        {
            observation.Failure = RouteRules.FromWebException(ex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or UriFormatException or IOException)
        {
            observation.Failure = RouteOutcome.Unknown;
        }
        // .NET Framework follows a proxy's redirect to CONNECT by itself, directly to the redirect's
        // host, and reports only how that failed (a name, a refusal): ask the proxy once more, raw.
        return ProxyConnect.MayHideAnswer(request, observation)
            ? ProxyConnect.Reconcile(observation, ConnectStatus(request.Proxy, request.Host, Math.Min(request.TimeoutMs, ProxyConnect.ProbeTimeoutMs)), request.ProxyCredentials)
            : observation;
    }

    /// <summary>
    /// The status line a proxy answers to a bare CONNECT for the host on port 443, without
    /// credentials and without anything sent through a tunnel it opens; null without an HTTP answer.
    /// </summary>
    internal static int? ConnectStatus(Uri proxy, string host, int timeoutMs)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            if (!client.ConnectAsync(proxy.Host, proxy.Port).Wait(timeoutMs)) { return null; }
            client.ReceiveTimeout = timeoutMs;
            client.SendTimeout = timeoutMs;
            var stream = client.GetStream();
            var ask = Encoding.ASCII.GetBytes("CONNECT " + host + ":443 HTTP/1.1\r\nHost: " + host + ":443\r\nProxy-Connection: close\r\n\r\n");
            stream.Write(ask, 0, ask.Length);
            var buffer = new byte[ProxyConnect.HeadLimit];
            var read = 0;
            int n;
            while (read < buffer.Length && (n = stream.Read(buffer, read, buffer.Length - read)) > 0)
            {
                read += n;
                if (Array.IndexOf(buffer, (byte)'\n', 0, read) >= 0) { break; }
            }
            return ProxyConnect.ParseStatus(Encoding.ASCII.GetString(buffer, 0, read));
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or AggregateException or ObjectDisposedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static void Read(HttpWebResponse response, HttpsRequest request, HttpsObservation observation)
    {
        observation.Status = (int)response.StatusCode;
        observation.ContentType = response.ContentType ?? "";
        var location = response.Headers?["Location"];
        if (!string.IsNullOrEmpty(location) && Uri.TryCreate(location, UriKind.Absolute, out var target)) { observation.LocationHost = target.Host; }
        if (!request.Get) { return; }
        try
        {
            using var stream = response.GetResponseStream();
            if (stream == null) { return; }
            var buffer = new byte[HttpsRequest.ReadLimit];
            var read = 0;
            int n;
            while (read < buffer.Length && (n = stream.Read(buffer, read, buffer.Length - read)) > 0) { read += n; }
            observation.Body = Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or WebException or ObjectDisposedException)
        {
            observation.Body = "";
        }
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct WinHttpProxyInfo
        {
            public int AccessType;
            public IntPtr Proxy;
            public IntPtr ProxyBypass;
        }

        [DllImport("winhttp.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinHttpGetDefaultProxyConfiguration(ref WinHttpProxyInfo info);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GlobalFree(IntPtr handle);
    }
}
