# Assistant execution and Gemini

Delivery enforces the same tool policy for chat and MCP. Anonymous callers
can track by public tracking number, preview fees and transit time, and read
service coverage. The other tools require the corresponding verified role.
Parcel reads by internal ID use Delivery's ownership checks. Merchant order
reads use Merchant's approved-owner policy. Fee previews read pricing and
link to booking, where the user can request an official quote.

Gemini may confirm the public tracking or fee intent selected by Delivery.
Only a fixed description of that intent goes to Gemini. Chat text, history,
tracking numbers, user IDs, private tool data and credentials supplied by
the caller stay in Delivery. The provider key goes in Google's API-key
header. A valid provider response identifies a Gemini-assisted exchange,
while the parcel and fee facts come from Delivery. If Gemini is unavailable
or requests any other tool or arguments, Delivery returns its local result.
Private intents use local tools throughout.

Set the Gemini key through the Delivery development user secret `AI:ApiKey`
or the deployment setting `AI__ApiKey`. Set `GEMINI_MODEL` to override the
default Gemini 3.5 Flash-Lite model. Use synthetic tracking and fee
requests in a configured environment to verify provider-backed mode.
The automated tests exercise the integration with a controlled HTTP
provider and check fallback separately.

## Limits

Configure the following settings under `AI:Limits` or through the environment
prefix `AI__Limits__`.

| Setting | Default |
|---|---:|
| `RequestBytes` | 32768 |
| `MessageCharacters` | 1500 per message |
| `HistoryEntries` | 10 per request |
| `ToolArgumentCharacters` | 2048 |
| `ProviderTimeoutSeconds` | 8 |
| `ProviderToolCalls` | 1 |
| `AnonymousRequestsPerMinute` | 120 across assistant endpoints per Delivery replica |

Gateway client budgets still apply at the public ingress.
