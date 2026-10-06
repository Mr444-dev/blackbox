# PostgreSQL performance lab plan

No benchmark result is currently published for this repository. The schema and data generator needed for the earlier `workflow_events` benchmark are not part of the active EF Core model, so the former speedup figures and sample result must not be treated as measured evidence.

## Reproducible benchmark plan

1. Add a versioned EF Core migration for the tables and indexes under test.
2. Generate a deterministic dataset with a documented row count, data distribution, and random seed.
3. Record the exact query, PostgreSQL version, hardware/container limits, indexes, and warm/cold cache conditions.
4. Capture `EXPLAIN (ANALYZE, BUFFERS)` before and after each index change.
5. Run repeated samples, report median and tail latency, and include the raw output with the change.
6. Verify that indexes improve the measured workload without unacceptable write or storage cost.

Until those steps are completed, this document describes a proposed experiment only; it makes no latency or speedup claim.
