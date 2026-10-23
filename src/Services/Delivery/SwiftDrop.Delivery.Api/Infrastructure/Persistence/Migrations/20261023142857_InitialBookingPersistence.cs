using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftDrop.Delivery.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialBookingPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "delivery_zones",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    boundaries = table.Column<string>(type: "jsonb", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_zones", x => x.id);
                    table.CheckConstraint("ck_zones_name", "btrim(name) <> ''");
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.id);
                    table.CheckConstraint("ck_outbox_payload", "(jsonb_typeof(payload) = 'object' AND event_type = 'StatusChanged' AND payload ?& ARRAY['eventId','eventType','schemaVersion','occurredAt','parcelId','parcelVersion','merchantId','status','previousStatus','correlationId'] AND payload - ARRAY['eventId','eventType','schemaVersion','occurredAt','parcelId','parcelVersion','merchantId','status','previousStatus','correlationId'] = '{}'::jsonb AND payload->>'eventId' = id::text AND payload->>'eventType' = event_type AND payload->'schemaVersion' = '1'::jsonb AND jsonb_typeof(payload->'parcelVersion') = 'number' AND (payload->>'parcelVersion')::bigint >= 1 AND jsonb_typeof(payload->'parcelId') = 'string' AND length(payload->>'parcelId') > 0 AND jsonb_typeof(payload->'correlationId') = 'string' AND length(payload->>'correlationId') > 0 AND jsonb_typeof(payload->'occurredAt') = 'string' AND (payload->>'occurredAt')::timestamptz = created_at AND (payload->>'status') IN ('Created','Assigned','PickedUp','InTransit','OutForDelivery','Delivered','Failed','Cancelled') AND (payload->'previousStatus' = 'null'::jsonb OR (payload->>'previousStatus') IN ('Created','Assigned','PickedUp','InTransit','OutForDelivery','Delivered','Failed','Cancelled')) AND (payload->'merchantId' = 'null'::jsonb OR jsonb_typeof(payload->'merchantId') = 'string')) IS TRUE");
                });

            migrationBuilder.CreateTable(
                name: "parcels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    external_order_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    tracking_number = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    size_category = table.Column<string>(type: "text", nullable: false),
                    weight_kg = table.Column<decimal>(type: "numeric", nullable: false),
                    pickup_address = table.Column<string>(type: "text", nullable: false),
                    pickup_contact = table.Column<string>(type: "text", nullable: true),
                    dropoff_address = table.Column<string>(type: "text", nullable: false),
                    dropoff_contact = table.Column<string>(type: "text", nullable: false),
                    zone_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_parcels", x => x.id);
                    table.UniqueConstraint("AK_parcels_id_zone_id", x => new { x.id, x.zone_id });
                    table.CheckConstraint("ck_parcels_details", "btrim(tracking_number) <> '' AND btrim(pickup_address) <> '' AND btrim(dropoff_address) <> '' AND btrim(dropoff_contact) <> ''");
                    table.CheckConstraint("ck_parcels_origin", "(customer_id IS NOT NULL AND merchant_id IS NULL AND external_order_id IS NULL) OR (customer_id IS NULL AND merchant_id IS NOT NULL AND external_order_id IS NOT NULL AND btrim(external_order_id) <> '')");
                    table.CheckConstraint("ck_parcels_size", "size_category IN ('Small','Medium','Large')");
                    table.CheckConstraint("ck_parcels_status", "status IN ('Created','Assigned','PickedUp','InTransit','OutForDelivery','Delivered','Failed','Cancelled')");
                    table.CheckConstraint("ck_parcels_version", "version >= 1");
                    table.CheckConstraint("ck_parcels_weight", "weight_kg > 0 AND weight_kg <= 30");
                    table.ForeignKey(
                        name: "FK_parcels_delivery_zones_zone_id",
                        column: x => x.zone_id,
                        principalTable: "delivery_zones",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "pricing_tiers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    zone_id = table.Column<Guid>(type: "uuid", nullable: false),
                    size_category = table.Column<string>(type: "text", nullable: false),
                    pricing_version = table.Column<string>(type: "text", nullable: false),
                    base_rate = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    per_kg_rate = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pricing_tiers", x => x.id);
                    table.UniqueConstraint("AK_pricing_tiers_id_pricing_version_currency", x => new { x.id, x.pricing_version, x.currency });
                    table.UniqueConstraint("AK_pricing_tiers_id_zone_id_pricing_version_currency", x => new { x.id, x.zone_id, x.pricing_version, x.currency });
                    table.CheckConstraint("ck_pricing_amounts", "base_rate >= 0 AND per_kg_rate >= 0");
                    table.CheckConstraint("ck_pricing_currency", "currency = 'LKR'");
                    table.CheckConstraint("ck_pricing_size", "size_category IN ('Small','Medium','Large')");
                    table.CheckConstraint("ck_pricing_version", "btrim(pricing_version) <> ''");
                    table.ForeignKey(
                        name: "FK_pricing_tiers_delivery_zones_zone_id",
                        column: x => x.zone_id,
                        principalTable: "delivery_zones",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "booking_history",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    parcel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_status = table.Column<string>(type: "text", nullable: true),
                    to_status = table.Column<string>(type: "text", nullable: false),
                    changed_by = table.Column<Guid>(type: "uuid", nullable: false),
                    change_reason = table.Column<string>(type: "text", nullable: true),
                    delivery_note = table.Column<string>(type: "text", nullable: true),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_booking_history", x => x.id);
                    table.CheckConstraint("ck_history_initial", "from_status IS NOT NULL OR to_status = 'Created'");
                    table.CheckConstraint("ck_history_status", "to_status IN ('Created','Assigned','PickedUp','InTransit','OutForDelivery','Delivered','Failed','Cancelled') AND (from_status IS NULL OR from_status IN ('Created','Assigned','PickedUp','InTransit','OutForDelivery','Delivered','Failed','Cancelled'))");
                    table.ForeignKey(
                        name: "FK_booking_history_parcels_parcel_id",
                        column: x => x.parcel_id,
                        principalTable: "parcels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_id = table.Column<string>(type: "text", nullable: false),
                    operation = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_fingerprint = table.Column<string>(type: "text", nullable: false),
                    stored_response = table.Column<string>(type: "jsonb", nullable: true),
                    parcel_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_keys", x => x.id);
                    table.CheckConstraint("ck_idempotency_completion", "((parcel_id IS NULL AND stored_response IS NULL) OR (parcel_id IS NOT NULL AND stored_response IS NOT NULL AND jsonb_typeof(stored_response) = 'object' AND stored_response ?& ARRAY['statusCode','body','location'] AND stored_response->'statusCode' = '201'::jsonb AND jsonb_typeof(stored_response->'body') = 'string' AND jsonb_typeof(stored_response->'location') = 'string')) IS TRUE");
                    table.CheckConstraint("ck_idempotency_expiry", "expires_at = created_at + INTERVAL '720 hours'");
                    table.CheckConstraint("ck_idempotency_scope", "btrim(principal_id) <> '' AND btrim(operation) <> '' AND btrim(key) <> '' AND btrim(request_fingerprint) <> ''");
                    table.ForeignKey(
                        name: "FK_idempotency_keys_parcels_parcel_id",
                        column: x => x.parcel_id,
                        principalTable: "parcels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "price_snapshots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    parcel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pricing_tier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    zone_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_fee = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    weight_fee = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    pricing_version = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    calculated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_price_snapshots", x => x.id);
                    table.CheckConstraint("ck_snapshots_amounts", "base_fee >= 0 AND weight_fee >= 0 AND total = base_fee + weight_fee");
                    table.CheckConstraint("ck_snapshots_currency", "currency = 'LKR'");
                    table.ForeignKey(
                        name: "FK_price_snapshots_parcels_parcel_id_zone_id",
                        columns: x => new { x.parcel_id, x.zone_id },
                        principalTable: "parcels",
                        principalColumns: new[] { "id", "zone_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_price_snapshots_pricing_tiers_pricing_tier_id_zone_id_prici~",
                        columns: x => new { x.pricing_tier_id, x.zone_id, x.pricing_version, x.currency },
                        principalTable: "pricing_tiers",
                        principalColumns: new[] { "id", "zone_id", "pricing_version", "currency" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "quotes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_fingerprint = table.Column<string>(type: "text", nullable: false),
                    delivery_input = table.Column<string>(type: "jsonb", nullable: false),
                    pricing_tier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pricing_version = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_by_parcel = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quotes", x => x.id);
                    table.CheckConstraint("ck_quotes_amount", "total >= 0 AND currency = 'LKR'");
                    table.CheckConstraint("ck_quotes_expiry", "expires_at = created_at + INTERVAL '10 minutes'");
                    table.CheckConstraint("ck_quotes_input", "jsonb_typeof(delivery_input) = 'object' AND btrim(request_fingerprint) <> ''");
                    table.CheckConstraint("ck_quotes_owner", "owner_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_quotes_parcels_consumed_by_parcel",
                        column: x => x.consumed_by_parcel,
                        principalTable: "parcels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_quotes_pricing_tiers_pricing_tier_id_pricing_version_curren~",
                        columns: x => new { x.pricing_tier_id, x.pricing_version, x.currency },
                        principalTable: "pricing_tiers",
                        principalColumns: new[] { "id", "pricing_version", "currency" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_booking_history_parcel_id",
                table: "booking_history",
                column: "parcel_id",
                unique: true,
                filter: "from_status IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_booking_history_parcel_id_timestamp",
                table: "booking_history",
                columns: new[] { "parcel_id", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_zones_name",
                table: "delivery_zones",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_keys_parcel_id",
                table: "idempotency_keys",
                column: "parcel_id");

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_keys_principal_id_operation_key",
                table: "idempotency_keys",
                columns: new[] { "principal_id", "operation", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_created_at_id",
                table: "outbox_messages",
                columns: new[] { "created_at", "id" },
                filter: "published_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_parcels_customer_id_created_at_id",
                table: "parcels",
                columns: new[] { "customer_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_parcels_merchant_id_external_order_id",
                table: "parcels",
                columns: new[] { "merchant_id", "external_order_id" },
                unique: true,
                filter: "merchant_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_parcels_tracking_number",
                table: "parcels",
                column: "tracking_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_parcels_zone_id",
                table: "parcels",
                column: "zone_id");

            migrationBuilder.CreateIndex(
                name: "IX_price_snapshots_parcel_id",
                table: "price_snapshots",
                column: "parcel_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_price_snapshots_parcel_id_zone_id",
                table: "price_snapshots",
                columns: new[] { "parcel_id", "zone_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_price_snapshots_pricing_tier_id_zone_id_pricing_version_cur~",
                table: "price_snapshots",
                columns: new[] { "pricing_tier_id", "zone_id", "pricing_version", "currency" });

            migrationBuilder.CreateIndex(
                name: "IX_pricing_tiers_zone_id_size_category_pricing_version",
                table: "pricing_tiers",
                columns: new[] { "zone_id", "size_category", "pricing_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_quotes_consumed_by_parcel",
                table: "quotes",
                column: "consumed_by_parcel",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_quotes_owner_id_expires_at",
                table: "quotes",
                columns: new[] { "owner_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_quotes_pricing_tier_id_pricing_version_currency",
                table: "quotes",
                columns: new[] { "pricing_tier_id", "pricing_version", "currency" });

            // PostgreSQL enforces invariants even when EF tracking is bypassed.
            // No schema is hardcoded, allowing isolated test search paths.
            migrationBuilder.Sql("""
                CREATE FUNCTION delivery_immutable_snapshot() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'Accepted price snapshots are immutable'
                        USING ERRCODE = '23514', CONSTRAINT = 'ck_price_snapshots_immutable';
                END;
                $$;
                CREATE TRIGGER price_snapshots_immutable
                    BEFORE UPDATE OR DELETE ON price_snapshots
                    FOR EACH ROW EXECUTE FUNCTION delivery_immutable_snapshot();

                CREATE FUNCTION delivery_require_snapshot() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM price_snapshots WHERE parcel_id = NEW.id) THEN
                        RAISE EXCEPTION 'A committed parcel requires a price snapshot'
                            USING ERRCODE = '23514', CONSTRAINT = 'ck_parcels_require_snapshot';
                    END IF;
                    RETURN NULL;
                END;
                $$;
                CREATE CONSTRAINT TRIGGER parcels_require_snapshot
                    AFTER INSERT ON parcels DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION delivery_require_snapshot();

                CREATE FUNCTION delivery_guard_parcel_booking() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW.status <> 'Created' OR NEW.version <> 1 THEN
                            RAISE EXCEPTION 'New parcels start at Created version 1'
                                USING ERRCODE = '23514', CONSTRAINT = 'ck_parcels_initial_state';
                        END IF;
                    ELSIF (to_jsonb(NEW) - ARRAY['status','version','updated_at']) IS DISTINCT FROM
                          (to_jsonb(OLD) - ARRAY['status','version','updated_at']) THEN
                        RAISE EXCEPTION 'Parcel ownership and booking details are immutable'
                            USING ERRCODE = '23514', CONSTRAINT = 'ck_parcels_booking_immutable';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER parcels_booking_guard BEFORE INSERT OR UPDATE ON parcels
                    FOR EACH ROW EXECUTE FUNCTION delivery_guard_parcel_booking();

                CREATE FUNCTION delivery_guard_quote() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'UPDATE' THEN
                        IF (to_jsonb(NEW) - 'consumed_by_parcel') IS DISTINCT FROM
                           (to_jsonb(OLD) - 'consumed_by_parcel') OR
                           (OLD.consumed_by_parcel IS NOT NULL AND
                            NEW.consumed_by_parcel IS DISTINCT FROM OLD.consumed_by_parcel) THEN
                            RAISE EXCEPTION 'Quote input and consumption are immutable'
                                USING ERRCODE = '23514', CONSTRAINT = 'ck_quotes_immutable';
                        END IF;
                    END IF;
                    IF NEW.consumed_by_parcel IS NOT NULL AND NOT EXISTS (
                        SELECT 1 FROM parcels
                        WHERE id = NEW.consumed_by_parcel AND customer_id = NEW.owner_id
                    ) THEN
                        RAISE EXCEPTION 'Quote consumption must match the owner'
                            USING ERRCODE = '23514', CONSTRAINT = 'ck_quotes_consumption_owner';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER quotes_guard
                    BEFORE INSERT OR UPDATE ON quotes
                    FOR EACH ROW EXECUTE FUNCTION delivery_guard_quote();

                -- Tariff changes create a new row/version. Activation is mutable;
                -- economic fields remain immutable even before first use.
                CREATE FUNCTION delivery_guard_tariff() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF (to_jsonb(NEW) - 'is_active') IS DISTINCT FROM (to_jsonb(OLD) - 'is_active') THEN
                        RAISE EXCEPTION 'A tariff change requires a new pricing version'
                            USING ERRCODE = '23514', CONSTRAINT = 'ck_pricing_version_immutable';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER pricing_tiers_guard BEFORE UPDATE ON pricing_tiers
                    FOR EACH ROW EXECUTE FUNCTION delivery_guard_tariff();

                CREATE FUNCTION delivery_guard_idempotency() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Idempotency tombstones must be retained'
                            USING ERRCODE = '23514', CONSTRAINT = 'ck_idempotency_retained';
                    END IF;
                    IF OLD.stored_response IS NOT NULL OR
                       (to_jsonb(NEW) - ARRAY['stored_response','parcel_id']) IS DISTINCT FROM
                       (to_jsonb(OLD) - ARRAY['stored_response','parcel_id']) THEN
                        RAISE EXCEPTION 'Idempotency scope and committed response are immutable'
                            USING ERRCODE = '23514', CONSTRAINT = 'ck_idempotency_immutable';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER idempotency_keys_guard
                    BEFORE UPDATE OR DELETE ON idempotency_keys
                    FOR EACH ROW EXECUTE FUNCTION delivery_guard_idempotency();

                -- The relay can update publication metadata, never the logical event.
                CREATE FUNCTION delivery_guard_outbox() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Event provenance must be retained pending an approved cleanup policy'
                            USING ERRCODE = '23514', CONSTRAINT = 'ck_outbox_retained';
                    END IF;
                    IF (to_jsonb(NEW) - 'published_at') IS DISTINCT FROM (to_jsonb(OLD) - 'published_at') THEN
                        RAISE EXCEPTION 'A committed outbox event is immutable'
                            USING ERRCODE = '23514', CONSTRAINT = 'ck_outbox_immutable';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER outbox_messages_guard BEFORE UPDATE OR DELETE ON outbox_messages
                    FOR EACH ROW EXECUTE FUNCTION delivery_guard_outbox();
                CREATE UNIQUE INDEX ux_outbox_parcel_version_event ON outbox_messages
                    ((payload->>'parcelId'), (payload->>'parcelVersion'), event_type);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "booking_history");

            migrationBuilder.DropTable(
                name: "idempotency_keys");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "price_snapshots");

            migrationBuilder.DropTable(
                name: "quotes");

            migrationBuilder.DropTable(
                name: "parcels");

            migrationBuilder.DropTable(
                name: "pricing_tiers");

            migrationBuilder.DropTable(
                name: "delivery_zones");

            migrationBuilder.Sql("""
                DROP FUNCTION delivery_immutable_snapshot();
                DROP FUNCTION delivery_require_snapshot();
                DROP FUNCTION delivery_guard_parcel_booking();
                DROP FUNCTION delivery_guard_quote();
                DROP FUNCTION delivery_guard_tariff();
                DROP FUNCTION delivery_guard_idempotency();
                DROP FUNCTION delivery_guard_outbox();
                """);
        }
    }
}
