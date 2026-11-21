"""Submit and read a Merchant order or verify a received webhook."""

import argparse
import hashlib
import hmac
import json
import os
from pathlib import Path
import sys
import time
from urllib.error import URLError
from urllib.parse import urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener
from uuid import UUID


ORDERS = "/api/v1/merchant/orders"


class NoRedirects(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def required(name):
    value = os.environ.get(name)
    if not value:
        raise ValueError(f"Set {name} in the environment.")
    return value


def order_example():
    url = urlsplit(required("SWIFTDROP_GATEWAY_URL"))
    if (
        url.scheme not in ("http", "https")
        or not url.hostname
        or url.username is not None
        or url.path not in ("", "/")
        or url.query
        or url.fragment
    ):
        raise ValueError("SWIFTDROP_GATEWAY_URL must be an HTTP(S) origin.")

    body = json.loads(Path(__file__).with_name("order.example.json").read_text(encoding="utf-8"))
    body["externalOrderId"] = required("SWIFTDROP_EXTERNAL_ORDER_ID")
    body["pickupAddress"]["zoneId"] = str(UUID(required("SWIFTDROP_PICKUP_ZONE_ID")))
    body["deliveryAddress"]["zoneId"] = str(UUID(required("SWIFTDROP_DELIVERY_ZONE_ID")))
    body["acceptedPriceVersion"] = required("SWIFTDROP_PRICE_VERSION")
    headers = {"X-API-Key": required("SWIFTDROP_MERCHANT_API_KEY")}
    opener = build_opener(NoRedirects())
    origin = url.geturl().rstrip("/")
    submit = Request(origin + ORDERS, json.dumps(body).encode("utf-8"), headers={
        **headers,
        "Content-Type": "application/json",
        "Idempotency-Key": required("SWIFTDROP_IDEMPOTENCY_KEY"),
    })
    with opener.open(submit, timeout=10) as response:
        if response.status != 202:
            raise ValueError(f"Expected 202, received {response.status}.")
        accepted = json.load(response)
        location = response.headers.get("Location")

    expected = f"{ORDERS}/{UUID(accepted['id'])}"
    if location != expected:
        raise ValueError("The accepted order returned an unexpected Location.")
    with opener.open(Request(origin + expected, headers=headers), timeout=10) as response:
        job = json.load(response)
    print(json.dumps(job, indent=2))


def verify_webhook(args):
    raw_body = args.body.read_bytes()
    secret = bytes.fromhex(required("SWIFTDROP_WEBHOOK_SECRET"))
    timestamp = args.timestamp
    if not timestamp.isascii() or not timestamp.isdecimal() or abs(time.time() - int(timestamp)) > 300:
        raise ValueError("The webhook timestamp is invalid or expired.")

    signed = timestamp.encode("ascii") + b"." + raw_body
    expected = "sha256=" + hmac.new(secret, signed, hashlib.sha256).hexdigest()
    if not args.signature.isascii() or not hmac.compare_digest(expected, args.signature):
        raise ValueError("The webhook signature is invalid.")

    event = json.loads(raw_body)
    if UUID(event["eventId"]) != UUID(args.event_id):
        raise ValueError("The event ID header does not match the signed body.")
    print(f"Verified {event['eventType']} {event['eventId']}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("order", help="Submit an order and look up the returned job.")
    webhook = commands.add_parser("verify-webhook", help="Verify a webhook using its original body bytes.")
    webhook.add_argument("body", type=Path)
    webhook.add_argument("--timestamp", required=True)
    webhook.add_argument("--signature", required=True)
    webhook.add_argument("--event-id", required=True)
    args = parser.parse_args()
    try:
        if args.command == "order":
            order_example()
        else:
            verify_webhook(args)
    except (ValueError, KeyError, TypeError, OSError, URLError) as error:
        print(error, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
