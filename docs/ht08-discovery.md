# MeloBuds Pro / HT08: discovery milestone

Status: **identity and GATT topology confirmed on hardware; protocol still unverified**. No HT08 proprietary read, query, subscription, or setting write is enabled. The desktop controls still target N70. A confirmed identity is evidence of *which device this is*, never evidence that a command works on it.

## Hardware-confirmed result

A contributor ran `--discovery-only` against a physical QCY MeloBuds Pro on 2026-09-11 and reported the redacted output.

| Field | Observed |
| --- | --- |
| Advertised local name | `QCY MeloBuds Pro` |
| QCY company ID | `0x521C` |
| Vendor / product ID | `19786` (`0x4D4A`) |
| Model | HT08 |
| Advertisement address type | Random |
| Manufacturer payload | 24 bytes, company ID excluded; all non-identity bytes redacted |

No address, full payload, or serial number was recorded. `19786` is therefore promoted from public-catalog evidence to hardware-confirmed **identity**. `19785` keeps its public-catalog level because no contributor has observed it, and `19790` (Pro Plus) remains unidentified.

### Observed GATT topology

Four services. Properties are exactly as reported by the Windows GATT client.

| Service | Characteristic | R | W | WWR | N | I |
| --- | --- | --- | --- | --- | --- | --- |
| `1800` Generic Access | `2A00` Device Name | ✓ | | | | |
| `1801` Generic Attribute | `2A05` Service Changed | | | | | ✓ |
| `7033` vendor, undocumented | `1001` | | | ✓ | | |
| `7033` | `1002` | | | | ✓ | |
| `7033` | `2001` | | | ✓ | | |
| `7033` | `2002` | | | | ✓ | |
| `A001` QCY control | `1001` | | | ✓ | | |
| `A001` | `1002` | ✓ | | | ✓ | |
| `A001` | `0007` | ✓ | | | | |
| `A001` | `0008` | ✓ | | | ✓ | |
| `A001` | `000B` | ✓ | | ✓ | | |
| `A001` | `000D` | ✓ | | ✓ | | |

Two findings matter more than the rest:

1. **The HT08 exposes no `180F` Battery Service and no `180A` Device Information Service.** The allowlisted standard reads therefore return nothing, and battery and firmware remain **Unknown**. This is a valid result and must not be worked around by sending unverified queries.
2. **The `A001` table closely resembles the N70's**, and additionally exposes `000B`, which the tested N70 firmware did not. Matching UUIDs and properties are *not* evidence of a matching command protocol — see the ANC divergence recorded below. The model gate deliberately refuses the command session even though the characteristic set would satisfy every structural check `QcyDeviceClient` performs; `Ht08GattTopologyDoesNotUnlockTheN70CommandSession` locks that behaviour in.

The undocumented `7033` service has no public description. Its `1001`/`1002` and `2001`/`2002` pairs follow a write/notify shape. **It is treated as out of scope and must not be probed**: an undocumented vendor service on a TWS device is a plausible location for firmware or production operations, and SECURITY.md places firmware traffic outside the project. Nothing in this repository addresses `7033`.

## Audit of the existing implementation

Audited from `ee7e849` before implementation, including README, CONTRIBUTING, SECURITY, all documentation, Protocol, Device, Bluetooth, Services, Probe, tests, and the ViewModel connection/capability paths.

| Area | Finding before this change |
| --- | --- |
| N70 identification | `QcyUuids.IsN70` accepts vendor IDs 23872/23877. Paired device discovery uses a QCY/MeloBuds name containing N70. |
| BLE scan | Active Windows advertisement watcher, extended advertisements enabled; filter company ID `0x521C`; collect by observed address for 8 seconds in UI or 12 in Probe; prioritize N70 then RSSI. |
| Manufacturer parsing | `QcyAdvertisement.Parse`: at least 8 bytes; first two form a big-endian vendor ID; bytes 5/6/7 carry batteries with charging in bit 7. |
| IDs | Company ID is separate from the payload vendor/product ID. No separate on-air model ID was decoded. Public database `modelId` is not a unique hardware model code. |
| Control endpoint | N70 parser reorders bytes 12,11,13,16,15,14; optional second address uses 19,18,20,23,22,21. Transport tries control, observed, then other address. |
| GATT | Control transport opens only `0000A001-0000-1000-8000-00805F9B34FB`. Firmware `00000007`, battery `00000008`, optional EQ `0000000B`, keys `0000000D`, command `00001001`, response `00001002`, all with the same Bluetooth base suffix. |
| Operations without application GATT writes | Scan, Windows cached battery property, service/characteristic enumeration, direct `ReadValueAsync` on available characteristics. |
| Client initialization | `QcyDeviceClient.CreateAsync` immediately subscribes to response and possibly battery, then calls `RefreshAsync`. It is not a write-free constructor. |
| Refresh | Reads direct characteristics, then sends framed `FE` requests on `1001`, including fallbacks. `RefreshBatteryAsync` can also send a query. |
| Probe | Preferred N70 but fell back to the first unknown QCY. Initialized the N70 client and performed a second refresh. Trace was attached after initialization, hiding the first queries from console output. |
| Battery | Advertisement; direct `0008` and notifications; fallback query `2F`; UI can show aggregate Windows `System.Devices.BatteryLife`. |
| Firmware | Direct `0007`, otherwise query `30`; proprietary parser formats three bytes or six bytes for L/R versions. |
| Capabilities | Nullable state and exposed characteristics; ViewModel flags for wear detection, EQ and keys. Custom EQ is enabled by the command/notification channel even before a ten-band readback. Not all setter methods enforce individual capability checks. |
| N70 assumptions | Fixed A001/1001/1002, query list, ANC scene triples, ten-band EQ, touch IDs, firmware/battery formats, UI labels and a single desired settings profile. |
| Cache hazard | Any cached A001 service could be assigned the N70 vendor ID without checking its name. Remembered addresses also reconstruct an N70 candidate without a saved model identity. |
| UI initialization | On connection, automatically reapplies desired settings when enabled. Timer refreshes battery once a minute, and reconnects while disconnected. |
| Confirmation limits | ANC compares exact bytes; generic writes accept an opcode response or fallback query, without always comparing requested value. EQ preset queries state; key mappings are reread. This milestone does not change those existing N70 semantics. |
| Tests | MSTest with deterministic in-memory packets and profile/startup tests; no BLE/hardware tests existed. |
| Safe reuse for HT08 | Company filtering, observed Windows address/type, RSSI, raw advertisement retention, UUID/property enumeration, strict standard reads. Packet framing remains reusable knowledge but is not activated. |
| Evidence still needed | HT08 advertisement field layout, actual endpoint/services, proprietary battery/firmware formats, every setting command and ACK/readback. A Read/Write property or familiar UUID is insufficient. |

Relevant history: `298b1dd` added BLE discovery/transport; `cc1d899` added the N70 session and Probe; `ed580bd` replaced the earlier placeholder protocol adapter with characteristic/state capability checks; `c546c83` modeled exact ANC state; `36af7f8` added cache/remembered reconnection and battery refresh. Reintroducing a general adapter hierarchy is unnecessary for a discovery-only model.

## Existing N70 writes

All command-channel payloads use `1001`; notifications arrive on `1002`.

| Path | Writes |
| --- | --- |
| Initialization subscriptions | CCCD writes for `1002` and, when available, `0008`. Standard descriptors, but still writes. |
| State queries | `FF 03 FE 01 <opcode>` for `2C`, fallback `06`, `17`, `09`, `10`, `23`, `24`, `2A`, `1D`, `14`, and conditional `2F`, `30`, `22`, `2B`. |
| Wear detection | `2C`, or legacy `06`. |
| Noise control | `17`: ANC scenes, transparency and normal. |
| Toggles | Game `09`, sleep `10`, LDAC `23`, multipoint `24`, wind `2A`. |
| Other settings | Prompt volume `1D`, auto power-off `14`, custom EQ `22`. |
| Direct characteristics | EQ preset to `000B`; touch mappings to `000D`. |
| Automatic callers | Client construction/refresh/battery fallback; desktop profile reapply, UI changes and battery timer. |

No command encodings or N70 state/confirmation algorithms were changed. No firmware, reset, or other new commands were added.

## Minimal model boundary

`QcyModelProfile` centralizes model identity and the permission to create an N70 command session. N70 retains its existing dynamic capability checks; HT08 and unknown profiles have no proprietary control capability. This model profile is distinct from `Services/DeviceProfile`, which stores the user's desired N70 settings.

`WindowsBluetoothTransport.ConnectAsync` refuses discovery-only models. The connection carries its model profile, and `QcyDeviceClient.CreateAsync` independently refuses unsupported models **before** installing handlers, subscribing, reading, or refreshing. A complete N70-shaped GATT table does not bypass this guard.

The cache no longer labels every A001 service N70: Windows-cache enumeration now requires an N70 name, so a cached HT08 exposing A001 is never offered as an N70 target. A renamed N70 may therefore need a fresh advertisement, which the existing scan path still provides. Cached and remembered addresses are additionally refused when the resolved endpoint names itself as a different model; an endpoint reporting no name is not treated as a mismatch, because a QCY control endpoint frequently exposes no name and requiring one would break the documented remembered-address reconnect. Neither A001 nor an old stored address is sufficient identity, and names/advertisements are not cryptographic authentication.

Discovery uses the existing scan and a separate, small `WindowsGattDiscovery` entry point in the Bluetooth layer. It returns value records, not a writable connection. The Probe branches into this path before reaching N70 initialization. Parsing/allowlisting stays in Protocol; Windows APIs stay in Bluetooth; rendering stays in Probe. The WinUI/ViewModel and saved profile format are unchanged.

## External evidence and licensing

Reviewed 2026-09-11; the implementation is independently written C#. No external source code, product database, captures, or assets were copied into this repository.

- **HttpKiwi/OpenQCY**, revision `30bd2fa067a6fc43214bd94ba9592a76e5ea4908`: [license](https://github.com/HttpKiwi/OpenQCY/blob/30bd2fa067a6fc43214bd94ba9592a76e5ea4908/LICENSE) is MIT with a separate note about unlicensed Quicky documentation. [Constants](https://github.com/HttpKiwi/OpenQCY/blob/30bd2fa067a6fc43214bd94ba9592a76e5ea4908/lib/core/qcy/constants.dart) and [advertisement mapping](https://github.com/HttpKiwi/OpenQCY/blob/30bd2fa067a6fc43214bd94ba9592a76e5ea4908/lib/core/qcy/advertisement.dart) associate vendor `19786` (`0x4D4A`) with MeloBuds Pro and company `0x521C`. Its parser requires 20 bytes, unlike Desktop's 8-byte minimum, although both use the first two bytes for identity. Its [BLE controller](https://github.com/HttpKiwi/OpenQCY/blob/30bd2fa067a6fc43214bd94ba9592a76e5ea4908/lib/services/ble_controller.dart) performs direct proprietary battery/firmware reads but also subscribes and synchronizes settings on connect; its startup flow is not safe to reuse here.
- **hui1601/Quicky**, revision `d13c68f08a6c8b031083eede968fb443824c7af0`: no LICENSE file in the inspected tree. Used only for factual interoperability comparison, not code copying or dependency inclusion. The [product database](https://github.com/hui1601/Quicky/blob/d13c68f08a6c8b031083eede968fb443824c7af0/internal/product/products.json) labels `19785` (`0x4D49`) HT08, `19786` MeloBuds Pro and `19790` HT08 MeloBuds Pro Plus. The last remains **unknown** here. Its shared `modelId=21020` also appears for N70, so it is not treated as an HT08 identifier or a wire field. Consult its [GATT documentation](https://github.com/hui1601/Quicky/blob/d13c68f08a6c8b031083eede968fb443824c7af0/docs/service.md) only as a lead for later validation.

These sources originally justified provisional identification only. The 2026-09-11 hardware run independently confirmed `19786` on a physical MeloBuds Pro, which settles **identity**. It does not establish compatibility with the HT08 command protocol, and their feature catalogs are still not imported.

### Protocol divergence already visible between HT08 and N70

Comparing this repository's hardware-validated N70 ANC encoding with the MeloBuds Pro values published by HttpKiwi/OpenQCY shows the two models do **not** share an ANC scene encoding:

| Scene | N70 `0x17` parameters (hardware-validated here) | MeloBuds Pro values published by HttpKiwi |
| --- | --- | --- |
| Indoor | `01 01 02` | `01 00 02` |
| Commuting | `01 02 02` | `01 01 02` |
| Noisy | `01 03 02` | `01 02 02` |
| Anti-wind | `01 04 00` | `01 03 00` |
| Adaptive | `01 05 00` | `01 05 00` |
| Transparency | `03 01 04` | `03 02 00` |
| Normal / off | `02 00 00` | `02 00 00` |

The sub-scene byte is shifted by one for four scenes and transparency differs in both bytes. Reusing the N70 ANC encoding on an HT08 would therefore select the wrong scene. This is the concrete justification for refusing to reuse N70 writes across models, and it is why ANC stays research-only.

The same family protocol also assigns low opcodes to destructive operations — HttpKiwi documents `0x01` as a settings reset and `0x03` as a factory reset. Opcode scanning or brute forcing is consequently never acceptable on this hardware.

## Discovery safety and output

Sequence: active scan → select exactly one matching observed endpoint → uncached GATT enumeration → allowlisted standard reads → print → dispose all service/device handles. No embedded address probing, proprietary characteristic reads, command queries, descriptor writes, notifications, settings reapply, pairing/reset operations, or fallback to N70 commands.

Active scanning and GATT enumeration exchange ordinary Bluetooth protocol traffic; this is not passive radio capture. Windows manages the connection and may retain it if another application holds a handle. The application makes no GATT writes in this path. See Microsoft's [GATT client lifecycle and API documentation](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-client).

Every discovered service/characteristic UUID and each of Read, Write, WriteWithoutResponse, Notify and Indicate is printed separately. Reads require the Read property **and** an exact service/characteristic pair:

| Service | Characteristic | Value |
| --- | --- | --- |
| `180F` Battery Service | `2A19` Battery Level | One byte, 0–100%; side/case association unspecified. |
| `180A` Device Information | `2A26` Firmware Revision | Valid UTF-8 string. |
| `180A` Device Information | `2A24` Model Number | Valid UTF-8 string. |
| `180A` Device Information | `2A29` Manufacturer Name | Valid UTF-8 string. |

These are Bluetooth-base UUIDs (`0000xxxx-0000-1000-8000-00805F9B34FB`), from SIG [Assigned Numbers](https://www.bluetooth.com/wp-content/uploads/Files/Specification/HTML/Assigned_Numbers/out/en/index-en.html), [Battery Service](https://www.bluetooth.com/specifications/specs/battery-service/) and [Device Information Service](https://www.bluetooth.com/wp-content/uploads/Files/Specification/HTML/DIS_v1.2/out/en/index-en.html). Serial number, System ID, device-name characteristic and other identifying values are excluded. Invalid UTF-8/control characters and invalid battery lengths/ranges are rejected. Failure does not trigger a query or subscription.

HT08 may expose none of these standard services. In that case battery and firmware remain unknown. Even if `A001/0007/0008` exist with Read, this milestone only enumerates them.

By default no address is printed; manufacturer payload length and first two identity bytes are shown, with **every remaining byte replaced by `XX`** because unknown fields may contain identifiers. Name and standard strings are escaped, but must still be manually reviewed for personal information. No diagnostic output is automatically saved. Optional `--raw-manufacturer-data` shows private local bytes only; do not use that option for the output you share or commit. Synthetic tests are explicitly not hardware evidence.

## Windows hardware test

1. Exit OpenQCY Desktop completely, including its tray instance, and close other BLE diagnostic clients. Turn off other QCY devices nearby so selection is unambiguous.
2. Close the QCY phone app and disconnect the earbuds from the phone (temporarily disabling phone Bluetooth is sufficient). Keep Windows Bluetooth on. Existing Windows audio pairing may remain; initially disconnect its audio connection using Windows settings, without removing the pairing.
3. Put both charged earbuds inside the case. Start the command below, then open the lid during the 12-second scan and leave it open. Keep the case near the PC.

```powershell
Set-Location 'C:\dev\OpenQCY-Desktop'
dotnet run --project Diagnostics/OpenQCY.Probe/OpenQCY.Probe.csproj -c Release -- --discovery-only
```

4. If no advertisement appears or the endpoint is unavailable, repeat with both earbuds removed and the case open, QCY app still closed. If still unavailable, connect the already-paired HT08 audio endpoint in Windows and repeat. These are collection variations, not a confirmed HT08 wake recipe; report which one works. Do not reset the earbuds or hold buttons to force undocumented modes.
5. If multiple QCY models were found, filter using the **observed** decimal vendor/product ID, for example:

```powershell
dotnet run --project Diagnostics/OpenQCY.Probe/OpenQCY.Probe.csproj -c Release -- --discovery-only --vendor-id 19786
```

If more than one endpoint still matches, the Probe refuses to guess. Isolate the target and retry; report the ambiguity if it persists. Unknown vendor IDs remain enumerable with the same discovery path. Only the actually observed endpoint/address type is attempted; a separate unadvertised control endpoint remains future work.

The scan lasts 12 seconds. GATT usually adds seconds, but Windows can queue requests for minutes. The 90-second budget prevents further operations after expiry; it does not forcibly cancel a Windows call already pending. Ctrl+C requests the same safe stop. Exit codes: `0` enumeration completed; `1` invalid options; `2` no unique target; `4` failed/partial enumeration; `5` cancellation/budget. Per-characteristic read failures are printed even if enumeration succeeds.

## What to return

Copy the complete **default, redacted** console output (including errors/exit code); replace any personal names or unexpected identifiers in strings. Include:

- Physical model from packaging, region/variant, and whether the observed vendor ID was 19785, 19786 or another value.
- Windows version and Bluetooth adapter model; lid position, buds inside/outside, Windows audio connected/disconnected, phone Bluetooth and QCY app state for each attempt.
- Firmware version and QCY app version as manually shown by the official app, if available. Record these separately before/after the Probe session; do not keep the app connected during the scan.
- Services, characteristics, all five property flags, read statuses and standard values. No raw address, full manufacturer data, serial number, account data or unrelated Bluetooth traffic.

For subsequent evidence use this small template, consistent with CONTRIBUTING:

```text
Model / variant / region:
Firmware version (source: official app or standard read; unknown if absent):
QCY app version / phone OS:
Transport: BLE GATT
Service UUID:
Characteristic UUID and properties:
Operation: standard read / official-app action (one setting only)
Request bytes: N/A for discovery enumeration; no proprietary request sent
Response bytes: N/A until a reviewed, redacted capture is available
Observed decoded value:
Baseline and exact reproduction steps (including case/buds/connections):
Repeated result / ACK or readback:
Redactions performed:
```

Next: first verify identity and endpoint, then inspect standard battery/firmware results. If absent, collect a minimal redacted trace of the official app reading battery/firmware on user-owned hardware, compare with the public leads, and add deterministic fixtures before enabling a proprietary read/query. ANC comes later: one official-app change from a known baseline, repeated request/response and exact state readback, reviewed separately before implementing any write. No firmware traffic or full unredacted HCI dump should enter Git.

Before proposing device support upstream, link an issue as requested by CONTRIBUTING. This work does not create an issue/PR, push, tag or publish anything automatically.
