#!/usr/bin/env bash
set -euo pipefail

image=${1:?Pass the built relay image as the first argument}
prefix="swiftdrop-relay-smoke-$$-$RANDOM"
database="$prefix-db"
broker="$prefix-broker"
relay="$prefix-app"
exchange='SwiftDrop.Contracts.V1:StatusChanged'
event_id='00000000-0000-4000-8000-000000000001'
recovery_id='00000000-0000-4000-8000-000000000002'

password() { od -An -N24 -tx1 /dev/urandom | tr -d ' \n'; }

db_password=$(password)
relay_password=$(password)
admin_password=$(password)
publisher_password=$(password)

cleanup() {
    local status=$?
    if (( status != 0 )); then
        docker logs --tail 50 "$relay" >&2 || true
        docker logs --tail 30 "$broker" >&2 || true
        docker logs --tail 30 "$database" >&2 || true
    fi
    docker rm -fv "$relay" "$broker" "$database" >/dev/null 2>&1 || true
    docker network rm "$prefix" >/dev/null 2>&1 || true
}
trap cleanup EXIT

wait_for() {
    local description=$1
    shift
    for ((attempt = 0; attempt < 90; attempt++)); do
        if "$@" >/dev/null 2>&1; then return 0; fi
        sleep 1
    done
    printf 'Timed out waiting for %s\n' "$description" >&2
    return 1
}

sql() {
    docker exec -i -e "PGPASSWORD=$db_password" "$database" \
        psql -h 127.0.0.1 -U postgres -d swiftdrop_delivery -XqAt -v ON_ERROR_STOP=1 "$@"
}

docker network create "$prefix" >/dev/null
docker run -d --name "$database" --network "$prefix" \
    -e POSTGRES_DB=swiftdrop_delivery -e "POSTGRES_PASSWORD=$db_password" postgres:16 >/dev/null
docker run -d --name "$broker" --network "$prefix" -p 127.0.0.1::15672 \
    -e RABBITMQ_DEFAULT_USER=smoke_admin -e "RABBITMQ_DEFAULT_PASS=$admin_password" rabbitmq:4-management >/dev/null

wait_for "PostgreSQL" sql -c 'SELECT 1'
sql -v "relay_password=$relay_password" <<'SQL'
CREATE TABLE public.outbox_messages (
    id uuid PRIMARY KEY,
    event_type text NOT NULL,
    payload jsonb NOT NULL,
    created_at timestamptz NOT NULL,
    published_at timestamptz
);
CREATE ROLE smoke_relay LOGIN PASSWORD :'relay_password';
GRANT CONNECT ON DATABASE swiftdrop_delivery TO smoke_relay;
GRANT USAGE ON SCHEMA public TO smoke_relay;
GRANT SELECT, UPDATE (published_at) ON public.outbox_messages TO smoke_relay;
SQL

api="http://$(docker port "$broker" 15672/tcp)/api"
rabbit() {
    curl --fail --silent --show-error --max-time 5 --user "smoke_admin:$admin_password" "$@"
}

wait_for "RabbitMQ management API" rabbit "$api/overview"
rabbit --json "{\"password\":\"$publisher_password\",\"tags\":\"\"}" -X PUT "$api/users/delivery_bus"
rabbit --json '{"configure":"^SwiftDrop[.]Contracts[.]V1:StatusChanged$","write":"^SwiftDrop[.]Contracts[.]V1:StatusChanged$","read":""}' \
    -X PUT "$api/permissions/%2F/delivery_bus"
rabbit --json '{"type":"fanout","durable":true}' -X PUT "$api/exchanges/%2F/$exchange"
rabbit --json '{"durable":true}' -X PUT "$api/queues/%2F/smoke-status"
rabbit --json '{"routing_key":""}' -X POST "$api/bindings/%2F/e/$exchange/q/smoke-status" >/dev/null

docker run -d --name "$relay" --network "$prefix" \
    -e "ConnectionStrings__DeliveryDatabase=Host=$database;Database=swiftdrop_delivery;Username=smoke_relay;Password=$relay_password" \
    -e "OutboxRelay__Broker__Host=$broker" -e OutboxRelay__Broker__Username=delivery_bus \
    -e "OutboxRelay__Broker__Password=$publisher_password" \
    "$image" SwiftDrop.Delivery.OutboxRelay.dll >/dev/null

relay_is_healthy() {
    [[ $(docker inspect --format '{{.State.Health.Status}}' "$relay") == healthy ]]
}

insert_event() {
    sql -v "event_id=$1" <<'SQL'
INSERT INTO public.outbox_messages (id, event_type, payload, created_at)
VALUES (:'event_id', 'StatusChanged', jsonb_build_object(
    'eventId', :'event_id', 'eventType', 'StatusChanged', 'schemaVersion', 1,
    'occurredAt', '2026-01-01T00:00:00Z', 'parcelId', 'smoke-parcel', 'parcelVersion', 1,
    'merchantId', NULL, 'status', 'Created', 'previousStatus', NULL, 'correlationId', 'smoke'
), CURRENT_TIMESTAMP);
SQL
}

publication_state() {
    sql -v "event_id=$1" <<'SQL'
SELECT CASE WHEN published_at IS NULL THEN 'pending' ELSE 'published' END
FROM public.outbox_messages WHERE id = :'event_id';
SQL
}

is_published() { [[ $(publication_state "$1") == published ]]; }

verify_delivery() {
    rabbit --json '{"count":2,"ackmode":"ack_requeue_false","encoding":"auto","truncate":50000}' \
        -X POST "$api/queues/%2F/smoke-status/get" |
        python -c '
import json
import sys

deliveries = json.load(sys.stdin)
if len(deliveries) != 1:
    sys.exit(f"Expected one delivery, found {len(deliveries)}")

event_id = sys.argv[1]
delivery = deliveries[0]
if delivery["properties"]["message_id"] != event_id or json.loads(delivery["payload"])["message"]["eventId"] != event_id:
    sys.exit("Broker message and payload must both retain the committed event ID")
' "$1"
}

wait_for "relay container health" relay_is_healthy
insert_event "$event_id"
wait_for "first event to be marked published" is_published "$event_id"
verify_delivery "$event_id"
printf '%s\n' 'Relay started and published one committed event with its original ID.'

docker stop "$broker" >/dev/null
insert_event "$recovery_id"
sleep 6
[[ $(publication_state "$recovery_id") == pending ]]

docker start "$broker" >/dev/null
api="http://$(docker port "$broker" 15672/tcp)/api"
wait_for "restarted RabbitMQ management API" rabbit "$api/overview"
wait_for "pending event to be published after RabbitMQ restart" is_published "$recovery_id"
verify_delivery "$recovery_id"
printf '%s\n' 'Broker outage left the committed event pending. The running relay published it after RabbitMQ restarted.'
