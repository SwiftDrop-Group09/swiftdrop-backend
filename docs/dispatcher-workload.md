# Dispatcher workload reads

These Delivery reads support FR-D03/TC-D03. Dispatcher and Admin principals can
read the fleet. Other roles cannot; the Courier self-manifest remains scoped to
the authenticated Courier and retains its separate service-day behavior.

## Contracts

`GET /api/v1/couriers/workload?page=1&pageSize=20` returns:

```json
{
  "items": [
    {
      "courierId": "11111111-1111-4111-8111-111111111111",
      "active": false,
      "onDuty": false,
      "activeCount": 3,
      "capacity": 10,
      "zoneIds": []
    }
  ],
  "totalCount": 1,
  "page": 1,
  "pageSize": 20
}
```

The fleet includes every operational Courier profile, including inactive,
off-duty and zero-workload profiles. `activeCount` counts retained assignments
whose status is `Active`, across **all service dates**, including overdue and
future work. Delivered, Failed, Cancelled and Reassigned records do not count.
Zone memberships do not multiply the count. `capacity` is the existing profile
configuration; this read does not decide whether a particular parcel can be
assigned. It supplies no daily-capacity reservation policy or available flag.
Courier IDs refer to Delivery's operational profiles; no Identity database or
privileged user-directory query supplies names.

`GET /api/v1/couriers/{courierId}/assignments?page=1&pageSize=20` returns:

```json
{
  "courierId": "11111111-1111-4111-8111-111111111111",
  "items": [
    {
      "parcelId": "22222222-2222-4222-8222-222222222222",
      "trackingNumber": "EXAMPLE-TRACKING",
      "status": "Assigned",
      "serviceDate": "2026-11-15",
      "assignedAt": "2026-11-14T12:00:00+00:00"
    }
  ],
  "totalCount": 1,
  "page": 1,
  "pageSize": 20
}
```

This is an operations-authorized projection, with no addresses, contacts, price,
proof or private notes. Its assignment-status/date scope matches `activeCount`.
Parcel status uses the canonical domain spelling. Service dates are calendar
dates; assignment timestamps are UTC. An existing courier with no outstanding
assignments returns an empty successful page. Missing/malformed/zero courier IDs
return a courier-specific 404. Literal `/couriers/me/assignments` keeps the
existing Courier-only handler and cannot be used by Dispatcher to impersonate one.

Both endpoints accept only one `page` and `pageSize` value; defaults are 1 and 20,
with maximum page size 100 and the existing offset bound. Other/duplicate/invalid
parameters return 400. Authorization precedes validation and storage access:
401 for missing/invalid identity, 403 for a non-operations role. Storage failure
returns safe 503 ProblemDetails, never a fabricated successful zero.

Workload ordering is courier ID ascending because Courier has no creation-time
field. Assignments order by assignment time descending, then assignment ID
descending. Each response's total, rows and memberships share a repeatable-read
database snapshot. Separate requests can observe subsequent committed changes.
Responses use `Cache-Control: private, no-store` and existing audit middleware.

## Compatibility and handoff

The user selected all unfinished work, all profiles with the existing `active`
flag exposed, and a paginated active-parcel view for this milestone. `active` is
an additive WorkloadPage field; the per-courier endpoint is a new REST contract.
The proposed central REST/route catalog needs these changes through its owning
contract review. No new GraphQL operation is included.

The existing Courier, CourierZone and CourierAssignment schema is reused. No
entity configuration, schema, migration, provisioning or shared database change
is included. The Gateway already forwards the courier route family. Assignment
and reassignment commands remain separate Dispatcher work; their transaction
must independently validate state, zone, active/duty flags and capacity.

The frontend workload feature requires this backend API before integration.
Verified real Courier profiles/memberships and shared database readiness remain
integration dependencies. The canonical runner applies shared migrations during
startup; the user's migration deferral still prevents incidental `up` commands.
Isolated test fixtures do not establish shared readiness or Docker startup acceptance.
