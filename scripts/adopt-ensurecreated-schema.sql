-- Adopt a PostgreSQL schema previously created by Blackbox's EF Core EnsureCreated.
-- Take and verify a database backup before running this script. It validates the
-- current Blackbox table/column/index shape, then records the baseline migration
-- without changing application rows. Do not use it on a fresh or unknown schema.
BEGIN;

DO $adopt$
DECLARE
    problems text;
    managed_column_count integer;
BEGIN
    IF to_regclass(format('%I.%I', current_schema(), '__EFMigrationsHistory')) IS NOT NULL THEN
        RAISE EXCEPTION 'Migration history already exists; do not run the legacy-schema adoption script.';
    END IF;

    WITH expected(table_name) AS (
        VALUES ('dead_letter_messages'), ('inbox_messages'), ('outbox_messages'),
               ('resource_reservations'), ('resources'), ('workflows')
    )
    SELECT string_agg(expected.table_name, ', ')
    INTO problems
    FROM expected
    WHERE to_regclass(format('%I.%I', current_schema(), expected.table_name)) IS NULL;

    IF problems IS NOT NULL THEN
        RAISE EXCEPTION 'Legacy Blackbox schema is incomplete; missing tables: %', problems;
    END IF;

    WITH expected(table_name, column_name, data_type, max_length, is_nullable) AS (
        VALUES
            ('dead_letter_messages', 'id', 'uuid', NULL::integer, false),
            ('dead_letter_messages', 'workflow_id', 'uuid', NULL::integer, true),
            ('dead_letter_messages', 'source', 'character varying', 64, false),
            ('dead_letter_messages', 'payload', 'jsonb', NULL::integer, false),
            ('dead_letter_messages', 'exception_message', 'text', NULL::integer, false),
            ('dead_letter_messages', 'stack_trace', 'text', NULL::integer, true),
            ('dead_letter_messages', 'status', 'character varying', 32, false),
            ('dead_letter_messages', 'created_at', 'timestamp with time zone', NULL::integer, false),
            ('dead_letter_messages', 'replayed_at', 'timestamp with time zone', NULL::integer, true),

            ('inbox_messages', 'id', 'uuid', NULL::integer, false),
            ('inbox_messages', 'consumer_name', 'character varying', 64, false),
            ('inbox_messages', 'message_id', 'character varying', 128, false),
            ('inbox_messages', 'payload', 'jsonb', NULL::integer, false),
            ('inbox_messages', 'status', 'character varying', 32, false),
            ('inbox_messages', 'created_at', 'timestamp with time zone', NULL::integer, false),

            ('outbox_messages', 'id', 'uuid', NULL::integer, false),
            ('outbox_messages', 'topic', 'character varying', 128, false),
            ('outbox_messages', 'message_key', 'character varying', 128, false),
            ('outbox_messages', 'event_type', 'character varying', 64, false),
            ('outbox_messages', 'payload', 'jsonb', NULL::integer, false),
            ('outbox_messages', 'status', 'character varying', 32, false),
            ('outbox_messages', 'retry_count', 'integer', NULL::integer, false),
            ('outbox_messages', 'error', 'text', NULL::integer, true),
            ('outbox_messages', 'created_at', 'timestamp with time zone', NULL::integer, false),
            ('outbox_messages', 'published_at', 'timestamp with time zone', NULL::integer, true),

            ('resource_reservations', 'id', 'uuid', NULL::integer, false),
            ('resource_reservations', 'resource_id', 'character varying', 64, false),
            ('resource_reservations', 'workflow_id', 'uuid', NULL::integer, false),
            ('resource_reservations', 'quantity', 'integer', NULL::integer, false),
            ('resource_reservations', 'status', 'character varying', 32, false),
            ('resource_reservations', 'created_at', 'timestamp with time zone', NULL::integer, false),

            ('resources', 'id', 'character varying', 64, false),
            ('resources', 'name', 'character varying', 128, false),
            ('resources', 'available_quantity', 'integer', NULL::integer, false),
            ('resources', 'allocated_quantity', 'integer', NULL::integer, false),
            ('resources', 'consumed_quantity', 'integer', NULL::integer, false),
            ('resources', 'version', 'integer', NULL::integer, false),
            ('resources', 'updated_at', 'timestamp with time zone', NULL::integer, false),

            ('workflows', 'id', 'uuid', NULL::integer, false),
            ('workflows', 'correlation_id', 'character varying', 128, false),
            ('workflows', 'status', 'character varying', 32, false),
            ('workflows', 'payload', 'jsonb', NULL::integer, false),
            ('workflows', 'error_details', 'jsonb', NULL::integer, true),
            ('workflows', 'version', 'integer', NULL::integer, false),
            ('workflows', 'created_at', 'timestamp with time zone', NULL::integer, false),
            ('workflows', 'updated_at', 'timestamp with time zone', NULL::integer, false)
    )
    SELECT string_agg(
        format('%I.%I expected %s (length %s, nullable %s), found %s (length %s, nullable %s)',
            expected.table_name, expected.column_name, expected.data_type,
            coalesce(expected.max_length::text, 'n/a'), expected.is_nullable,
            coalesce(actual.data_type, 'missing'),
            coalesce(actual.character_maximum_length::text, 'n/a'),
            coalesce(actual.is_nullable, 'n/a')),
        E'\n')
    INTO problems
    FROM expected
    LEFT JOIN information_schema.columns AS actual
      ON actual.table_schema = current_schema()
     AND actual.table_name = expected.table_name
     AND actual.column_name = expected.column_name
    WHERE actual.column_name IS NULL
       OR actual.data_type <> expected.data_type
       OR coalesce(actual.character_maximum_length, -1) <> coalesce(expected.max_length, -1)
       OR (actual.is_nullable = 'YES') IS DISTINCT FROM expected.is_nullable;

    IF problems IS NOT NULL THEN
        RAISE EXCEPTION 'Legacy Blackbox columns do not match the migration baseline:%', E'\n' || problems;
    END IF;

    SELECT count(*)
    INTO managed_column_count
    FROM information_schema.columns
    WHERE table_schema = current_schema()
      AND table_name IN ('dead_letter_messages', 'inbox_messages', 'outbox_messages',
                         'resource_reservations', 'resources', 'workflows');

    IF managed_column_count <> 46 THEN
        RAISE EXCEPTION 'Legacy Blackbox tables have % columns; expected exactly 46.', managed_column_count;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_attribute AS attribute
        JOIN pg_class AS table_metadata ON table_metadata.oid = attribute.attrelid
        JOIN pg_namespace AS table_schema ON table_schema.oid = table_metadata.relnamespace
        JOIN pg_attrdef AS column_default
          ON column_default.adrelid = table_metadata.oid
         AND column_default.adnum = attribute.attnum
        WHERE table_schema.nspname = current_schema()
          AND table_metadata.relname = 'resources'
          AND attribute.attname = 'consumed_quantity'
          AND NOT attribute.attisdropped
          AND pg_get_expr(column_default.adbin, column_default.adrelid) = '0'
    ) THEN
        RAISE EXCEPTION 'Legacy resources.consumed_quantity column is missing its expected default value of 0.';
    END IF;

    WITH expected(table_name, index_name, is_primary, columns) AS (
        VALUES
            ('dead_letter_messages', 'PK_dead_letter_messages', true, ARRAY['id']::text[]),
            ('inbox_messages', 'PK_inbox_messages', true, ARRAY['id']::text[]),
            ('inbox_messages', 'IX_inbox_messages_consumer_name_message_id', false, ARRAY['consumer_name', 'message_id']::text[]),
            ('outbox_messages', 'PK_outbox_messages', true, ARRAY['id']::text[]),
            ('resource_reservations', 'PK_resource_reservations', true, ARRAY['id']::text[]),
            ('resource_reservations', 'IX_resource_reservations_resource_id_workflow_id', false, ARRAY['resource_id', 'workflow_id']::text[]),
            ('resources', 'PK_resources', true, ARRAY['id']::text[]),
            ('workflows', 'PK_workflows', true, ARRAY['id']::text[]),
            ('workflows', 'IX_workflows_correlation_id', false, ARRAY['correlation_id']::text[])
    )
    SELECT string_agg(expected.index_name, ', ')
    INTO problems
    FROM expected
    WHERE NOT EXISTS (
        SELECT 1
        FROM pg_index AS index_metadata
        JOIN pg_class AS table_metadata ON table_metadata.oid = index_metadata.indrelid
        JOIN pg_namespace AS table_schema ON table_schema.oid = table_metadata.relnamespace
        JOIN pg_class AS index_object ON index_object.oid = index_metadata.indexrelid
        CROSS JOIN LATERAL (
            SELECT array_agg(attribute.attname::text ORDER BY key_column.ordinality) AS column_names
            FROM unnest(index_metadata.indkey) WITH ORDINALITY AS key_column(attnum, ordinality)
            JOIN pg_attribute AS attribute
              ON attribute.attrelid = table_metadata.oid
             AND attribute.attnum = key_column.attnum
            WHERE key_column.ordinality <= index_metadata.indnkeyatts
        ) AS indexed_columns
        WHERE table_schema.nspname = current_schema()
          AND table_metadata.relname = expected.table_name
          AND index_object.relname = expected.index_name
          AND index_metadata.indisunique
          AND index_metadata.indisvalid
          AND index_metadata.indisready
          AND index_metadata.indisprimary = expected.is_primary
          AND indexed_columns.column_names = expected.columns
    );

    IF problems IS NOT NULL THEN
        RAISE EXCEPTION 'Legacy Blackbox schema is missing or has different key/index definitions: %', problems;
    END IF;
END
$adopt$;

CREATE TABLE "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261015193546_BaselineEnsureCreatedSchema', '10.0.4');

COMMIT;
