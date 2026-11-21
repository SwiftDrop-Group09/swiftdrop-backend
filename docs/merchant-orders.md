# Merchant orders and webhooks

The [OpenAPI contract](merchant-orders.openapi.json) describes the order
submission and job read endpoints. Use the configured Gateway origin with
an active `X-API-Key` for an approved Merchant business. Submission requires
`orders:write`. Job lookup and listing require `orders:read` or an approved
Merchant owner access token. Send one credential per request.

Submit the [example body](../examples/merchant/order.example.json) to
`POST /api/v1/merchant/orders`. Replace the pickup and delivery zone IDs with
configured zones and use the pricing version the sender accepted. Supply a
single `Idempotency-Key` of up to 128 visible ASCII characters. Preserve
both the key and body when retrying an uncertain response. The same business
and external order ID with matching details returns the existing job.
A reused key or order ID with conflicting details returns 409.

A valid submission returns 202 with a relative `Location` and a job ID.
Read that URL with the same business's read credential. `Accepted` means
the order is stored for processing. Parcel fields remain null until
Delivery creates the parcel. Missing fields and malformed input return 400.
Invalid or revoked credentials return 401, and missing scope or business
approval returns 403.

## Run the order example

Set `SWIFTDROP_GATEWAY_URL` to the configured origin. Supply the API key with
both scopes via `SWIFTDROP_MERCHANT_API_KEY`. Set
`SWIFTDROP_PICKUP_ZONE_ID`, `SWIFTDROP_DELIVERY_ZONE_ID`,
`SWIFTDROP_PRICE_VERSION`, `SWIFTDROP_EXTERNAL_ORDER_ID` and
`SWIFTDROP_IDEMPOTENCY_KEY` for the example request.

```sh
python examples/merchant/integration.py order
```

## Configure and verify webhooks

Use an approved Merchant owner's bearer JWT for
`POST /api/v1/merchant/webhooks`. Set `url` or `endpointUrl` to your configured
HTTPS receiver and set `events` to an array of supported event names such as
`delivery.delivered`. If both URL fields are supplied, `url` takes precedence.
The response uses `endpointUrl` and returns a hex signing secret once.
Keep it in private configuration and use the header key version to select
the correct secret for an incoming webhook.

Persist the original request body bytes for verification. SwiftDrop signs the
ASCII timestamp, a period, and the raw body with HMAC-SHA256. Verify the
signature, reject timestamps more than five minutes from the local clock,
and deduplicate verified events by `X-SwiftDrop-Event-Id` for the subscription.
The [webhook example](../examples/merchant/webhook.example.json) shows the
body fields. Use the actual received bytes and headers with this command.

```sh
python examples/merchant/integration.py verify-webhook "$WEBHOOK_BODY_FILE" \
  --timestamp "$WEBHOOK_TIMESTAMP" --signature "$WEBHOOK_SIGNATURE" \
  --event-id "$WEBHOOK_EVENT_ID"
```

Set `SWIFTDROP_WEBHOOK_SECRET` to the hex secret for the key version before
running the verifier.
