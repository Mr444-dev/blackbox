-- =============================================================================
-- BLACKBOX PLATFORM - INITIAL POSTGRESQL SCHEMA
-- Strict Invariants, Audit Trails, Transactional Outbox, and Concurrency Locks
-- =============================================================================

CREATE EXTENSION IF NOT EXISTS "uuid-ossp";

-- 1. WORKFLOWS TABLE (The Master State Machine Record)
CREATE TABLE IF NOT EXISTS workflows (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    correlation_id VARCHAR(128) NOT NULL,
    status VARCHAR(32) NOT NULL,
    payload JSONB NOT NULL,
    error_details JSONB,
    version INT NOT NULL DEFAULT 1,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_workflows_correlation_id ON workflows(correlation_id);
CREATE INDEX IF NOT EXISTS idx_workflows_status ON workflows(status);
CREATE INDEX IF NOT EXISTS idx_workflows_created_at ON workflows(created_at);

-- 2. WORKFLOW EVENTS (Immutable Audit Ledger)
CREATE TABLE IF NOT EXISTS workflow_events (
    id BIGSERIAL PRIMARY KEY,
    workflow_id UUID NOT NULL REFERENCES workflows(id) ON DELETE CASCADE,
    event_type VARCHAR(64) NOT NULL,
    from_state VARCHAR(32),
    to_state VARCHAR(32) NOT NULL,
    event_data JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_workflow_events_workflow_id ON workflow_events(workflow_id);
CREATE INDEX IF NOT EXISTS idx_workflow_events_created_at ON workflow_events(created_at);

-- 3. INVENTORY & RESOURCES (Strict Concurrency & Capacity Invariant)
CREATE TABLE IF NOT EXISTS resources (
    id VARCHAR(64) PRIMARY KEY,
    name VARCHAR(128) NOT NULL,
    available_quantity INT NOT NULL CHECK (available_quantity >= 0),
    allocated_quantity INT NOT NULL DEFAULT 0 CHECK (allocated_quantity >= 0),
    version INT NOT NULL DEFAULT 1,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS resource_reservations (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    resource_id VARCHAR(64) NOT NULL REFERENCES resources(id),
    workflow_id UUID NOT NULL REFERENCES workflows(id) ON DELETE CASCADE,
    quantity INT NOT NULL CHECK (quantity > 0),
    status VARCHAR(32) NOT NULL, -- ACTIVE, RELEASED, CONSUMED
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_reservation_resource_workflow UNIQUE(resource_id, workflow_id)
);

CREATE INDEX IF NOT EXISTS idx_reservations_workflow_id ON resource_reservations(workflow_id);

-- 4. TRANSACTIONAL OUTBOX (Dual-Write Prevention)
CREATE TABLE IF NOT EXISTS outbox_messages (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    topic VARCHAR(128) NOT NULL,
    message_key VARCHAR(128) NOT NULL,
    event_type VARCHAR(64) NOT NULL,
    payload JSONB NOT NULL,
    status VARCHAR(32) NOT NULL DEFAULT 'PENDING', -- PENDING, PUBLISHED, FAILED
    retry_count INT NOT NULL DEFAULT 0,
    error TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    published_at TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_outbox_pending ON outbox_messages(created_at) WHERE status = 'PENDING';

-- 5. IDEMPOTENT INBOX (At-Least-Once Consumer Deduplication)
CREATE TABLE IF NOT EXISTS inbox_messages (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    consumer_name VARCHAR(64) NOT NULL,
    message_id VARCHAR(128) NOT NULL,
    payload JSONB NOT NULL,
    status VARCHAR(32) NOT NULL DEFAULT 'PROCESSED',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_inbox_consumer_message UNIQUE(consumer_name, message_id)
);

CREATE INDEX IF NOT EXISTS idx_inbox_created_at ON inbox_messages(created_at);

-- 6. DEAD LETTER QUEUE (DLQ - Poison Pill Quarantine)
CREATE TABLE IF NOT EXISTS dead_letter_messages (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    workflow_id UUID REFERENCES workflows(id) ON DELETE SET NULL,
    source VARCHAR(64) NOT NULL,
    payload JSONB NOT NULL,
    exception_message TEXT NOT NULL,
    stack_trace TEXT,
    status VARCHAR(32) NOT NULL DEFAULT 'QUARANTINED', -- QUARANTINED, REPLAYED, DISCARDED
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    replayed_at TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_dlq_status ON dead_letter_messages(status);

-- Seed initial test resources
INSERT INTO resources (id, name, available_quantity, allocated_quantity)
VALUES 
    ('SKU-LAPTOP-001', 'High-Performance Workstation', 1, 0),
    ('SKU-PHONE-002', 'Enterprise Smartphone', 10, 0),
    ('SKU-DESK-003', 'Ergonomic Standing Desk', 5, 0)
ON CONFLICT (id) DO NOTHING;
