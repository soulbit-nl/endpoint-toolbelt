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
/// The check for a newer version: the feed is read strictly (three-number version, a date, release
/// notes over https on the publisher's site or below the product's own repository), the request
/// follows no redirect and gives no sign-in to anyone but the proxy, the app looks at most once a
/// day, the organization can switch it off, and the app only says that a newer version exists: it
/// never downloads or installs anything.
/// </summary>
public class UpdateCheckTests
{
    private const string Good = "{\"version\":\"1.1.2\",\"released\":\"2026-12-01\",\"notes\":\"https://github.com/soulbit-nl/endpoint-toolbelt/releases/tag/v1.1.2\"}";

    [Fact]
    public void A_feed_with_the_three_fields_in_their_form_is_an_offer()
    {
        var offer = UpdateCheck.Parse(Good);
        Assert.NotNull(offer);
        Assert.Equal("1.1.2", offer.Version);
        Assert.Equal("2026-12-01", offer.Released);
        Assert.Equal("https://github.com/soulbit-nl/endpoint-toolbelt/releases/tag/v1.1.2", offer.Notes);
        Assert.NotNull(UpdateCheck.Parse("{\"version\":\"1.1.2\",\"released\":\"2026-12-01\",\"notes\":\"https://soulbit.app/go/endpoint-toolbelt\",\"extra\":1}"));
    }

    [Theory]
    [InlineData("{\"version\":\"1.1\",\"released\":\"2026-12-01\",\"notes\":\"https://soulbit.app/x\"}")]
    [InlineData("{\"version\":\"1.1.2-beta\",\"released\":\"2026-12-01\",\"notes\":\"https://soulbit.app/x\"}")]
    [InlineData("{\"version\":\"١.١.٢\",\"released\":\"2026-12-01\",\"notes\":\"https://soulbit.app/x\"}")]
    [InlineData("{\"version\":\"1.1.2\",\"released\":\"last week\",\"notes\":\"https://soulbit.app/x\"}")]
    [InlineData("{\"version\":\"1.1.2\",\"released\":\"2026-12-01\",\"notes\":\"http://soulbit.app/x\"}")]
    [InlineData("{\"version\":\"1.1.2\",\"released\":\"2026-12-01\",\"notes\":\"https://contoso.example/x\"}")]
    [InlineData("{\"version\":\"1.1.2\",\"released\":\"2026-12-01\",\"notes\":\"https://github.com/contoso/other/releases\"}")]
    [InlineData("{\"version\":\"1.1.2\",\"released\":\"2026-12-01\",\"notes\":\"https://github.com/soulbit-nl/endpoint-toolbelt-other/releases\"}")]
    [InlineData("{\"version\":\"1.1.2\",\"released\":\"2026-12-01\",\"notes\":\"https://github.com/soulbit-nl/endpoint-toolbelt/%2e%2e/%2e%2e/contoso/other\"}")]
    [InlineData("{\"version\":\"1.1.2\",\"released\":\"2026-12-01\",\"notes\":\"https://github.com/soulbit-nl/endpoint-toolbelt/../../contoso/other\"}")]
    [InlineData("{\"version\":\"1.1.2\",\"released\":\"2026-12-01\",\"notes\":\"https://github.com/soulbit-nl/endpoint-toolbelt/releases%2Fx\"}")]
    [InlineData("{\"version\":\"1.1.2\",\"released\":\"2026-12-01\",\"notes\":\"https://:@soulbit.app/x\"}")]
    [InlineData("{\"version\":\"1.1.2\",\"released\":\"2026-12-01\",\"notes\":\"https://soulbit.app:8443/x\"}")]
    [InlineData("{\"version\":\"1.1.2\",\"released\":\"2026-12-01\",\"notes\":\"javascript:alert(1)\"}")]
    [InlineData("{\"version\":\"1.1.2\",\"released\":\"2026-12-01\"}")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData(null)]
    public void A_feed_that_is_not_exactly_in_form_is_no_offer(string json)
        => Assert.Null(UpdateCheck.Parse(json));

    [Fact]
    public void A_feed_longer_than_the_limit_is_no_offer()
        => Assert.Null(UpdateCheck.Parse(Good.Replace("}", ",\"pad\":\"" + new string('x', UpdateCheck.MaxBytes) + "\"}")));

    [Theory]
    [InlineData("1.0.1", "1.1.2", true)]
    [InlineData("1.0.1.0", "1.1.2", true)]
    [InlineData("1.1.2", "1.1.2", false)]
    [InlineData("1.2.0", "1.1.2", false)]
    [InlineData("1.1.9", "1.1.10", true)]
    [InlineData("development", "1.1.2", false)]
    public void Only_a_newer_version_is_offered(string running, string offered, bool newer)
        => Assert.Equal(newer, UpdateCheck.IsNewer(running, new UpdateOffer { Version = offered }));

    [Fact]
    public void No_offer_is_never_newer()
        => Assert.False(UpdateCheck.IsNewer("1.0.1", null));

    [Fact]
    public void The_app_looks_at_most_once_a_day_and_again_after_a_clock_change()
    {
        var now = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(UpdateCheck.Due(null, now));
        Assert.False(UpdateCheck.Due(now.AddHours(-23), now));
        Assert.True(UpdateCheck.Due(now.AddHours(-24), now));
        Assert.True(UpdateCheck.Due(now.AddHours(1), now));
    }

    [Fact]
    public void The_organization_switches_the_check_off_with_zero()
    {
        Assert.True(UpdateCheck.Enabled((object)null));
        Assert.True(UpdateCheck.Enabled(1));
        Assert.False(UpdateCheck.Enabled(0));
        Assert.False(UpdateCheck.Enabled(" 0 "));
        Assert.True(UpdateCheck.Enabled("1"));
        Assert.Equal("UpdateCheck", UpdateCheck.PolicyValue);
    }

    [Theory]
    // Policy, then the user's own choice, then the device's value from the service (on by default
    // there); a service that did not answer (null) is no request, unless the user switched it on.
    [InlineData(null, null, true, true, false)]
    [InlineData(null, null, false, false, false)]
    [InlineData(null, null, null, false, false)]
    [InlineData(null, true, false, true, false)]
    [InlineData(null, true, null, true, false)]
    [InlineData(null, false, true, false, false)]
    [InlineData(0, true, true, false, true)]
    [InlineData("0", null, true, false, true)]
    [InlineData(1, false, true, false, false)]
    public void Policy_then_the_user_then_the_device_s_value_decide_and_off_means_no_request(object policy, bool? user, bool? device, bool on, bool locked)
        => Assert.Equal((on, locked), UpdateCheck.Decide(policy, user, device));

    [Theory]
    // About offers the check box unless a policy locks it, or the device's value is unknown and the user chose nothing.
    [InlineData(false, null, true, true)]
    [InlineData(false, null, false, true)]
    [InlineData(false, null, null, false)]
    [InlineData(false, true, null, true)]
    [InlineData(false, false, null, true)]
    [InlineData(true, true, true, false)]
    public void About_offers_the_check_box_only_when_the_choice_can_be_made_knowingly(bool locked, bool? user, bool? device, bool offered)
        => Assert.Equal(offered, UpdateCheck.OfferChoice(locked, user, device));

    [Fact]
    public void The_notice_says_only_that_a_version_is_available_and_links_the_product_s_page()
    {
        var offer = UpdateCheck.Parse(Good);
        Assert.Equal("Endpoint Toolbelt 1.1.2 is available.", UpdateCheck.NoticeLine(offer));
        Assert.Equal("Check for new versions at start-up", UpdateCheck.CheckOption);

        // The notice's one link is fixed in the code and goes to the publisher's site.
        var notes = new Uri(UpdateCheck.ReleaseNotesUrl);
        Assert.Equal(Uri.UriSchemeHttps, notes.Scheme);
        Assert.Equal("soulbit.app", notes.Host);
        // About names the newer version, without saying who installs it; without one it stays as it is.
        Assert.Equal("Version 1.1.2 is available (released 2026-12-01).", UpdateCheck.AboutLine(offer));
        Assert.Contains("version 1.0.1" + Environment.NewLine + "Version 1.1.2 is available (released 2026-12-01).", EndpointToolbelt.App.Ui.MainWindow.AboutBody("1.0.1", offer));
        Assert.DoesNotContain("is available", EndpointToolbelt.App.Ui.MainWindow.AboutBody("1.0.1"));
    }

    [Fact]
    public void Check_now_in_About_names_the_newer_version_the_newest_or_that_it_could_not_read()
    {
        var offer = UpdateCheck.Parse(Good);
        Assert.Equal("Check _now", UpdateCheck.CheckNowButton);
        Assert.StartsWith("Version 1.1.2 is available (released ", UpdateCheck.CheckNowResult("1.0.1.0", offer));
        Assert.Equal("You have the newest version (1.1.2).", UpdateCheck.CheckNowResult("1.1.2.0", offer));
        Assert.Equal("You have the newest version (1.2.0).", UpdateCheck.CheckNowResult("1.2.0+abc", offer));
        Assert.Equal("The newest version could not be read. Check the connection and try again.", UpdateCheck.CheckNowResult("1.0.1.0", null));
    }

    [Fact]
    public void The_start_up_notice_shows_once_per_version()
    {
        var offer = new UpdateOffer { Version = "1.1.2" };
        Assert.True(UpdateCheck.NoticeDue(offer, null));
        Assert.True(UpdateCheck.NoticeDue(offer, "1.1.1"));
        Assert.False(UpdateCheck.NoticeDue(offer, "1.1.2"));
        Assert.False(UpdateCheck.NoticeDue(null, null));
    }

    [Fact]
    public void The_feed_is_https_on_the_publisher_s_site()
    {
        var feed = new Uri(UpdateCheck.FeedUrl);
        Assert.Equal(Uri.UriSchemeHttps, feed.Scheme);
        Assert.Equal("soulbit.app", feed.Host);
    }

    [Fact]
    public void The_check_never_downloads_installs_or_starts_anything()
    {
        var source = File.ReadAllText(Path.Combine(EngineHarness.RepositoryRoot(), "src", "EndpointToolbelt.App", "Core", "UpdateCheck.cs"));
        foreach (var never in new[] { "msiexec", "Process.Start", "DownloadFile", "WriteAllBytes", "File.Create", "Environment.MachineName", "Environment.UserName", "CredentialCache.DefaultCredentials", "DefaultNetworkCredentials", "WebRequest.DefaultWebProxy", "AllowAutoRedirect = true" })
        {
            Assert.DoesNotContain(never, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    // --- The request, against a server on the loopback address --------------------------------------

    /// <summary>
    /// A listener on the loopback address that answers every request with one fixed answer, and keeps
    /// each request's first line plus the name of any sign-in header.
    /// </summary>
    private sealed class LoopbackServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly byte[] _answer;

        public List<string> Requests { get; } = new();

        public List<string> SignIns { get; } = new();

        public Uri Address { get; }

        public LoopbackServer(int status, string body, string extraHeaders = "")
        {
            var content = Encoding.UTF8.GetBytes(body ?? "");
            var head = "HTTP/1.1 " + status + " Test\r\nContent-Type: application/json\r\n" + extraHeaders + "Content-Length: " + content.Length + "\r\nConnection: close\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(head);
            _answer = new byte[bytes.Length + content.Length];
            Buffer.BlockCopy(bytes, 0, _answer, 0, bytes.Length);
            Buffer.BlockCopy(content, 0, _answer, bytes.Length, content.Length);
            _listener.Start();
            Address = new Uri("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/endpoint-toolbelt/latest.json");
            new Thread(Accept) { IsBackground = true, Name = "loopback feed" }.Start();
        }

        private void Accept()
        {
            try
            {
                while (true)
                {
                    var client = _listener.AcceptTcpClient();
                    new Thread(() => Serve(client)) { IsBackground = true, Name = "loopback feed connection" }.Start();
                }
            }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException or InvalidOperationException) { }
        }

        private void Serve(TcpClient client)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    stream.ReadTimeout = 5000;
                    var buffer = new byte[8192];
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) { return; }
                    var lines = Encoding.ASCII.GetString(buffer, 0, read).Split(new[] { "\r\n" }, StringSplitOptions.None);
                    lock (Requests)
                    {
                        Requests.Add(lines[0]);
                        foreach (var line in lines)
                        {
                            var colon = line.IndexOf(':');
                            if (colon > 0 && line.Substring(0, colon).EndsWith("Authorization", StringComparison.OrdinalIgnoreCase)) { SignIns.Add(line.Substring(0, colon)); }
                        }
                    }
                    stream.Write(_answer, 0, _answer.Length);
                }
            }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException or InvalidOperationException) { }
        }

        public void Dispose() => _listener.Stop();
    }

    [Fact]
    public void A_feed_that_answers_200_with_the_feed_is_an_offer()
    {
        using var server = new LoopbackServer(200, Good);
        var offer = UpdateCheck.Fetch(server.Address, null);
        Assert.NotNull(offer);
        Assert.Equal("1.1.2", offer.Version);
        lock (server.Requests) { Assert.Equal("GET /endpoint-toolbelt/latest.json HTTP/1.1", Assert.Single(server.Requests)); }
    }

    [Fact]
    public void A_redirect_is_never_followed_and_is_no_offer()
    {
        using var elsewhere = new LoopbackServer(200, Good);
        using var server = new LoopbackServer(302, "", "Location: " + elsewhere.Address + "\r\n");
        Assert.Null(UpdateCheck.Fetch(server.Address, null));
        lock (elsewhere.Requests) { Assert.Empty(elsewhere.Requests); }
    }

    [Theory]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(204)]
    public void An_answer_other_than_200_is_no_offer(int status)
    {
        using var server = new LoopbackServer(status, status == 204 ? "" : Good);
        Assert.Null(UpdateCheck.Fetch(server.Address, null));
    }

    [Fact]
    public void An_answer_over_the_limit_is_no_offer()
    {
        using var server = new LoopbackServer(200, Good.Replace("}", ",\"pad\":\"" + new string('x', UpdateCheck.MaxBytes) + "\"}"));
        Assert.Null(UpdateCheck.Fetch(server.Address, null));
    }

    [Theory]
    [InlineData("NTLM")]
    [InlineData("Negotiate")]
    public void A_server_that_asks_for_a_windows_sign_in_gets_none(string scheme)
    {
        using var server = new LoopbackServer(401, "", "WWW-Authenticate: " + scheme + "\r\n");
        Assert.Null(UpdateCheck.Fetch(server.Address, null));
        lock (server.Requests) { Assert.NotEmpty(server.Requests); Assert.Empty(server.SignIns); }
    }

    [Theory]
    [InlineData("NTLM")]
    [InlineData("Negotiate")]
    public void Through_a_proxy_whose_redirect_asks_for_a_sign_in_nobody_gets_one(string scheme)
    {
        // .NET follows a proxy's redirect to CONNECT by itself; the redirect's host then asks for a sign-in.
        using var target = new LoopbackServer(401, "", "WWW-Authenticate: " + scheme + "\r\nProxy-Authenticate: " + scheme + "\r\n");
        using var proxy = new LoopbackServer(302, "", "Location: " + target.Address + "\r\n");
        var proxyAddress = new Uri("http://127.0.0.1:" + proxy.Address.Port + "/");

        Assert.Null(UpdateCheck.Fetch(new Uri(UpdateCheck.FeedUrl), proxyAddress));

        lock (proxy.Requests) { Assert.NotEmpty(proxy.Requests); Assert.Empty(proxy.SignIns); }
        lock (target.Requests) { Assert.Empty(target.SignIns); }
    }

    [Fact]
    public void A_feed_that_is_not_https_or_loopback_is_never_requested()
        => Assert.Null(UpdateCheck.Fetch(new Uri("http://soulbit.app/endpoint-toolbelt/latest.json"), null));
}
