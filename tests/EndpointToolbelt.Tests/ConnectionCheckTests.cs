using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EndpointToolbelt.App.Core;
using EndpointToolbelt.Protocol;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// Check Microsoft connections. Fixed cases with a fake TCP step and a fake HTTPS layer
/// (no network): answered (200, 302, 401), a TLS failure, a certificate for another name, an untrusted
/// chain, an inspecting proxy, a captive portal (a redirect away, an HTML page where JSON was
/// expected), a proxy that asks for sign-in or answers itself, and a time-out. For each, Home's row,
/// the card, Copy details for IT, the run log lines and the history line carry the same meaning.
/// </summary>
public class ConnectionCheckTests
{
    private static readonly DateTime T0 = new(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
    private const string RunId = "local-0a1b2c3d";
    private const string UserProxy = "http://proxy.contoso.example:8080";
    private const string MachineProxy = "http://machine.contoso.example:3128";
    private const string OpenId = "{\"issuer\":\"https://login.microsoftonline.com/{tenantid}/v2.0\",\"token_endpoint\":\"https://login.microsoftonline.com/common/oauth2/v2.0/token\"}";

    // --- What an HTTPS request can show -----------------------------------------------------------

    /// <summary>The service answered over TLS from a Microsoft chain; a GET gets the OpenID document.</summary>
    private static HttpsObservation Ok(HttpsRequest r, int status = 200)
        => new() { TlsSeen = true, MicrosoftChain = true, IssuerOrganization = "DigiCert Inc", Status = status, ContentType = r.Get ? "application/json" : "", Body = r.Get ? OpenId : "" };

    private static HttpsObservation Inspected(HttpsRequest r)
        => new() { TlsSeen = true, MicrosoftChain = false, IssuerOrganization = "Contoso Inspection CA", Status = 200, ContentType = r.Get ? "application/json" : "", Body = r.Get ? OpenId : "" };

    private static HttpsObservation WrongHost() => new() { Failure = RouteOutcome.TlsFailed, TlsSeen = true, NameMismatch = true };
    private static HttpsObservation Untrusted() => new() { Failure = RouteOutcome.TlsFailed, TlsSeen = true, ChainUntrusted = true };

    /// <summary>A proxy (or the network) answering itself, without a TLS handshake with the host.</summary>
    private static HttpsObservation Itself(int status, string location = "") => new() { Status = status, LocationHost = location };

    /// <summary>Proxy settings and HTTPS answers the test decides, per request.</summary>
    internal sealed class FakeRoutes : IProxyRoutes
    {
        public Uri Browser { get; set; }
        public (Uri, string)? Machine { get; set; }
        public Func<HttpsRequest, HttpsObservation> Answer { get; set; } = r => Ok(r);
        public List<HttpsRequest> Requests { get; } = new();
        public Uri BrowserProxy(Uri target) => Browser;
        public (Uri Proxy, string Bypass)? MachineProxy() => Machine;

        public HttpsObservation Https(HttpsRequest request)
        {
            lock (Requests) { Requests.Add(request); }
            return Answer(request);
        }
    }

    /// <summary>A health source with a TCP step and the HTTPS layer, for Home's Connection to Intune row.</summary>
    private sealed class Source : IHealthSource, IProxyRoutes, IDirectRoute
    {
        public Func<string, ProbeAnswer> Direct { get; set; } = _ => ProbeAnswer.Connected;
        public FakeRoutes Routes { get; set; } = new();
        public DateTime UtcNow => T0;
        public string ImeServiceStatus() => "Running";
        public DsregInfo Dsreg() => new() { AzureAdJoined = "YES", AzureAdPrt = "YES" };
        public DateTime? MdmCertificateExpiryUtc() => T0.AddDays(300);
        public bool CanReach(string host, int port, int timeoutMs) => Direct(host).Outcome == RouteOutcome.TcpConnected;
        public ProbeAnswer Connect(string host, int port, int timeoutMs) => Direct(host);
        public double FreeSystemDriveGB() => 100;
        public bool ServicingRestartPending() => false;
        public Uri BrowserProxy(Uri target) => Routes.BrowserProxy(target);
        public (Uri Proxy, string Bypass)? MachineProxy() => Routes.MachineProxy();
        public HttpsObservation Https(HttpsRequest request) => Routes.Https(request);
    }

    /// <summary>Everything one case shows: the check's result, its words, the ticket, the history line and Home's row.</summary>
    private sealed class Case
    {
        public ConnectivityResult Result;
        public string Status;
        public Tone Tone;
        public List<ResultCard> Cards;
        public string Ticket;
        public string History;
        public string HistoryShown;
        public HealthResult Home;

        public ResultCard Card(string purposeStart) => Cards.Single(c => c.Title.StartsWith(purposeStart, StringComparison.Ordinal));

        public string Route(string host, string route, string step = "https") => Result.Lines.Single(l => l.StartsWith("ROUTE ", StringComparison.Ordinal) && l.Contains(" host=" + host + " ") && l.Contains(" route=" + route + " step=" + step + " "));

        public string Target(string host) => Result.Lines.Single(l => l.StartsWith("TARGET ", StringComparison.Ordinal) && l.Contains(" host=" + host + " "));
    }

    private static Case Run(Func<string, ProbeAnswer> direct, FakeRoutes routes)
    {
        var result = ConnectivityTest.Run((host, _, _) => direct(host), routes, RunId, () => T0);
        var (status, tone, cards) = ConnectivityTest.Present(result);
        var check = new LocalRun { Name = ConnectivityTest.Name, RunId = RunId, StartedUtc = T0, EndedUtc = T0.AddSeconds(6) };
        var header = new RunLogHeader { Check = check, AppVersion = "1.2.3", DeviceName = "PC-CONTOSO-01", User = "CONTOSO\\contosouser", Zone = TimeZoneInfo.Utc, NowUtc = T0.AddMinutes(1) };
        var history = ConnectivityTest.HistoryResult(result);
        var home = HealthChecks.Run(new Source { Direct = direct, Routes = routes }, new StatusInfo { Enrolled = true }, out _).Single(r => r.Name == HealthChecks.Connectivity);
        return new Case
        {
            Result = result,
            Status = status,
            Tone = tone,
            Cards = cards,
            Ticket = RunTicket.Text(header, cards, status, tone, ConnectivityTest.TicketLine(result)),
            History = history,
            HistoryShown = RunWords.HistoryLine("2026-11-12 11:00:00 +01:00;user;ConnTest;" + history),
            Home = home,
        };
    }

    private static string Line(string ticket, string label) => ticket.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries).Single(l => l.StartsWith(label + " · ", StringComparison.Ordinal));

    // --- Answered ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(200)]
    [InlineData(302)]
    [InlineData(401)]
    public void Every_service_answering_over_a_valid_certificate_is_answered_whatever_its_status(int status)
    {
        var routes = new FakeRoutes { Answer = r => Ok(r, r.Get ? 200 : status) };
        var c = Run(_ => ProbeAnswer.Connected, routes);

        Assert.Equal(ConnectionVerdict.AllAnswered, c.Result.Verdict);
        Assert.Equal("The 5 Microsoft services answered.", c.Status);
        Assert.Equal(Tone.Success, c.Tone);
        Assert.All(c.Cards, card => Assert.Equal(CardKind.Ok, card.Kind));
        var management = c.Card("Device management connection");
        Assert.Equal("Device management connection: answered", management.Title);
        Assert.Contains("Connection: a connection opened.", management.Detail);
        Assert.Contains("Secure request: the service answered (HTTP " + status + ").", management.Detail);
        Assert.DoesNotContain("not tested", management.Detail);
        Assert.DoesNotContain("not tried", management.Detail);
        Assert.Equal("Outlook on the web connection: answered (signing in was not tested)", c.Card("Outlook on the web").Title);
        Assert.DoesNotContain("were not tested", c.Status);

        // The request: no proxy, a HEAD to "/" for every host but sign-in, which GETs its OpenID document.
        Assert.All(routes.Requests, r => Assert.Null(r.Proxy));
        Assert.Equal(ConnectivityTest.OpenIdPath, routes.Requests.First(r => r.Host == "login.microsoftonline.com").Path);
        Assert.True(routes.Requests.First(r => r.Host == "login.microsoftonline.com").Get);
        Assert.False(routes.Requests.First(r => r.Host == "manage.microsoft.com").Get);
        Assert.Equal("/", routes.Requests.First(r => r.Host == "manage.microsoft.com").Path);

        // The run log: one PROXY line, then only the steps tried (no "skipped" noise without a proxy).
        Assert.Equal("PROXY run=local-0a1b2c3d user=none machine=none", c.Result.Lines[0]);
        Assert.DoesNotContain(c.Result.Lines, l => l.Contains("outcome=skipped") || l.Contains("reason="));
        Assert.Equal(1 + 5 * 2 + 5 + 1, c.Result.Lines.Count);
        Assert.Equal("ROUTE run=local-0a1b2c3d time=2026-11-12T10:00:00.000Z ms=0 target=\"Device management connection\" host=manage.microsoft.com port=443 route=direct step=tcp context=user-app outcome=tcp-connected",
            c.Route("manage.microsoft.com", "direct", "tcp"));
        Assert.EndsWith("route=direct step=https context=user-app outcome=answered http=" + status + " cert=microsoft", c.Route("manage.microsoft.com", "direct"));
        Assert.Equal("TARGET run=local-0a1b2c3d target=\"Device management connection\" host=manage.microsoft.com answer=answered", c.Target("manage.microsoft.com"));
        Assert.Equal("RESULT run=local-0a1b2c3d verdict=all-answered required-not-reachable=0", c.Result.Lines.Last());
        Assert.DoesNotContain(c.Result.Lines, l => l.Contains("https://") || l.Contains("openid") || l.Contains("issuer\""));

        Assert.Equal("Next · None needed.", Line(c.Ticket, "Next"));
        Assert.Equal("0|local-0a1b2c3d|all-answered", c.History);
        Assert.EndsWith("Check Microsoft connections: all services answered (0, reference local-0a1b2c3d)", c.HistoryShown);
        Assert.Equal(HealthStatus.Pass, c.Home.Status);
        Assert.Equal("manage.microsoft.com answered (HTTP " + status + ")", c.Home.Detail);
    }

    [Fact]
    public void Sign_in_counts_only_with_its_OpenID_document_from_Microsofts_issuer()
    {
        var target = ConnectivityTest.Targets.Single(t => t.Key == "sign-in");
        Assert.True(HttpsRules.IsOpenIdDocument(OpenId));
        Assert.False(HttpsRules.IsOpenIdDocument("{\"issuer\":\"https://login.contoso.example/\"}"));
        Assert.False(HttpsRules.IsOpenIdDocument("{\"no-issuer\":1}"));
        Assert.False(HttpsRules.IsOpenIdDocument("{not json"));
        Assert.False(HttpsRules.IsOpenIdDocument(""));
        var other = new HttpsObservation { TlsSeen = true, MicrosoftChain = true, Status = 200, ContentType = "application/json", Body = "{\"issuer\":\"https://login.contoso.example/\"}" };
        Assert.Equal(RouteOutcome.UnexpectedAnswer, HttpsRules.Classify(other, target, viaProxy: false).Outcome);
        var notFound = new HttpsObservation { TlsSeen = true, MicrosoftChain = true, Status = 404, ContentType = "application/json", Body = "{}" };
        Assert.Equal(RouteOutcome.UnexpectedAnswer, HttpsRules.Classify(notFound, target, viaProxy: false).Outcome);

        var c = Run(_ => ProbeAnswer.Connected, new FakeRoutes { Answer = r => r.Get ? other : Ok(r) });
        Assert.Equal(ConnectionVerdict.NotConfirmed, c.Result.Verdict);
        Assert.Equal("Not confirmed: the work account sign-in connection. The service gave an unexpected answer.", c.Status);
        var signIn = c.Card("Work account sign-in");
        Assert.Equal("Work account sign-in connection: not confirmed", signIn.Title);
        Assert.Contains("an unexpected answer (HTTP 200), not the sign-in service's document", signIn.Detail);
        Assert.Contains("outcome=unexpected-answer http=200", c.Route("login.microsoftonline.com", "direct"));
    }

    // --- The proxies ------------------------------------------------------------------------------

    [Fact]
    public void User_proxy_answers_and_machine_proxy_asks_for_sign_in_says_both_with_the_proxy_detail()
    {
        var routes = new FakeRoutes
        {
            Browser = new Uri(UserProxy),
            Machine = (new Uri(MachineProxy), "<local>"),
            Answer = r => r.Proxy?.Host == "machine.contoso.example" ? Itself(407) : Ok(r),
        };
        var c = Run(_ => ProbeAnswer.Failed(RouteOutcome.NameNotResolved), routes);

        Assert.Equal(ConnectionVerdict.DeviceRouteNotConfirmed, c.Result.Verdict);
        Assert.Equal("Your route works; the device's own route (machine proxy) is not confirmed: the proxy asks for sign-in.", c.Status);
        var management = c.Card("Device management connection");
        Assert.Equal(CardKind.Warning, management.Kind);
        Assert.Equal("Device management connection: answered for you, not confirmed for the device", management.Title);
        Assert.Contains("Through your proxy (proxy.contoso.example:8080): the service answered (HTTP 200).", management.Detail);
        Assert.Contains("Through the machine proxy (machine.contoso.example:3128): the proxy asks for sign-in (HTTP 407).", management.Detail);
        Assert.Contains("asks for a sign-in they cannot give", management.NextStep);
        Assert.Equal(CardKind.Ok, c.Card("Outlook on the web").Kind);

        // The user's proxy may get the user's sign-in (as the user's apps do); the machine proxy never.
        Assert.All(routes.Requests.Where(r => r.Proxy?.Host == "proxy.contoso.example"), r => Assert.True(r.ProxyCredentials));
        Assert.All(routes.Requests.Where(r => r.Proxy?.Host == "machine.contoso.example"), r => Assert.False(r.ProxyCredentials));

        // With proxies set the log keeps the full detail, also why a route was not tried.
        Assert.Equal("PROXY run=local-0a1b2c3d user=set machine=set", c.Result.Lines[0]);
        Assert.EndsWith("route=machine-proxy step=https context=user-app outcome=proxy-auth http=407 proxy=machine.contoso.example:3128", c.Route("manage.microsoft.com", "machine-proxy"));
        Assert.EndsWith("route=user-proxy step=https context=user-app outcome=answered http=200 proxy=proxy.contoso.example:8080 cert=microsoft", c.Route("manage.microsoft.com", "user-proxy"));
        Assert.EndsWith("route=direct step=tcp context=user-app outcome=dns-failed", c.Route("manage.microsoft.com", "direct", "tcp"));
        Assert.EndsWith("route=machine-proxy step=https context=user-app outcome=skipped reason=not-used", c.Route("outlook.office.com", "machine-proxy"));
        Assert.EndsWith("answer=answered-for-user-only", c.Target("manage.microsoft.com"));
        Assert.Equal("0|local-0a1b2c3d|device-route-not-confirmed", c.History);

        Assert.Equal(HealthStatus.Warn, c.Home.Status);
        Assert.Equal("Answered for you, not confirmed for the device: the machine proxy asks for sign-in", c.Home.Detail);
    }

    [Fact]
    public void A_user_proxy_that_asks_for_sign_in_is_reported_and_not_answered()
    {
        var c = Run(_ => ProbeAnswer.Failed(RouteOutcome.Refused), new FakeRoutes { Browser = new Uri(UserProxy), Answer = _ => Itself(407) });
        Assert.Equal(ConnectionVerdict.NotConfirmed, c.Result.Verdict);
        Assert.EndsWith("The proxy asks for sign-in.", c.Status);
        Assert.Equal("Contact your IT support: the proxy asks for a sign-in this check could not give.", c.Card("Device management connection").NextStep);
        Assert.Contains("outcome=proxy-auth http=407 proxy=proxy.contoso.example:8080", c.Route("manage.microsoft.com", "user-proxy"));
    }

    [Theory]
    [InlineData(403, "the proxy answered itself (HTTP 403), so the service was not reached through it")]
    [InlineData(502, "the proxy answered HTTP 502: it could not reach the service")]
    public void A_proxy_that_answers_403_or_502_itself_is_not_proof(int status, string words)
    {
        var c = Run(_ => ProbeAnswer.Failed(RouteOutcome.TimedOut), new FakeRoutes { Browser = new Uri(UserProxy), Answer = _ => Itself(status) });

        Assert.Equal(ConnectionVerdict.NotConfirmed, c.Result.Verdict);
        Assert.Equal("Not confirmed: the device management connection, the work account sign-in connection and the device registration connection. The proxy answered instead of the service.", c.Status);
        var management = c.Card("Device management connection");
        Assert.Equal("Device management connection: not confirmed", management.Title);
        Assert.Contains("Through your proxy (proxy.contoso.example:8080): " + words + ".", management.Detail);
        Assert.Contains("Connection: no answer within 4 seconds.", management.Detail);
        Assert.Equal("Contact your IT support: the proxy may block this connection.", management.NextStep);
        Assert.Contains("outcome=proxy-answered http=" + status + " proxy=proxy.contoso.example:8080", c.Route("manage.microsoft.com", "user-proxy"));
        Assert.EndsWith("outcome=timeout timeout-ms=4000", c.Route("manage.microsoft.com", "direct", "tcp"));
        Assert.Equal("0|local-0a1b2c3d|not-confirmed", c.History);
        Assert.Equal(HealthStatus.Warn, c.Home.Status);
        Assert.Equal("Not confirmed: " + words, c.Home.Detail);
    }

    [Fact]
    public void A_bypassed_machine_proxy_is_noted_and_the_known_contact_is_a_separate_observation()
    {
        var routes = new FakeRoutes { Machine = (new Uri(MachineProxy), "*.microsoft.com;<local>") };
        var result = ConnectivityTest.Run((_, _, _) => ProbeAnswer.Connected, routes, RunId, () => T0);
        Assert.Equal("PROXY run=local-0a1b2c3d user=none machine=set", result.Lines[0]);
        Assert.EndsWith("outcome=skipped reason=bypassed", result.Lines.Single(l => l.Contains("host=manage.microsoft.com") && l.Contains("route=machine-proxy")));
        // The user's proxy is not set, so its route is not mentioned at all.
        Assert.DoesNotContain(result.Lines, l => l.Contains("route=user-proxy"));

        var contact = new ManagementContact { LastSyncUtc = T0.AddMinutes(-2), LastSyncSource = "MDM session log", LastImeCheckInUtc = T0.AddMinutes(-20) };
        var (_, _, cards) = ConnectivityTest.Present(result, contact, TimeZoneInfo.Utc);
        Assert.Contains("Separate observation, not part of this check: last MDM sync 2026-11-12 09:58 (MDM session log); last IME check-in 2026-11-12 09:40 (IME log).", cards[0].Detail);
        Assert.DoesNotContain("Separate observation", cards[1].Detail);
        Assert.Equal("OBSERVATION run=local-0a1b2c3d last-mdm-sync=2026-11-12T09:58:00Z source=\"MDM session log\" last-ime-check-in=2026-11-12T09:40:00Z source=\"IME log\"", ConnectivityTest.ContactLine(RunId, contact));
        Assert.Null(ConnectivityTest.ContactLine(RunId, new ManagementContact()));
    }

    // --- The new outcomes -------------------------------------------------------------------

    [Fact]
    public void A_TLS_failure_says_check_the_clock_then_IT()
    {
        var c = Run(_ => ProbeAnswer.Connected, new FakeRoutes { Answer = _ => HttpsObservation.Failed(RouteOutcome.TlsFailed) });
        Assert.Equal(ConnectionVerdict.SecureConnectionFailed, c.Result.Verdict);
        Assert.Equal(Tone.Error, c.Tone);
        var management = c.Card("Device management connection");
        Assert.Equal(CardKind.Error, management.Kind);
        Assert.Equal("Device management connection: the secure connection failed", management.Title);
        Assert.Contains("Secure request: the secure connection (TLS) failed.", management.Detail);
        Assert.StartsWith("Check that this PC's date and time are right", management.NextStep);
        Assert.StartsWith("The TLS handshake with manage.microsoft.com failed (secure request).", management.Support);
        Assert.EndsWith("outcome=tls-failed", c.Route("manage.microsoft.com", "direct"));
        Assert.Equal("0|local-0a1b2c3d|secure-connection-failed", c.History);
        Assert.Equal(HealthStatus.Fail, c.Home.Status);
        Assert.Equal("The secure connection failed: the secure connection (TLS) failed", c.Home.Detail);
    }

    [Fact]
    public void A_certificate_for_another_name_points_to_the_network_sign_in_then_IT()
    {
        var c = Run(_ => ProbeAnswer.Connected, new FakeRoutes { Answer = _ => WrongHost() });
        Assert.Equal(ConnectionVerdict.SecureConnectionFailed, c.Result.Verdict);
        var management = c.Card("Device management connection");
        Assert.Contains("the certificate that answered is for another name", management.Detail);
        Assert.StartsWith("If you are on a hotel or public Wi-Fi network, sign in to it in a browser first.", management.NextStep);
        Assert.Contains("is for another name: something on the network answers in the service's place", management.Support);
        Assert.EndsWith("outcome=cert-wrong-host cert=wrong-host", c.Route("manage.microsoft.com", "direct"));
    }

    [Fact]
    public void An_untrusted_chain_is_for_IT()
    {
        var c = Run(_ => ProbeAnswer.Connected, new FakeRoutes { Answer = _ => Untrusted() });
        var management = c.Card("Device management connection");
        Assert.Contains("the certificate chain is not trusted on this PC", management.Detail);
        Assert.Equal("Contact your IT support.", management.NextStep);
        Assert.Contains("does not end in a root this PC trusts", management.Support);
        Assert.EndsWith("outcome=cert-untrusted cert=untrusted", c.Route("manage.microsoft.com", "direct"));
        Assert.Equal("The secure connection to the device management connection, the work account sign-in connection and the device registration connection failed. Copy details for IT.", c.Status);
    }

    [Fact]
    public void An_inspecting_proxy_names_its_certificate_authority_in_Copy_for_IT_and_the_log_only()
    {
        var c = Run(_ => ProbeAnswer.Connected, new FakeRoutes { Browser = new Uri(UserProxy), Answer = r => Inspected(r) });
        Assert.Equal(ConnectionVerdict.Inspected, c.Result.Verdict);
        Assert.Equal("The Microsoft services answered, but a proxy on this network looks inside the secure connections. Copy details for IT.", c.Status);
        Assert.Equal(Tone.Warning, c.Tone);
        var management = c.Card("Device management connection");
        Assert.Equal(CardKind.Warning, management.Kind);
        Assert.Equal("Device management connection: answered through an inspecting proxy", management.Title);
        Assert.Contains("through a proxy that inspects the secure connection", management.Detail);
        Assert.StartsWith("Exclude manage.microsoft.com from TLS inspection", management.Support);
        // The organization: in the log and Copy details for IT, never in a card or the status.
        Assert.All(c.Cards, card => Assert.DoesNotContain("Contoso Inspection CA", card.AccessibleText));
        Assert.DoesNotContain("Contoso Inspection CA", c.Status);
        Assert.Contains("issuer-org=\"Contoso Inspection CA\"", c.Route("manage.microsoft.com", "user-proxy"));
        Assert.Contains("cert=inspected", c.Route("manage.microsoft.com", "user-proxy"));
        Assert.Contains("Connections · TLS inspection by the certificate authority of Contoso Inspection CA", c.Ticket);
        Assert.EndsWith("answer=inspected", c.Target("manage.microsoft.com"));
        Assert.Equal("0|local-0a1b2c3d|inspected", c.History);
        Assert.Equal(HealthStatus.Warn, c.Home.Status);
        Assert.Equal("manage.microsoft.com answered through a proxy that looks inside the secure connection", c.Home.Detail);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    public void A_proxy_that_answers_CONNECT_with_a_redirect_answered_itself_and_is_never_offline_or_a_captive_portal(int status)
    {
        // What the bare CONNECT gives the observation (ProxyConnect.Reconcile): the status, no TLS, no redirect host.
        var routes = new FakeRoutes { Browser = new Uri(UserProxy), Answer = _ => Itself(status) };
        var c = Run(_ => ProbeAnswer.Failed(RouteOutcome.Refused), routes);
        Assert.Equal(ConnectionVerdict.NotConfirmed, c.Result.Verdict);
        Assert.Equal("Not confirmed: the device management connection, the work account sign-in connection and the device registration connection. The proxy answered instead of the service.", c.Status);
        var management = c.Card("Device management connection");
        Assert.Equal("Device management connection: not confirmed", management.Title);
        Assert.Contains("the proxy answered itself (HTTP " + status + "), so the service was not reached through it", management.Detail);
        Assert.EndsWith("outcome=proxy-answered http=" + status + " proxy=proxy.contoso.example:8080", c.Route("manage.microsoft.com", "user-proxy"));
        Assert.DoesNotContain(c.Result.Lines, l => l.Contains("redirect-host=") || l.Contains("captive") || l.Contains("dns-failed"));
        Assert.Equal("0|local-0a1b2c3d|not-confirmed", c.History);
        Assert.NotEqual(HealthStatus.Fail, c.Home.Status);
    }

    [Fact]
    public void A_redirect_away_from_Microsoft_after_the_handshake_is_a_captive_portal_and_asks_to_sign_in_to_the_network()
    {
        var away = new HttpsObservation { TlsSeen = true, MicrosoftChain = true, Status = 302, LocationHost = "wifi.contoso.example" };
        var routes = new FakeRoutes { Browser = new Uri(UserProxy), Answer = _ => away };
        var c = Run(_ => ProbeAnswer.Failed(RouteOutcome.Refused), routes);
        Assert.Equal(ConnectionVerdict.CaptivePortal, c.Result.Verdict);
        Assert.Equal(Tone.Error, c.Tone);
        Assert.Equal("This network shows its own sign-in page instead of the Microsoft services. Sign in to the network in a browser, then check again.", c.Status);
        var management = c.Card("Device management connection");
        Assert.Equal(CardKind.Error, management.Kind);
        Assert.Equal("Device management connection: the network's sign-in page answered instead", management.Title);
        Assert.Contains("the network answered with its own page (a redirect to wifi.contoso.example)", management.Detail);
        Assert.StartsWith("Open a web page in your browser and sign in to the network", management.NextStep);
        Assert.Contains("outcome=captive-portal http=302 proxy=proxy.contoso.example:8080 redirect-host=wifi.contoso.example", c.Route("manage.microsoft.com", "user-proxy"));
        Assert.Equal("0|local-0a1b2c3d|captive-portal", c.History);
        Assert.Equal(HealthStatus.Fail, c.Home.Status);
    }

    [Fact]
    public void A_redirect_within_Microsoft_is_the_service_answering_and_is_never_followed()
    {
        var target = ConnectivityTest.Management;
        var own = new HttpsObservation { TlsSeen = true, MicrosoftChain = true, Status = 302, LocationHost = "portal.manage.microsoft.com" };
        Assert.Equal(RouteOutcome.Answered, HttpsRules.Classify(own, target, viaProxy: false).Outcome);
        var away = new HttpsObservation { TlsSeen = true, MicrosoftChain = true, Status = 302, LocationHost = "login.contoso.example" };
        Assert.Equal(RouteOutcome.CaptivePortal, HttpsRules.Classify(away, target, viaProxy: false).Outcome);
        Assert.True(MicrosoftChains.IsMicrosoftHost("login.microsoftonline.com"));
        Assert.True(MicrosoftChains.IsMicrosoftHost("outlook.office.com"));
        Assert.False(MicrosoftChains.IsMicrosoftHost("microsoft.com.contoso.example"));
        Assert.False(MicrosoftChains.IsMicrosoftHost("notmicrosoft.com"));
    }

    [Fact]
    public void An_HTML_page_where_the_sign_in_document_was_expected_is_a_captive_portal()
    {
        var html = new HttpsObservation { TlsSeen = true, MicrosoftChain = true, Status = 200, ContentType = "text/html; charset=utf-8", Body = "<html><form>Sign in to the guest network</form></html>" };
        var c = Run(_ => ProbeAnswer.Connected, new FakeRoutes { Answer = r => r.Get ? html : Ok(r) });
        Assert.Equal(ConnectionVerdict.CaptivePortal, c.Result.Verdict);
        Assert.Equal("Work account sign-in connection: the network's sign-in page answered instead", c.Card("Work account sign-in").Title);
        Assert.Contains("the network answered with its own page instead of the service", c.Card("Work account sign-in").Detail);
        // The page's text is never written anywhere.
        Assert.DoesNotContain(c.Result.Lines, l => l.Contains("guest network"));
        Assert.DoesNotContain("guest network", c.Ticket);
    }

    [Fact]
    public void A_time_out_after_the_connection_opened_is_not_confirmed_and_says_the_route_exists()
    {
        var c = Run(_ => ProbeAnswer.Connected, new FakeRoutes { Answer = _ => HttpsObservation.Failed(RouteOutcome.TimedOut) });
        Assert.Equal(ConnectionVerdict.NotConfirmed, c.Result.Verdict);
        Assert.Equal("Not confirmed: the device management connection, the work account sign-in connection and the device registration connection. A connection opened, but the service did not answer.", c.Status);
        var management = c.Card("Device management connection");
        Assert.Contains("Secure request: no answer within 6 seconds.", management.Detail);
        Assert.Equal("Check again in a few minutes. If it stays, contact your IT support.", management.NextStep);
        Assert.Contains("A connection opened, so the route exists", management.Support);
        Assert.EndsWith("outcome=timeout timeout-ms=6000", c.Route("manage.microsoft.com", "direct"));
    }

    // --- The verdict's other rules ----------------------------------------------------

    [Fact]
    public void Only_download_optimization_failing_keeps_management_ok_and_claims_nothing_about_downloads()
    {
        var c = Run(host => host.StartsWith("geo.", StringComparison.Ordinal) ? ProbeAnswer.Failed(RouteOutcome.Refused) : ProbeAnswer.Connected,
            new FakeRoutes { Answer = r => r.Host.StartsWith("geo.", StringComparison.Ordinal) ? HttpsObservation.Failed(RouteOutcome.Refused) : Ok(r) });

        Assert.Equal(ConnectionVerdict.DownloadOptimizationNotReachable, c.Result.Verdict);
        Assert.Equal("This PC couldn't reach the download optimization service. The app download itself wasn't tested.", c.Status);
        Assert.Equal(Tone.Warning, c.Tone);
        Assert.Equal(CardKind.Ok, c.Card("Device management connection").Kind);
        Assert.Equal(CardKind.Info, c.Card("App and update download optimization").Kind);
        Assert.Equal("App and update download optimization: not reachable", c.Card("App and update download optimization").Title);
        foreach (var text in c.Cards.Select(card => card.AccessibleText).Append(c.Status).Append(c.Ticket))
        {
            Assert.DoesNotContain("peers", text);
            Assert.DoesNotContain("cannot use", text);
        }
        Assert.Equal("0|local-0a1b2c3d|download-optimization-not-reachable", c.History);
        Assert.Equal(HealthStatus.Pass, c.Home.Status);
    }

    [Fact]
    public void Download_optimization_inspected_or_captive_alone_never_turns_the_result_red()
    {
        var c = Run(_ => ProbeAnswer.Connected, new FakeRoutes { Answer = r => r.Host.StartsWith("geo.", StringComparison.Ordinal) ? Itself(302, "wifi.contoso.example") : Ok(r) });
        Assert.Equal(ConnectionVerdict.DownloadOptimizationNotReachable, c.Result.Verdict);
        Assert.Equal(Tone.Warning, c.Tone);
        Assert.Equal(CardKind.Info, c.Card("App and update download optimization").Kind);
    }

    [Fact]
    public void Outlook_not_answering_while_Intune_answers()
    {
        var c = Run(host => host == "outlook.office.com" ? ProbeAnswer.Failed(RouteOutcome.NameNotResolved) : ProbeAnswer.Connected,
            new FakeRoutes { Answer = r => r.Host == "outlook.office.com" ? HttpsObservation.Failed(RouteOutcome.NameNotResolved) : Ok(r) });
        Assert.Equal(ConnectionVerdict.OutlookNotAnswered, c.Result.Verdict);
        Assert.Equal("The device management services answered; Outlook on the web did not.", c.Status);
        var outlook = c.Card("Outlook on the web connection");
        Assert.Equal(CardKind.Warning, outlook.Kind);
        Assert.Equal("Outlook on the web connection: not reachable", outlook.Title);
        Assert.Equal("0|local-0a1b2c3d|outlook-not-answered", c.History);
        Assert.Equal(HealthStatus.Pass, c.Home.Status);
    }

    [Fact]
    public void Fully_offline_is_an_honest_problem_without_blaming_Microsoft()
    {
        var c = Run(_ => ProbeAnswer.Failed(RouteOutcome.NameNotResolved), new FakeRoutes { Answer = _ => HttpsObservation.Failed(RouteOutcome.NameNotResolved) });

        Assert.Equal(ConnectionVerdict.Offline, c.Result.Verdict);
        Assert.Equal(3, c.Result.RequiredFailed);
        Assert.Equal("This PC couldn't reach any Microsoft connection. Check the network connection, Wi-Fi or VPN.", c.Status);
        Assert.Equal(Tone.Error, c.Tone);
        Assert.Equal("Device management connection: not reachable", c.Card("Device management connection").Title);
        foreach (var text in c.Cards.Select(card => card.AccessibleText).Append(c.Status).Append(c.Ticket))
        {
            Assert.DoesNotContain("is down", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("status page", text, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal("Next · User: Check that this PC is connected to the network and, if your organization uses one, to the VPN. Then check again. IT support: Check the firewall, proxy and name resolution for manage.microsoft.com on port 443.", Line(c.Ticket, "Next"));
        Assert.Equal("RESULT run=local-0a1b2c3d verdict=offline required-not-reachable=3", c.Result.Lines.Last());
        Assert.Equal("3|local-0a1b2c3d|offline", c.History);
        Assert.Equal(HealthStatus.Fail, c.Home.Status);
        Assert.Equal("Not reachable: the name could not be resolved. Check the network or VPN", c.Home.Detail);
    }

    [Fact]
    public void Old_history_lines_still_read_and_two_outcomes_with_the_same_number_differ()
    {
        Assert.Equal("2026-10-15 14:02 +02:00  Check Microsoft connections: all required endpoints reachable (0)", RunWords.HistoryLine("2026-10-15 14:02:11 +02:00;user;ConnTest;0"));
        // An older line keeps its words.
        Assert.EndsWith("all connections reachable (0, reference local-00000001)", RunWords.HistoryLine("2026-11-12 11:00:00 +01:00;user;ConnTest;0|local-00000001|all-reachable"));
        Assert.EndsWith("Outlook on the web not reachable (0, reference local-00000004)", RunWords.HistoryLine("2026-11-12 11:00:00 +01:00;user;ConnTest;0|local-00000004|outlook-not-reachable"));
        var b = RunWords.HistoryLine("2026-11-12 11:00:00 +01:00;user;ConnTest;0|local-00000002|captive-portal");
        Assert.EndsWith("the network's sign-in page answered instead (0, reference local-00000002)", b);
        Assert.Equal("2026-11-12 11:00 +01:00  Check Microsoft connections: 0|local-00000003|later-word", RunWords.HistoryLine("2026-11-12 11:00:00 +01:00;user;ConnTest;0|local-00000003|later-word"));
    }

    // --- The rules without the check --------------------------------------------------------------

    [Fact]
    public void The_HTTPS_rules_tell_every_outcome_apart()
    {
        var m = ConnectivityTest.Management;
        ProbeAnswer Of(HttpsObservation o, bool proxy = false) => HttpsRules.Classify(o, m, proxy);
        Assert.Equal(RouteOutcome.Answered, Of(new HttpsObservation { TlsSeen = true, MicrosoftChain = true, Status = 401 }).Outcome);
        Assert.Equal(RouteOutcome.Inspected, Of(new HttpsObservation { TlsSeen = true, MicrosoftChain = false, IssuerOrganization = "Contoso\r\nInspection \"CA\"", Status = 200 }).Outcome);
        Assert.Equal("ContosoInspection CA", Of(new HttpsObservation { TlsSeen = true, MicrosoftChain = false, IssuerOrganization = "Contoso\r\nInspection \"CA\"", Status = 200 }).IssuerOrganization);
        Assert.Equal(RouteOutcome.ProxyAuthRequired, Of(Itself(407), proxy: true).Outcome);
        Assert.Equal(RouteOutcome.ProxyAnswered, Of(Itself(403), proxy: true).Outcome);
        Assert.Equal(RouteOutcome.ProxyAnswered, Of(new HttpsObservation { TlsSeen = true, MicrosoftChain = true, Status = 502 }, proxy: true).Outcome);
        Assert.Equal(RouteOutcome.ProxyAnswered, Of(Itself(302, "wifi.contoso.example"), proxy: true).Outcome);
        Assert.Equal(302, Of(Itself(302, "wifi.contoso.example"), proxy: true).HttpStatus);
        Assert.Equal("", Of(Itself(302, "wifi.contoso.example"), proxy: true).RedirectHost);
        Assert.Equal(RouteOutcome.CertificateWrongHost, Of(WrongHost()).Outcome);
        Assert.Equal(RouteOutcome.CertificateUntrusted, Of(Untrusted()).Outcome);
        Assert.Equal(RouteOutcome.TlsFailed, Of(HttpsObservation.Failed(RouteOutcome.TlsFailed)).Outcome);
        Assert.Equal(RouteOutcome.TimedOut, Of(HttpsObservation.Failed(RouteOutcome.TimedOut)).Outcome);
        Assert.Equal(RouteOutcome.Unknown, Of(null).Outcome);

        Assert.Equal(RouteOutcome.NameNotResolved, RouteRules.FromSocketError(SocketError.HostNotFound));
        Assert.Equal(RouteOutcome.Refused, RouteRules.FromSocketError(SocketError.ConnectionRefused));
        Assert.Equal(RouteOutcome.TimedOut, RouteRules.FromSocketError(SocketError.TimedOut));
        Assert.Equal(RouteOutcome.TlsFailed, RouteRules.FromWebException(new System.Net.WebException("x", System.Net.WebExceptionStatus.TrustFailure)));
        Assert.Equal(RouteOutcome.NameNotResolved, RouteRules.FromWebException(new System.Net.WebException("x", System.Net.WebExceptionStatus.ProxyNameResolutionFailure)));
    }

    [Fact]
    public void A_Microsoft_chain_is_known_by_its_root_thumbprint_or_its_publisher()
    {
        Assert.True(MicrosoftChains.IsMicrosoftRoot("DF3C24F9BFD666761B268073FE06D1CC8D4F82A4", "Contoso"));
        Assert.True(MicrosoftChains.IsMicrosoftRoot("73a5e64a3bff8316ff0edccc618a906e4eae4d74", ""));
        Assert.True(MicrosoftChains.IsMicrosoftRoot("0000000000000000000000000000000000000000", "Microsoft Corporation"));
        Assert.True(MicrosoftChains.IsMicrosoftRoot(null, "DigiCert Inc"));
        Assert.False(MicrosoftChains.IsMicrosoftRoot("0000000000000000000000000000000000000000", "Contoso Inspection CA"));
        Assert.Equal(7, MicrosoftChains.RootThumbprints.Count);

        // The organization of a certificate: its O, else its common name.
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Contoso Root, O=Contoso Inspection Ltd, C=NL", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var withOrg = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.Equal("Contoso Inspection Ltd", SystemProxyRoutes.OrganizationOf(withOrg));
        var plain = new CertificateRequest("CN=Contoso Plain Root", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var withoutOrg = plain.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.Equal("Contoso Plain Root", SystemProxyRoutes.OrganizationOf(withoutOrg));
        Assert.Equal("", SystemProxyRoutes.OrganizationOf(null));
    }

    [Fact]
    public void Copy_for_IT_carries_a_step_from_an_OK_or_Info_card_and_never_points_to_a_missing_log()
    {
        var check = new LocalRun { Name = "Clear Teams cache", RunId = RunId, StartedUtc = T0, EndedUtc = T0.AddSeconds(5) };
        var header = new RunLogHeader { Check = check, AppVersion = "1.2.3", Zone = TimeZoneInfo.Utc, NowUtc = T0.AddMinutes(1) };
        var plain = new[] { ResultCard.From(FindingSeverity.Ok, "Teams cache cleared") };
        Assert.Contains("Next · None needed.", RunTicket.Text(header, plain, "Done.", Tone.Success));
        var withStep = new[] { ResultCard.From(FindingSeverity.Ok, "Teams cache cleared", "", "Start Teams again.") };
        Assert.Contains("Next · User: Start Teams again.", RunTicket.Text(header, withStep, "Done.", Tone.Success));
        var neutral = RunTicket.Text(header, plain, "Cancelled.", Tone.Neutral, logSaved: false);
        Assert.Contains("Next · IT support: no run log of this attempt was saved on this PC; this text is its record.", neutral);
        Assert.Null(ConnectivityTest.TicketLine(new ConnectivityResult()));
    }
}
