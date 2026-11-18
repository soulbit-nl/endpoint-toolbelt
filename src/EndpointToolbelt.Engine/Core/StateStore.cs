using System;
using System.Globalization;
using EndpointToolbelt.Platform;

namespace EndpointToolbelt.Engine;

/// <summary>
/// State the engine keeps under HKLM\SOFTWARE\EndpointToolbelt\State (SYSTEM and Administrators
/// only). The engine owns the single cooldown mechanism; the service only reads these values for
/// the status view.
/// </summary>
public sealed class StateStore
{
    public const string Key = ProductInfo.StateKey;

    private const string RebootPendingKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending";
    private const string RebootRequiredKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired";

    private readonly RunContext _run;

    internal StateStore(RunContext run) => _run = run;

    private IRegistry Registry => _run.Services.Registry;

    /// <summary>ISO 8601 with the offset: the engine and the user do not share a clock.</summary>
    public static string ToStateTime(DateTimeOffset when) => when.ToString("o", CultureInfo.InvariantCulture);

    /// <summary>A stored time as a UTC DateTime, or null when missing or unreadable.</summary>
    public static DateTime? FromStateTime(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value.UtcDateTime : null;
    }

    public string Get(string name)
    {
        try
        {
            return MachineRegistry.AsText(Registry.GetValue(Key, name));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool Set(string name, string value)
    {
        try
        {
            Registry.SetString(Key, name, value);
            return true;
        }
        catch (Exception ex)
        {
            _run.Log(string.Format(CultureInfo.InvariantCulture, "Could not write state value {0}: {1}", name, ex.Message), LogSeverity.Warning);
            return false;
        }
    }

    /// <summary>
    /// True when the action may run; false (with a COOLDOWN finding) while the cooldown is
    /// active. An unreadable timestamp allows the action and logs a warning: the State key is
    /// writable only by SYSTEM and Administrators, so this cannot be abused by users.
    /// </summary>
    public bool CooldownAllows(string stateName, int hours, string friendlyName, DateTime? nowUtc = null)
    {
        // 0 hours: the administrator switched the cooldown off.
        if (hours <= 0) { return true; }
        var raw = Get(stateName);
        if (string.IsNullOrEmpty(raw)) { return true; }
        var last = FromStateTime(raw);
        if (last == null)
        {
            _run.Log(string.Format(CultureInfo.InvariantCulture, "The time of the last {0} could not be read; the cooldown is not applied.", friendlyName), LogSeverity.Warning);
            return true;
        }
        var availableAt = last.Value.AddHours(hours);
        if ((nowUtc ?? _run.Services.Clock.UtcNow) >= availableAt) { return true; }
        _run.Log(string.Format(CultureInfo.InvariantCulture, "{0} skipped: the last run that changed this device was at {1}; cooldown {2}.",
            friendlyName, RunContext.UserTime(last.Value, "yyyy-MM-dd HH:mm"), TextFormat.Count(hours, "hour")));
        _run.AddFinding(FindingLevel.Info, "COOLDOWN", string.Format(CultureInfo.InvariantCulture, "{0} was skipped: it ran recently", friendlyName),
            string.Format(CultureInfo.InvariantCulture, "Last run that changed this device: {0}. The {1}-hour cooldown keeps a device from being put through it again and again.",
                RunContext.UserTime(last.Value, "yyyy-MM-dd HH:mm"), hours),
            string.Format(CultureInfo.InvariantCulture, "It can run again from about {0}. Nothing on this device was changed now.", RunContext.UserTime(availableAt, "yyyy-MM-dd HH:mm")));
        return false;
    }

    /// <summary>Stamped only when the device was changed, and only after the change ran.</summary>
    public void Stamp(string stateName) => Set(stateName, ToStateTime(_run.Services.Clock.Now));

    /// <summary>
    /// Stamps the cooldown before the part of an action that changes the device, and returns the
    /// value it replaced. A run that is stopped (Cancel, the time limit, a service stop) ends its
    /// process before the close-out, so it cannot stamp afterwards; without this a user could
    /// cancel a repair halfway and start it again at once. The close-out confirms the stamp when
    /// the device was changed, or puts the old value back (<see cref="Restore"/>).
    /// </summary>
    public string StampProvisionally(string stateName)
    {
        var previous = Get(stateName);
        Set(stateName, ToStateTime(_run.Services.Clock.Now));
        return previous;
    }

    /// <summary>Puts back the value <see cref="StampProvisionally"/> replaced: nothing on the device changed.</summary>
    public void Restore(string stateName, string previous)
    {
        if (!string.IsNullOrEmpty(previous))
        {
            Set(stateName, previous);
            return;
        }
        try
        {
            Registry.DeleteValue(Key, stateName);
        }
        catch (Exception ex)
        {
            _run.Log(string.Format(CultureInfo.InvariantCulture, "Could not remove state value {0}: {1}", stateName, ex.Message), LogSeverity.Warning);
        }
    }

    /// <summary>
    /// Records that this product changed something that needs a restart. The servicing keys read
    /// by <see cref="PendingRestart"/> are not reliably set by what a repair does, so without this
    /// the status would say "no restart pending" right after a repair that needs one. The service
    /// shows the marker only while it is newer than the last boot.
    /// </summary>
    public void MarkRestartRequired(string causedBy)
    {
        Set("RestartRequiredSince", ToStateTime(_run.Services.Clock.Now));
        Set("RestartRequiredBy", causedBy);
        _run.RestartRequired = true;
        _run.WriteRecord("restart-required");
        _run.PersistentLog("EndpointToolbelt:RESTART-REQUIRED");
    }

    /// <summary>
    /// Component servicing and Windows Update restart flags only. PendingFileRenameOperations is
    /// deliberately not read: it is set on most devices most of the time.
    /// </summary>
    public bool PendingRestart() => KeyExists(RebootPendingKey) || KeyExists(RebootRequiredKey);

    // Like Test-Path: a key that cannot be read counts as missing.
    private bool KeyExists(string subKey)
    {
        try { return Registry.KeyExists(subKey); }
        catch (Exception) { return false; }
    }
}
