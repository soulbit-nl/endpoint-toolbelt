# Name and trademarks

The product is **Endpoint Toolbelt by SoulBit**. This page says how the name is written, how
Intune is mentioned and which installed names are spelled without a space.

## The name

| Item | Text |
|---|---|
| Name | Endpoint Toolbelt |
| Window and dialog titles | Endpoint Toolbelt (the full name; "Toolbelt" alone is never a name) |
| Publisher | SoulBit |
| Publisher line | Endpoint Toolbelt by SoulBit |
| One-line description | Self-service repair for Intune-managed Windows devices |
| Trademark notice | Microsoft, Intune and Windows are trademarks of the Microsoft group of companies. |

The window has no header of its own: its title bar names the product. The About dialog (title
"Endpoint Toolbelt - About") carries the publisher line, the description, the statement that the
product is not affiliated with or endorsed by Microsoft, and the trademark notice. Beside its OK
button it links to the publisher's site, "soulbit.app" (`https://soulbit.app/go/endpoint-toolbelt`, a
redirect to the start page with all products, with no version or device data in the address),
and to the source code on GitHub; they open the browser only when clicked. It has no
separate compatibility sentence: "Works with Microsoft Intune." read as if the product operated
Intune, while it works on the device itself; the description already says which devices it is
for. The MSI's summary and its Programs and Features comment say "Endpoint Toolbelt by SoulBit:
self-service repair for Intune-managed Windows devices". The README starts with the name.

"for Microsoft Intune" never follows the name in a title, a header or a subtitle: the product's
own name is the title, and which devices it is for is said by the description and in the text.

SoulBit is the publisher, not part of the name: the MSI's Manufacturer, the company and
copyright in the file properties, the publisher line in About, and the parent folder
`C:\Program Files\SoulBit\Endpoint Toolbelt`.

In code the constants are `ProductInfo.DisplayName`, `ProductInfo.ShortDisplayName`,
`ProductInfo.NameWithPublisher`, `ProductInfo.Description` and `ProductInfo.TrademarkNotice` (`src/EndpointToolbelt.Contracts/ProductInfo.cs`). Every text a
person reads uses them; a test (`NamingTests`) refuses a product-name literal anywhere else in
the source, and refuses the spelling "ToolBelt" in the source and the installer.

### Why "Toolbelt" is never used alone

Other software marks contain the word, among them "SQL Toolbelt" (EU, UK and US, class 9). So
the product always carries its full name, and "Toolbelt" alone is not used as a product name,
family name or logo.

## How Intune is mentioned

Intune is a registered trademark of Microsoft for software that administers, monitors and
audits devices: the category this product is in. Microsoft's
[Trademark and Brand Guidelines](https://www.microsoft.com/en-us/legal/intellectualproperty/trademarks/usage/general)
say, without a written licence:

- do not use Microsoft's brand assets in the name of a business, product, service, app, domain
  name or social media account;
- do say that a product is compatible or interoperable with a Microsoft product, and make the
  relation clear ("works with", "for");
- do use the names as adjectives before nouns: "Intune-managed devices", "Intune tools";
- include a trademark notice when their trademarks are used.

So: "Works with Microsoft Intune" and "Intune-managed devices" (allowed), never "Intune
Toolbelt", and never "intune" in a domain name, a social media handle, an account or a trademark
filing. The texts say in plain words what the product does with Intune (repairs the Intune
Management Extension, syncs with Intune, reads the enrollment) and never imply that Microsoft
endorses or made it. The product uses no Microsoft logo or icon.

## Installed names

Installed devices, their administrators and the tools that call the service know these names;
renaming them would break upgrades, the event log and every reader of the registry and the pipes.
The spelling a person reads is the installed name with a space.

| Item | Name |
|---|---|
| Installation folder | `%ProgramFiles%\SoulBit\Endpoint Toolbelt\` |
| Event log and event source | `Endpoint Toolbelt` |
| Service name | `EndpointToolbeltService` |
| Pipes | `\\.\pipe\EndpointToolbelt`, `\\.\pipe\EndpointToolbelt.Caller` |
| Registry keys | `HKLM\SOFTWARE\EndpointToolbelt`, `HKLM\SOFTWARE\Policies\EndpointToolbelt` |
| Working folder, log files | `EndpointToolbelt`, `EndpointToolbelt.log`, `EndpointToolbeltService.log` |
| File names, assemblies, namespaces | `EndpointToolbelt.exe`, `EndpointToolbelt.Service.exe`, `EndpointToolbelt.Engine.exe`, `EndpointToolbelt.*`, `EndpointToolbelt-<version>-x64.msi` |
| Repository | `endpoint-toolbelt` |
