using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace EndpointToolbelt.App.Core;

/// <summary>A newer version the publisher offers, as its feed names it.</summary>
[DataContract]
public sealed class UpdateOffer
{
    /// <summary>"1.1.2": three numbers.</summary>
    [DataMember(Name = "version")] public string Version { get; set; } = "";

    /// <summary>The release date, "yyyy-MM-dd".</summary>
    [DataMember(Name = "released")] public string Released { get; set; } = "";

    /// <summary>The release notes: an https address on the publisher's site or below the product's own repository.</summary>
    [DataMember(Name = "notes")] public string Notes { get; set; } = "";
}

/// <summary>
/// The check for a newer version: at most once a day the app (as the user, never the service)
/// reads one small file of the publisher's site, <see cref="FeedUrl"/>, which names the newest
/// version, its release date and its release notes. The request carries no data about the device or
/// the user; the site sees what every web server sees (the internet address and the time). About
/// names a newer version, and once per version a small notice at start-up says it, with one link to
/// the product's page on the publisher's site. The user turns the check off in About, the installer
/// for the device (its first screen's check box, or the MSI property UPDATECHECK=0), an organization
/// with the policy value; off means no request at all. The app never downloads or
/// installs anything itself. An organization switches the check off with the policy value
/// <see cref="PolicyValue"/> (0), which locks it.
/// </summary>
public static class UpdateCheck
{
    /// <summary>The feed: one JSON object, <c>{"version":"1.1.2","released":"2026-12-01","notes":"https://..."}</c>.</summary>
    public const string FeedUrl = "https://soulbit.app/endpoint-toolbelt/latest.json";

    /// <summary>The policy value (REG_DWORD under <see cref="ProductInfo.PolicyKey"/>): 0 switches the check off; missing or any other value leaves it on.</summary>
    public const string PolicyValue = "UpdateCheck";

    /// <summary>How often the app looks.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>The longest wait for the feed.</summary>
    public const int TimeoutMilliseconds = 5000;

    /// <summary>A longer answer is no feed.</summary>
    public const int MaxBytes = 16 * 1024;

    // ASCII digits only: \d would also take digits of other scripts.
    private static readonly Regex VersionPattern = new("^[0-9]{1,4}\\.[0-9]{1,4}\\.[0-9]{1,4}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly char[] VersionEnds = { '+', '-', ' ' };

    /// <summary>
    /// The offer in a feed, or null when the feed is not exactly what it should be: a version of
    /// three numbers, a release date, and release notes over https on the publisher's site or below
    /// the product's own repository. Nothing of a feed that does not pass reaches the window.
    /// </summary>
    public static UpdateOffer Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxBytes) { return null; }
        UpdateOffer offer;
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            offer = new DataContractJsonSerializer(typeof(UpdateOffer)).ReadObject(stream) as UpdateOffer;
        }
        catch (Exception ex) when (ex is SerializationException or ArgumentException or InvalidCastException)
        {
            return null;
        }
        return Clean(offer);
    }

    /// <summary>The offer when every field has its form (see <see cref="Parse"/>); otherwise null.</summary>
    public static UpdateOffer Clean(UpdateOffer offer)
    {
        if (offer == null) { return null; }
        var version = (offer.Version ?? "").Trim();
        var released = (offer.Released ?? "").Trim();
        var notes = (offer.Notes ?? "").Trim();
        if (!VersionPattern.IsMatch(version)) { return null; }
        if (!DateTime.TryParseExact(released, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) { return null; }
        return NotesAllowed(notes) ? new UpdateOffer { Version = version, Released = released, Notes = new Uri(notes).AbsoluteUri } : null;
    }

    /// <summary>
    /// Release notes over https, without a port or user part, on the publisher's site or below the
    /// product's own repository. A link with a "%" or a "\" is refused: an encoded dot segment
    /// ("%2e%2e") passes a prefix check, and a browser reads it as "..".
    /// </summary>
    private static bool NotesAllowed(string notes)
    {
        if (notes.IndexOf('%') >= 0 || notes.IndexOf('\\') >= 0) { return false; }
        if (!Uri.TryCreate(notes, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || !uri.IsDefaultPort) { return false; }
        var source = new Uri(ProductInfo.SourceUrl);
        if (string.Equals(uri.Host, new Uri(FeedUrl).Host, StringComparison.OrdinalIgnoreCase)) { return true; }
        return string.Equals(uri.Host, source.Host, StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith(source.AbsolutePath.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the offered version is newer than the running one.</summary>
    public static bool IsNewer(string running, UpdateOffer offer)
        => offer != null && System.Version.TryParse(offer.Version, out var offered) && System.Version.TryParse(Core(running), out var current) && offered > current;

    // "1.0.1" from "1.0.1", "1.0.1.0" or "1.0.1+abc".
    private static string Core(string version)
    {
        var text = (version ?? "").Trim();
        var end = text.IndexOfAny(VersionEnds);
        if (end >= 0) { text = text.Substring(0, end); }
        var parts = text.Split('.');
        return parts.Length >= 3 ? parts[0] + "." + parts[1] + "." + parts[2] : text;
    }

    /// <summary>True when the last look is a day or more ago, in the future (a clock that was changed), or never happened.</summary>
    public static bool Due(DateTime? lastUtc, DateTime nowUtc)
        => lastUtc is not DateTime last || last > nowUtc || nowUtc - last >= Interval;

    /// <summary>
    /// Whether the app looks for a newer version, and whether the user may change it. "Off" means
    /// no request at all. The organization's policy decides first and locks the choice (a policy key
    /// that cannot be read counts as off and locked); then the user's own choice in About; then the
    /// device's value, which the service reads (the installer's
    /// UPDATECHECK under the Config key, which only SYSTEM and Administrators can read) and sends in
    /// its status. Without the service's answer and without a choice of the user, no request.
    /// </summary>
    public static (bool On, bool Locked) Setting(bool? userChoice, bool? device)
    {
        object policy;
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(ProductInfo.PolicyKey, writable: false);
            policy = key?.GetValue(PolicyValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return (false, true);
        }
        return Decide(policy, userChoice, device);
    }

    /// <summary>
    /// The rule of <see cref="Setting"/> on values already read: policy, then the user, then the
    /// device's value from the service (unknown is off: an installer's "0" is never overruled by a
    /// service that did not answer).
    /// </summary>
    public static (bool On, bool Locked) Decide(object policy, bool? userChoice, bool? device)
    {
        if (!Enabled(policy)) { return (false, true); }
        if (userChoice is bool chosen) { return (chosen, false); }
        return (device == true, false);
    }

    /// <summary>
    /// Whether About offers the check box: not when a policy locks the check, and not while the
    /// device's value is unknown and the user has chosen nothing (a tick would outrank an installer's
    /// "0" the app cannot see).
    /// </summary>
    public static bool OfferChoice(bool locked, bool? userChoice, bool? device)
        => !locked && (userChoice.HasValue || device.HasValue);

    /// <summary>The check is on unless the value is the number 0 (or the text "0").</summary>
    public static bool Enabled(object value) => value switch
    {
        int number => number != 0,
        string text => text.Trim() != "0",
        _ => true,
    };

    /// <summary>The feed through the user's proxy, if the system has one for it.</summary>
    public static UpdateOffer Fetch()
    {
        var feed = new Uri(FeedUrl);
        return Fetch(feed, new SystemProxyRoutes().BrowserProxy(feed));
    }

    /// <summary>
    /// Reads a feed with one GET, the way the connection check makes its requests: https only (a
    /// loopback address only for the tests), no redirect followed (an answer other than 200 is no
    /// offer), no cookie and no credentials for the server, the user's Windows sign-in only for the
    /// proxy's own host and port, on a proxy object of this request's own, at most
    /// <see cref="TimeoutMilliseconds"/> and <see cref="MaxBytes"/>. Null when anything is not as expected.
    /// </summary>
    internal static UpdateOffer Fetch(Uri feed, Uri proxy)
    {
        if (feed == null || !(feed.Scheme == Uri.UriSchemeHttps || (feed.IsLoopback && feed.Scheme == Uri.UriSchemeHttp))) { return null; }
        try
        {
            var request = (HttpWebRequest)WebRequest.Create(feed);
            request.Method = "GET";
            request.Accept = "application/json";
            request.UserAgent = ProductInfo.DisplayName;
            request.Timeout = TimeoutMilliseconds;
            request.ReadWriteTimeout = TimeoutMilliseconds;
            request.AllowAutoRedirect = false;
            request.CookieContainer = null;
            request.Credentials = null;
            request.UseDefaultCredentials = false;
            request.PreAuthenticate = false;
            request.KeepAlive = false;
            // The user's sign-in only for the proxy's own host and port: .NET follows a proxy's redirect
            // to CONNECT by itself and would otherwise answer a sign-in request from the redirect's host.
            request.Proxy = proxy == null ? null : new WebProxy(proxy) { Credentials = new ProxyOnlyCredentials(proxy) };
            using var response = (HttpWebResponse)request.GetResponse();
            if (response.StatusCode != HttpStatusCode.OK) { return null; }
            using var body = response.GetResponseStream();
            if (body == null) { return null; }
            var buffer = new byte[MaxBytes + 1];
            var read = 0;
            int n;
            while (read < buffer.Length && (n = body.Read(buffer, read, buffer.Length - read)) > 0) { read += n; }
            return read > MaxBytes ? null : Parse(Encoding.UTF8.GetString(buffer, 0, read));
        }
        catch (Exception ex) when (ex is WebException or IOException or NotSupportedException or SecurityException or UriFormatException or InvalidOperationException or ProtocolViolationException)
        {
            return null;
        }
    }

    /// <summary>The start-up notice: "Endpoint Toolbelt 1.1.2 is available." It says nothing about who updates the app.</summary>
    public static string NoticeLine(UpdateOffer offer) => ProductInfo.DisplayName + " " + offer.Version + " is available.";

    /// <summary>The notice's one link: the product's page on the publisher's site, which keeps the release notes; fixed in the code.</summary>
    public const string ReleaseNotesUrl = ProductInfo.ProductsUrl;

    /// <summary>The check box in About and on the installer's first screen; one meaning: no request when cleared.</summary>
    public const string CheckOption = "Check for new versions at start-up";

    /// <summary>True when the start-up notice shows: a newer version, not shown before for this version.</summary>
    public static bool NoticeDue(UpdateOffer newer, string shownFor)
        => newer != null && !string.Equals(shownFor, newer.Version, StringComparison.Ordinal);

    /// <summary>The line About adds while the check is on and a newer version is known.</summary>
    public static string AboutLine(UpdateOffer offer) => "Version " + offer.Version + " is available (released " + offer.Released + ").";

    /// <summary>About's button that looks at once, also with the check box cleared: the user asks for it.</summary>
    public const string CheckNowButton = "Check _now";

    /// <summary>Said while About's look runs.</summary>
    public const string CheckingNow = "Looking for a newer version...";

    /// <summary>
    /// The answer of About's look: the newer version, or that the running one is the newest, or
    /// that the feed could not be read (null offer).
    /// </summary>
    public static string CheckNowResult(string running, UpdateOffer offer)
        => offer == null ? "The newest version could not be read. Check the connection and try again."
            : IsNewer(running, offer) ? AboutLine(offer)
            : "You have the newest version (" + Core(running) + ").";
}
