-- =========================================================================================
-- PROJECT BLACKBOX: Multi-Region Active-Active PostgreSQL Replication & CRDT Schema
-- =========================================================================================
-- This script configures PostgreSQL logical replication publications, CRDT storage tables,
-- and PL/pgSQL merge functions that mathematically resolve replication conflicts without 2PC.
-- =========================================================================================

-- 1. Helper function: Compute pairwise maximum (Join-Semilattice Merge) between two JSONB vectors
--    e.g. Merge('{"regA": 10, "regB": 5}', '{"regA": 12, "regB": 3}') => '{"regA": 12, "regB": 5}'
CREATE OR REPLACE FUNCTION crdt_jsonb_vector_max(v1 JSONB, v2 JSONB)
RETURNS JSONB
LANGUAGE plpgsql
IMMUTABLE
AS $$
DECLARE
    result JSONB := COALESCE(v1, '{}'::jsonb);
    rec RECORD;
    current_val BIGINT;
    incoming_val BIGINT;
BEGIN
    IF v2 IS NULL OR v2 = '{}'::jsonb THEN
        RETURN result;
    END IF;

    FOR rec IN SELECT key, value FROM jsonb_each_text(v2)
    LOOP
        incoming_val := rec.value::BIGINT;
        IF result ? rec.key THEN
            current_val := (result ->> rec.key)::BIGINT;
            IF incoming_val > current_val THEN
                result := jsonb_set(result, ARRAY[rec.key], to_jsonb(incoming_val));
            END IF;
        ELSE
            result := jsonb_set(result, ARRAY[rec.key], to_jsonb(incoming_val));
        END IF;
    END LOOP;

    RETURN result;
END;
$$;

-- 2. Helper function: Sum all scalar values in a JSONB vector
CREATE OR REPLACE FUNCTION crdt_jsonb_vector_sum(v JSONB)
RETURNS BIGINT
LANGUAGE plpgsql
IMMUTABLE
AS $$
DECLARE
    total BIGINT := 0;
    rec RECORD;
BEGIN
    IF v IS NULL OR v = '{}'::jsonb THEN
        RETURN 0;
    END IF;

    FOR rec IN SELECT value FROM jsonb_each_text(v)
    LOOP
        total := total + rec.value::BIGINT;
    END LOOP;

    RETURN total;
END;
$$;

-- =========================================================================================
-- 3. PN-Counter Table (Positive-Negative Replicated Counter)
-- =========================================================================================
CREATE TABLE IF NOT EXISTS crdt_pn_counters (
    resource_id VARCHAR(64) PRIMARY KEY,
    p_vector JSONB NOT NULL DEFAULT '{}'::jsonb,
    n_vector JSONB NOT NULL DEFAULT '{}'::jsonb,
    converged_value BIGINT NOT NULL DEFAULT 0,
    last_synced_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Trigger to automatically maintain converged value and merge vectors on upsert
CREATE OR REPLACE FUNCTION trg_crdt_pn_counter_merge()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    -- Merge incoming P and N vectors with existing state
    NEW.p_vector := crdt_jsonb_vector_max(OLD.p_vector, NEW.p_vector);
    NEW.n_vector := crdt_jsonb_vector_max(OLD.n_vector, NEW.n_vector);
    
    -- Calculate converged value: SUM(P) - SUM(N)
    NEW.converged_value := crdt_jsonb_vector_sum(NEW.p_vector) - crdt_jsonb_vector_sum(NEW.n_vector);
    NEW.last_synced_at := NOW();

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_crdt_pn_counter_merge ON crdt_pn_counters;
CREATE TRIGGER trg_crdt_pn_counter_merge
BEFORE UPDATE ON crdt_pn_counters
FOR EACH ROW
EXECUTE FUNCTION trg_crdt_pn_counter_merge();

-- =========================================================================================
-- 4. LWW-Register Table (Last-Write-Wins Register with Deterministic Node Tie-Breaking)
-- =========================================================================================
CREATE TABLE IF NOT EXISTS crdt_lww_registers (
    resource_id VARCHAR(64) PRIMARY KEY,
    state_payload TEXT NOT NULL,
    node_id VARCHAR(64) NOT NULL,
    timestamp_ticks BIGINT NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Trigger for Last-Write-Wins deterministic conflict resolution on cross-region updates
CREATE OR REPLACE FUNCTION trg_crdt_lww_merge()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    -- If existing timestamp is strictly newer, ignore incoming update
    IF OLD.timestamp_ticks > NEW.timestamp_ticks THEN
        RETURN NULL; -- Discard stale update silently
    END IF;

    -- If timestamps are identical, tie-break deterministically using node_id lexicographical order
    IF OLD.timestamp_ticks = NEW.timestamp_ticks AND OLD.node_id >= NEW.node_id THEN
        RETURN NULL; -- Discard duplicate or lower-priority tie-break
    END IF;

    NEW.updated_at := NOW();
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_crdt_lww_merge ON crdt_lww_registers;
CREATE TRIGGER trg_crdt_lww_merge
BEFORE UPDATE ON crdt_lww_registers
FOR EACH ROW
EXECUTE FUNCTION trg_crdt_lww_merge();

-- =========================================================================================
-- 5. PostgreSQL Logical Replication Publication Configuration
-- =========================================================================================
-- Create publication for tables enabled for active-active cross-region synchronization
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_publication WHERE pubname = 'blackbox_crdt_pub') THEN
        CREATE PUBLICATION blackbox_crdt_pub FOR TABLE crdt_pn_counters, crdt_lww_registers;
    END IF;
END $$;

-- Notes for multi-region subscription setup:
-- Region West Europe:
--   CREATE SUBSCRIPTION blackbox_poland_sub 
--   CONNECTION 'host=polandcentral-postgres port=5432 dbname=blackbox_db user=rep_user password=...' 
--   PUBLICATION blackbox_crdt_pub;
--
-- Region Poland Central:
--   CREATE SUBSCRIPTION blackbox_westeurope_sub 
--   CONNECTION 'host=westeurope-postgres port=5432 dbname=blackbox_db user=rep_user password=...' 
--   PUBLICATION blackbox_crdt_pub;
