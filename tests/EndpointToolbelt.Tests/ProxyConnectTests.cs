using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using EndpointToolbelt.App.Core;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// A proxy that answers CONNECT itself with a status other than 2xx. HttpWebRequest (.NET
/// Framework 4.8) makes the CONNECT with a request of its own that follows a 3xx directly to the
/// Location's host, so the caller sees only that host's failure; the check asks the proxy once more
/// with a bare CONNECT and reports "the proxy answered itself (HTTP code)", never dns-failed,
/// refused, offline or a captive portal. The exception shapes below are the ones .NET Framework
/// 4.8 throws for a 302 to CONNECT (measured with a loopback proxy).
/// </summary>
public class ProxyConnectTests
{
    private static readonly Uri Proxy = new("http://proxy.contoso.example:8080");

    private static HttpsRequest Through(Uri proxy, bool credentials = true)
        => new() { Host = "manage.microsoft.com", Proxy = proxy, ProxyCredentials = credentials, TimeoutMs = 6000 };

    /// <summary>The exceptions HttpWebRequest throws when the proxy answered CONNECT with a 3xx.</summary>
    public static IEnumerable<object[]> RedirectFollowedShapes()
    {
        // Location http://wifi.contoso.example/... (301, 302, 303 and 307 alike): its name is resolved here and fails.
        yield return new object[] { new WebException("The remote name could not be resolved: 'wifi.contoso.example'", WebExceptionStatus.NameResolutionFailure), RouteOutcome.NameNotResolved };
        // Location on an address where nothing listens.
        yield return new object[] { new WebException("Unable to connect to the remote server", new SocketException((int)SocketError.ConnectionRefused), WebExceptionStatus.ConnectFailure, null), RouteOutcome.Refused };
        // A tunnel that answered but closed (the same request after a 200 that hung up).
        yield return new object[] { new WebException("The underlying connection was closed: Unable to connect to the remote server.", new IOException("The operation is not allowed on non-connected sockets."), WebExceptionStatus.ConnectFailure, null), RouteOutcome.Unknown };
    }

    [Theory]
    [MemberData(nameof(RedirectFollowedShapes))]
    public void A_redirect_to_CONNECT_that_NET_followed_is_asked_again_and_becomes_the_proxy_answering_itself(WebException shape, RouteOutcome failure)
    {
        var observation = HttpsObservation.Failed(RouteRules.FromWebException(shape));
        Assert.Equal(failure, observation.Failure);
        var request = Through(Proxy);
        Assert.True(ProxyConnect.MayHideAnswer(request, observation));

        var answer = HttpsRules.Classify(ProxyConnect.Reconcile(observation, 302, request.ProxyCredentials), ConnectivityTest.Management, viaProxy: true);
        Assert.Equal(RouteOutcome.ProxyAnswered, answer.Outcome);
        Assert.Equal(302, answer.HttpStatus);
        Assert.Equal("", answer.RedirectHost);
    }

    [Fact]
    public void Only_an_answer_through_a_proxy_before_TLS_is_asked_again()
    {
        var name = HttpsObservation.Failed(RouteOutcome.NameNotResolved);
        Assert.False(ProxyConnect.MayHideAnswer(Through(null), name));
        Assert.False(ProxyConnect.MayHideAnswer(Through(Proxy), HttpsObservation.Failed(RouteOutcome.TimedOut)));
        Assert.False(ProxyConnect.MayHideAnswer(Through(Proxy), HttpsObservation.Failed(RouteOutcome.TlsFailed)));
        Assert.False(ProxyConnect.MayHideAnswer(Through(Proxy), new HttpsObservation { Failure = RouteOutcome.Unknown, TlsSeen = true }));
        Assert.False(ProxyConnect.MayHideAnswer(Through(Proxy), new HttpsObservation { TlsSeen = true, Status = 200 }));
        Assert.False(ProxyConnect.MayHideAnswer(Through(Proxy), new HttpsObservation { Status = 407 }));
        Assert.False(ProxyConnect.MayHideAnswer(Through(null), new HttpsObservation { Status = 403 }));
        Assert.False(ProxyConnect.MayHideAnswer(Through(Proxy), null));
        Assert.True(ProxyConnect.MayHideAnswer(Through(Proxy), HttpsObservation.Failed(RouteOutcome.Refused)));
        // A status without TLS may come from the redirect's host instead of the proxy (a 401 there).
        Assert.True(ProxyConnect.MayHideAnswer(Through(Proxy), new HttpsObservation { Status = 401 }));
        Assert.True(ProxyConnect.MayHideAnswer(Through(Proxy), new HttpsObservation { Status = 403 }));
    }

    [Fact]
    public void The_user_s_sign_in_goes_only_to_the_proxy_s_own_host_and_port()
    {
        var credentials = new ProxyOnlyCredentials(Proxy);
        Assert.Same(CredentialCache.DefaultNetworkCredentials, credentials.GetCredential(new Uri("http://proxy.contoso.example:8080/"), "NTLM"));
        Assert.Same(CredentialCache.DefaultNetworkCredentials, credentials.GetCredential("PROXY.contoso.example.", 8080, "Negotiate"));
        Assert.Null(credentials.GetCredential(new Uri("http://wifi.contoso.example/guest/login"), "NTLM"));
        Assert.Null(credentials.GetCredential(new Uri("http://proxy.contoso.example:3128/"), "NTLM"));
        Assert.Null(credentials.GetCredential("wifi.contoso.example", 8080, "Negotiate"));
        Assert.Null(credentials.GetCredential((Uri)null, "NTLM"));
        Assert.False(ProxyConnect.SameEndpoint("", 8080, Proxy));
        Assert.False(ProxyConnect.SameEndpoint("proxy.contoso.example", 8080, null));
    }

    [Fact]
    public void The_bare_CONNECT_decides_only_when_the_proxy_answered_itself()
    {
        var failed = HttpsObservation.Failed(RouteOutcome.NameNotResolved);
        ProbeAnswer Of(int? status, bool credentials = true) => HttpsRules.Classify(ProxyConnect.Reconcile(failed, status, credentials), ConnectivityTest.Management, viaProxy: true);

        // No answer, or the tunnel opens: the request's own failure stands.
        Assert.Equal(RouteOutcome.NameNotResolved, Of(null).Outcome);
        Assert.Equal(RouteOutcome.NameNotResolved, Of(200).Outcome);
        Assert.Equal(RouteOutcome.NameNotResolved, Of(204).Outcome);
        // The proxy answered itself, with its status.
        Assert.Equal((RouteOutcome.ProxyAnswered, (int?)403), (Of(403).Outcome, Of(403).HttpStatus));
        Assert.Equal((RouteOutcome.ProxyAnswered, (int?)502), (Of(502).Outcome, Of(502).HttpStatus));
        Assert.Equal((RouteOutcome.ProxyAnswered, (int?)301), (Of(301).Outcome, Of(301).HttpStatus));
        // 407 without the user's sign-in: sign-in asked; with it, the status after the sign-in is unknown.
        Assert.Equal(RouteOutcome.ProxyAuthRequired, Of(407, credentials: false).Outcome);
        Assert.Equal((RouteOutcome.ProxyAnswered, (int?)null), (Of(407).Outcome, Of(407).HttpStatus));
        Assert.Equal("the proxy answered itself, so the service was not reached through it",
            ConnectivityTest.RouteWords(new RouteResult { Route = RouteKind.UserProxy, Step = RouteStep.Https, Outcome = RouteOutcome.ProxyAnswered }));
    }

    [Theory]
    [InlineData("HTTP/1.1 302 Found\r\nLocation: http://wifi.contoso.example/\r\n", 302)]
    [InlineData("HTTP/1.0 403 Forbidden\r\n", 403)]
    [InlineData("HTTP/1.1 200 Connection established\r\n\r\n", 200)]
    [InlineData("HTTP/1.1 407\r\n", 407)]
    [InlineData("HTTP/1.1 502", 502)]
    [InlineData("HTTP/2 200\r\n", null)]
    [InlineData("SSH-2.0-OpenSSH\r\n", null)]
    [InlineData("HTTP/1.1 30", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_status_line_of_the_bare_CONNECT_is_read_strictly(string head, int? status)
        => Assert.Equal(status, ProxyConnect.ParseStatus(head));

    // --- The real request through a loopback proxy -------------------------------------------------

    /// <summary>
    /// A listener on the loopback address that answers every request on a connection with one fixed
    /// answer, and keeps each request's first line plus the name and scheme of any sign-in header.
    /// </summary>
    private sealed class LoopbackProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly string _answer;

        public List<string> Requests { get; } = new();

        public List<string> SignIns { get; } = new();

        public Uri Address { get; }

        public LoopbackProxy(int status, string location, string extraHeaders = "")
        {
            _answer = "HTTP/1.1 " + status + " Test\r\n" + (location == null ? "" : "Location: " + location + "\r\n") + extraHeaders + "Content-Length: 0\r\n\r\n";
            _listener.Start();
            Address = new Uri("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port);
            new Thread(Accept) { IsBackground = true, Name = "loopback proxy" }.Start();
        }

        private void Accept()
        {
            try
            {
                while (true)
                {
                    var client = _listener.AcceptTcpClient();
                    new Thread(() => Serve(client)) { IsBackground = true, Name = "loopback connection" }.Start();
                }
            }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException or InvalidOperationException) { }
        }

        // One connection: an NTLM or Negotiate handshake sends its next request on the same connection.
        private void Serve(TcpClient client)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    stream.ReadTimeout = 5000;
                    var buffer = new byte[8192];
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        var lines = Encoding.ASCII.GetString(buffer, 0, read).Split(new[] { "\r\n" }, StringSplitOptions.None);
                        lock (Requests)
                        {
                            Requests.Add(lines[0]);
                            foreach (var line in lines)
                            {
                                var colon = line.IndexOf(':');
                                if (colon > 0 && line.Substring(0, colon).EndsWith("Authorization", StringComparison.OrdinalIgnoreCase))
                                {
                                    SignIns.Add(line.Substring(0, colon) + ": " + line.Substring(colon + 1).Trim().Split(' ')[0]);
                                }
                            }
                        }
                        var bytes = Encoding.ASCII.GetBytes(_answer);
                        stream.Write(bytes, 0, bytes.Length);
                    }
                }
            }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException or InvalidOperationException) { }
        }

        public void Dispose() => _listener.Stop();
    }

    [Theory]
    [InlineData("NTLM")]
    [InlineData("Negotiate")]
    public void A_proxy_s_redirect_never_carries_the_user_s_sign_in_to_the_redirect_s_host(string scheme)
    {
        // The redirect's host asks for a Windows sign-in; .NET follows the proxy's 302 there by itself.
        using var target = new LoopbackProxy(401, null, "WWW-Authenticate: " + scheme + "\r\nProxy-Authenticate: " + scheme + "\r\n");
        using var proxy = new LoopbackProxy(302, target.Address + "login");

        var observation = new SystemProxyRoutes().Https(Through(proxy.Address, credentials: true));

        lock (target.Requests) { Assert.NotEmpty(target.Requests); Assert.Empty(target.SignIns); }
        // The status is the proxy's own (the bare CONNECT), not the 401 of the redirect's host.
        Assert.Equal(302, observation.Status);
        Assert.Equal(RouteOutcome.ProxyAnswered, HttpsRules.Classify(observation, ConnectivityTest.Management, viaProxy: true).Outcome);
    }

    [Fact]
    public void A_proxy_that_asks_for_a_Windows_sign_in_still_gets_it_when_the_user_s_route_may_give_it()
    {
        using var proxy = new LoopbackProxy(407, null, "Proxy-Authenticate: NTLM\r\n");
        var observation = new SystemProxyRoutes().Https(Through(proxy.Address, credentials: true));
        Assert.Equal(407, observation.Status);
        lock (proxy.Requests) { Assert.Contains("Proxy-Authorization: NTLM", proxy.SignIns); }

        using var machine = new LoopbackProxy(407, null, "Proxy-Authenticate: NTLM\r\n");
        var none = new SystemProxyRoutes().Https(Through(machine.Address, credentials: false));
        Assert.Equal(407, none.Status);
        lock (machine.Requests) { Assert.Empty(machine.SignIns); }
    }

    [Theory]
    [InlineData(302, "http://127.0.0.1:1/guest/login?from=etb")]
    [InlineData(307, "http://127.0.0.1:1/")]
    [InlineData(302, null)]
    [InlineData(302, "/login")]
    [InlineData(403, null)]
    public void Through_a_real_proxy_that_answers_CONNECT_itself_the_check_sees_its_status(int status, string location)
    {
        using var proxy = new LoopbackProxy(status, location);
        var request = Through(proxy.Address, credentials: false);

        var observation = new SystemProxyRoutes().Https(request);

        Assert.Null(observation.Failure);
        Assert.False(observation.TlsSeen);
        Assert.Equal(status, observation.Status);
        var answer = HttpsRules.Classify(observation, ConnectivityTest.Management, viaProxy: true);
        Assert.Equal(RouteOutcome.ProxyAnswered, answer.Outcome);
        Assert.Equal(status, answer.HttpStatus);
        Assert.Equal("", answer.RedirectHost);
        lock (proxy.Requests) { Assert.All(proxy.Requests, r => Assert.Equal("CONNECT manage.microsoft.com:443 HTTP/1.1", r)); }
    }

    [Fact]
    public void Through_a_proxy_that_is_not_there_the_failure_stands()
    {
        // A port nobody listens on (taken and released): the request and the bare CONNECT are both refused.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var observation = new SystemProxyRoutes().Https(Through(new Uri("http://127.0.0.1:" + port), credentials: false));

        Assert.Equal(RouteOutcome.Refused, observation.Failure);
        Assert.Null(observation.Status);
    }
}
