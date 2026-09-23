-- =========================================================================================
-- PROJECT BLACKBOX: Tamper-Proof Cryptographic Audit Ledger & Merkle Root Schema
-- =========================================================================================
-- Immutable WORM (Write Once Read Many) storage with SHA-256 hash chaining and Merkle trees
-- for SOC 2, HIPAA, PCI-DSS, and regulatory compliance audits.
-- =========================================================================================

-- 1. Merkle Blocks Table (Certified Cryptographic Roots)
CREATE TABLE IF NOT EXISTS audit_merkle_blocks (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    block_number BIGSERIAL NOT NULL UNIQUE,
    merkle_root VARCHAR(64) NOT NULL UNIQUE,
    leaf_count INT NOT NULL,
    sealed_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- 2. Audit Log Entries Table (Cryptographically Chained Records)
CREATE TABLE IF NOT EXISTS audit_log_entries (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    sequence_number BIGINT NOT NULL UNIQUE,
    workflow_id UUID NOT NULL,
    from_state VARCHAR(32) NOT NULL,
    to_state VARCHAR(32) NOT NULL,
    payload_hash VARCHAR(64) NOT NULL,
    previous_entry_hash VARCHAR(64) NOT NULL,
    entry_hash VARCHAR(64) NOT NULL UNIQUE,
    merkle_block_id UUID REFERENCES audit_merkle_blocks(id),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Index for instant lookups by workflow and entry hash
CREATE INDEX IF NOT EXISTS idx_audit_log_workflow_id ON audit_log_entries(workflow_id);
CREATE INDEX IF NOT EXISTS idx_audit_log_entry_hash ON audit_log_entries(entry_hash);

-- 3. Strict Immutability Trigger: Reject ANY UPDATE or DELETE (Append-Only WORM enforcement)
CREATE OR REPLACE FUNCTION trg_prevent_audit_tampering()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'CRITICAL SECURITY VIOLATION: Cryptographic audit log entries are immutable and cannot be updated or deleted!';
END;
$$;

DROP TRIGGER IF EXISTS trg_audit_entries_immutable ON audit_log_entries;
CREATE TRIGGER trg_audit_entries_immutable
BEFORE UPDATE OR DELETE ON audit_log_entries
FOR EACH ROW
EXECUTE FUNCTION trg_prevent_audit_tampering();

DROP TRIGGER IF EXISTS trg_audit_blocks_immutable ON audit_merkle_blocks;
CREATE TRIGGER trg_audit_blocks_immutable
BEFORE UPDATE OR DELETE ON audit_merkle_blocks
FOR EACH ROW
EXECUTE FUNCTION trg_prevent_audit_tampering();
