# License refresh (connected installs)

Workbench can keep its license key current by asking the vendor's [license server](license-server.md) for a fresh one. This is
optional. **The default is offline**: nothing is sent anywhere.

## Offline or connected

| | Offline (default) | Connected |
| --- | --- | --- |
| Setting | `License__ServerUrl` empty or not set | `License__ServerUrl=https://license.example.com` |
| Calls out | Never. Not at startup, not on a schedule | At startup, then every 24 hours |
| Key | A long-lived key you paste under Setup › License | A short-lived key the server renews while the subscription is paid |
| For | Defense and air-gapped networks | Self-hosted installs with internet access |

Seats and modules bought or changed in the license server (Stripe, or the admin page) reach a connected install on its next
refresh, with no key to paste.

## What happens

1. You paste the key you were given under **Setup › License**, as always. It must be a connected-license (`WB2`) key.
2. The first run **activates**: it sends the license id from that key, a random **install id** (made once and kept in the
   organization's database; it is not derived from the machine), the number of full and light seats in use, and the Workbench
   version. Nothing else is sent. Later runs **refresh** with the same details.
3. The server answers with a new key. Workbench **verifies its signature offline** against the built-in public key before
   using it, and ignores the answer if it is not signed by it, is for a different license, or is older than the key in use.
   A valid key replaces the stored one exactly as if it had been pasted. Over-use is not blocked (the server only warns).
4. **Setup › License** shows the last attempt, the last success and the server's own message.

## When something goes wrong

Nothing here ever locks anyone out. The key you hold, its expiry date and the 14-day grace period decide what is on.

| Situation | What Workbench does |
| --- | --- |
| Server unreachable, a 5xx, rate limited, or an answer that does not verify | Keeps the current key. Tries again after 5 minutes, then 10, 20, 40 … never waiting longer than the normal interval |
| The server says the subscription lapsed, was canceled or the license revoked | Keeps the current key and shows the server's reason on Setup › License. Keeps asking daily, so a renewal is picked up. The existing grace period then Community still follow the key's expiry |
| The key is an offline key | The server answers `offline_license`; nothing is refreshed and nothing is flagged |
| No successful refresh for 7 days (`License__RefreshStaleDays`) while attempts keep failing or being refused | Administrators see a banner. **Entitlements are not changed by it** |

## Settings

| Setting | Default | |
| --- | --- | --- |
| `License:ServerUrl` | empty | The license server's base URL. Empty means offline |
| `License:RefreshIntervalHours` | `24` | Hours between refreshes while they work |
| `License:RefreshStaleDays` | `7` | Days without a success before the admin banner |
| `License:RefreshTimeoutSeconds` | `30` | Per request |

If your network needs it, allow outbound HTTPS from the Workbench server to the license server only.

## What has not been verified

The client is tested against a fake server written from the license server's code and contract, and a key issued by the server's
own signer is checked to parse here. It has **not** been run against a live license server, a real Stripe subscription, or over a
real network (TLS, proxies, timeouts).
