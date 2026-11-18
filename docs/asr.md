# Endpoint Toolbelt and attack surface reduction rules

This guide is for IT administrators whose devices use Microsoft Defender's attack surface
reduction (ASR) rules. One rule can stop Endpoint Toolbelt from starting or installing. The
guide explains why, how to recognize it, and how to allow the Toolbelt without switching the
rule off.

## In short

| Situation | What to do |
|---|---|
| Any build, signed or not | Add a **per-rule exclusion** for the install folder `C:\Program Files\SoulBit\Endpoint Toolbelt\` to this one rule. It lasts across versions. See [A per-rule folder exclusion](#a-per-rule-folder-exclusion). |
| A release signed by SoulBit, when you prefer an indicator | Add an **Allow certificate indicator** in Microsoft Defender for Endpoint for that release's signing certificate. It covers that release only. See [Per release: a certificate indicator](#per-release-a-certificate-indicator). |
| You sign the build with your own certificate | Sign it with `build.ps1` and add a certificate indicator for your certificate. See [Signing a build yourself](#signing-a-build-yourself). |

Do not set the rule to audit mode or switch it off to make the Toolbelt work: that allows
every other unknown program as well.

## The rule

The rule is **"Block executable files from running unless they meet a prevalence, age, or
trusted list criterion"**, GUID `01443614-cd74-433a-b99e-2ecdc07bfc25`. Configuration Manager
calls it "... criteria". From Microsoft's ASR rules reference (see [Sources](#sources)):

- It blocks executable files, for example `.exe`, `.dll` or `.scr`, from launching, because
  launching untrusted or unknown executable files can be risky. A file runs when it meets one
  of the criteria in the rule's name. In plain words: it is seen on enough devices
  (prevalence), it has been known long enough (age), or it is trusted (for example through an
  Allow indicator of your organization).
- It depends on Microsoft Defender Antivirus and on **cloud-delivered protection**, which must
  be on for the rule to work.
- In block mode it writes event **1121** to the `Microsoft-Windows-Windows Defender/Operational`
  log; in audit mode it writes event **1122**.
- It honors per-rule exclusions, global ASR exclusions, Defender Antivirus file and folder
  exclusions, and indicators for files and for certificates.

## Why the Toolbelt is blocked

- **A signature alone is not enough.** A signature from a known publisher is one way a file
  gains trust, and the releases are signed; an unsigned build you made yourself has only its own
  hash to go on.
- **Every version is new.** Each build has new file hashes, so each version starts with no
  prevalence and no age, on every device at the same time. The Toolbelt runs on far fewer
  devices than a mass-market program, so a version may never become widespread enough to meet
  the prevalence criterion on its own.
- **Signing alone is not enough.** A new certificate has no reputation either. A signed build
  passes this rule once the certificate is on your trusted list, which is what the certificate
  indicator below does.

The rule checks every executable file, so it can block the app (`EndpointToolbelt.exe`), the
service (`EndpointToolbelt.Service.exe`), the repair engine (`EndpointToolbelt.Engine.exe`) and
the assemblies they load.

## How to recognize it

What the user or the installer sees:

- **The app does not start.** Windows shows its own message when the user opens Endpoint
  Toolbelt from the Start menu.
- **The install fails.** When the rule blocks the service, the MSI rolls back: `msiexec` returns
  1603, the Application log has MsiInstaller event 11920 (Error 1920, the service failed to
  start) and the System log has Service Control Manager event 7000 for Endpoint Toolbelt
  Service, "Access is denied".
- **A repair action fails with "blocked by a device policy".** When only the repair engine is
  blocked, the action ends with `EngineNotSigned` and the product's event 1013 names App Control,
  AppLocker, ASR rules and security software as possible causes.

To confirm it on the device, look for event 1121 with the rule's GUID and a path under
`C:\Program Files\SoulBit\Endpoint Toolbelt\`:

```powershell
Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-Windows Defender/Operational'; Id=1121,1122} -MaxEvents 50 |
  Where-Object Message -match 'EndpointToolbelt' | Format-List TimeCreated, Id, Message
```

## Per release: a certificate indicator

Every release from 1.0.0 is signed by SoulBit with Microsoft's Artifact Signing. Its certificates
are renewed daily and are valid for 72 hours, so each release is signed with a certificate of its
own; Microsoft notes that pinning trust to one such certificate is not durable. Defender's
certificate indicators support only the **leaf** certificate (the one the files are signed with)
and do not cover its parent or child certificates. An Allow indicator therefore covers the release
whose certificate it is, and a new release needs a new indicator. For an allowance that lasts
across versions, use the [per-rule folder exclusion](#a-per-rule-folder-exclusion).

**Requirements** (from Microsoft's page on certificate indicators):

- Microsoft Defender Antivirus in active mode, with cloud-delivered protection on;
- anti-malware client version 4.18.1901 or later, and up-to-date security intelligence;
- Windows 10 version 1703 or later, or Windows 11.

**The certificate of a release.** Take it from the release itself: check the signature, then
export the signing certificate to a `.cer` file.

```powershell
$sig = Get-AuthenticodeSignature 'C:\Program Files\SoulBit\Endpoint Toolbelt\EndpointToolbelt.exe'
$sig | Format-List Status, @{n='Subject';e={$_.SignerCertificate.Subject}}, @{n='Thumbprint';e={$_.SignerCertificate.Thumbprint}}
Export-Certificate -Cert $sig.SignerCertificate -FilePath .\EndpointToolbelt-signing.cer
```

`Status` must be `Valid` and the subject must name SoulBit (`O=SoulBit`).

**Steps**, in the Microsoft Defender portal:

1. Go to **Settings > Endpoints > Indicators** (under **Rules**) and open the **Certificates**
   tab.
2. Select **Add indicator** and upload the certificate file (`.cer` or `.pem`) you exported. It
   must be the **leaf** certificate, the one the files are signed with, not its root or
   intermediate.
3. Set the action to **Allow** and add a description, for example "Endpoint Toolbelt 1.0.0,
   allowed for ASR rule 01443614".
4. Set the scope to the device groups that run the Toolbelt, review the summary and select
   **Save**.

Creating or removing a certificate indicator can take up to 3 hours.

**A new release.** Add the indicator of the new release before you deploy it, and keep the old
one while devices still run the release it covers.

## A per-rule folder exclusion

Exclude the Toolbelt's install folder from this one rule: it holds for every version, signed or
not. The rule stays in block mode for every other program.

**Why this is acceptable.** `C:\Program Files\SoulBit\Endpoint Toolbelt\` is below Program
Files, so only administrators (and SYSTEM) can write there. A user or a program running as the
user cannot put a file in it. The Toolbelt's service also checks this itself: it refuses to start
when anyone other than the system and administrators can change its folder, its files or the
folders above it.

> **Warning.** Never exclude a folder a user can write to, such as `%LOCALAPPDATA%`, `%TEMP%`,
> `%APPDATA%`, `Downloads`, `C:\Users\...`, a folder under `%ProgramData%` that users can change,
> or a network share. Anything a user (or malware running as that user) puts there would then
> run without this rule's check. Microsoft also warns that excluded files are allowed to run and
> that no reports or events are recorded for them. Do not use user environment variables in an
> exclusion path, and do not add the folder as a global ASR exclusion: a global exclusion applies
> to every ASR rule.

**Steps in Microsoft Intune:**

1. Go to **Endpoint security > Attack surface reduction** and open the policy (profile
   **Attack Surface Reduction Rules**) that sets this rule. Select **Properties**, then **Edit**
   next to **Configuration settings**.
2. Find **Block executable files from running unless they meet a prevalence, age, or trusted list
   criterion**. With the rule in **Block** (or **Audit** or **Warn**) mode, a section **ASR Only
   Per Rule Exclusions** appears directly below it.
3. In that section select **Add** and enter:

   ```
   C:\Program Files\SoulBit\Endpoint Toolbelt\
   ```

   On devices where Windows is not on drive C, use `%ProgramFiles%\SoulBit\Endpoint Toolbelt\`
   instead: `%ProgramFiles%` is a system environment variable, which exclusions accept.
4. Select **Review + save**. The policy keeps its assignment to Microsoft Entra **device** groups;
   Defender for Endpoint management does not support user groups.

**Steps in Group Policy** (devices not managed by Intune): **Computer Configuration >
Administrative Templates > Windows Components > Microsoft Defender Antivirus > Microsoft
Defender Exploit Guard > Attack Surface Reduction > Apply a list of exclusions to specific
attack surface reduction (ASR) rules**. Set it to **Enabled**, select **Show...**, and add a row
with the value name `01443614-cd74-433a-b99e-2ecdc07bfc25` and the value
`C:\Program Files\SoulBit\Endpoint Toolbelt\`. This setting needs the Administrative Templates
of version 24H2 or later.

**Afterwards:**

- An exclusion applies when a program starts. After the policy reaches the device (sync the
  device from Company Portal or from **Settings > Accounts > Access work or school**), restart
  the device, or at least the Endpoint Toolbelt Service and the app.
- If the install had failed, install again.
- There should be no new event 1121 for a file under the install folder.
- If your Defender policy sets **Disable Local Admin Merge**, check Microsoft's notes on it in
  "Configure ASR rules and exclusions": with some management methods, per-rule exclusions do not
  apply then.

The exclusion stays: it holds for the next version too. If you prefer indicators instead, remove it only once the indicator of every release your devices run is in place.

## Other options, and why they are second best

- **File hash indicators** (Allow) for `EndpointToolbelt.exe`, `EndpointToolbelt.Service.exe`
  and `EndpointToolbelt.Engine.exe`, and for the assemblies they load,
  `EndpointToolbelt.Contracts.dll` and `EndpointToolbelt.Platform.dll` (the rule covers DLLs as
  well). This works without a certificate, but every build has new hashes, so you must add them again
  for every version, before you deploy it.
- **Warn mode** lets a user select **Unblock** for 24 hours. Microsoft describes it as a temporary
  bypass, not an exclusion, and newer Defender versions require administrator approval for it.
- **Audit mode or Off** for the rule allows every unknown program, not only the Toolbelt.

## Signing a build yourself

Organizations that build the Toolbelt themselves can sign it with their own code-signing
certificate and allow that certificate with an indicator, as above. The build script signs
when it is given a certificate; without one it reports `Signing: skipped: no certificate given
(unsigned build)`.

1. Put the code-signing certificate, with its private key, in `Cert:\CurrentUser\My` or
   `Cert:\LocalMachine\My` on the build machine.
2. Build with its thumbprint and an RFC 3161 timestamp server of your certificate authority:

   ```powershell
   .\build\build.ps1 -SignCertificateThumbprint <thumbprint> -TimestampUrl <timestamp server URL>
   ```

   This signs every executable and assembly the MSI installs (in a staged copy, never in the
   source tree) and then the MSI itself. Signatures are always timestamped, so they stay valid
   after the certificate expires; the script stops when `-TimestampUrl` is missing.
3. Export the leaf certificate (without its private key) for the indicator, for example with
   `Export-Certificate -Cert Cert:\CurrentUser\My\<thumbprint> -FilePath EndpointToolbelt-signing.cer`.
4. The certificate must chain to a root that Microsoft trusts, or, for your own certificate
   authority, to a root installed on the devices in **Trusted Root Certification Authorities**
   (with its intermediates).

For App Control for Business and AppLocker, see [Signing](deployment.md#signing) in the
deployment guide.

## Sources

- Microsoft Learn, [ASR rules reference](https://learn.microsoft.com/en-us/defender-endpoint/attack-surface-reduction-rules-reference): the rule, its GUID and dependencies.
- Microsoft Learn, [ASR rules overview](https://learn.microsoft.com/en-us/defender-endpoint/attack-surface-reduction-rules-overview): exclusions, which exclusions and indicators each rule honors, rule modes and Warn mode.
- Microsoft Learn, [Configure ASR rules and exclusions](https://learn.microsoft.com/en-us/defender-endpoint/attack-surface-reduction-rules-configure): per-rule exclusions in Intune and Group Policy.
- Microsoft Learn, [Create indicators based on certificates](https://learn.microsoft.com/en-us/defender-endpoint/indicator-certificates): requirements, leaf certificates only (neither parent nor child certificates are covered) and the portal steps.
- Microsoft Learn, [Artifact Signing certificate management](https://learn.microsoft.com/en-us/azure/artifact-signing/concept-certificate-management): certificates renewed daily and valid for 72 hours; the certificate profile's EKU as the durable identity.
- Microsoft Learn, [ASR events in Windows Event Viewer](https://learn.microsoft.com/en-us/defender-endpoint/attack-surface-reduction-windows-events): events 1121 and 1122.
