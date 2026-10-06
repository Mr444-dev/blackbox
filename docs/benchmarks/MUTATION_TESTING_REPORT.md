# Mutation testing

This repository contains `stryker-config.json`, but no verified Stryker.NET run or current mutation score is included. Previously circulated mutant counts and scores were not tied to a reproducible result artifact and have been removed.

## Run and record a result

Use the Stryker.NET version pinned for the environment and run it against `src/Blackbox.Core` with `tests/Blackbox.UnitTests`. Save the generated report as a CI artifact. A published score should include the command, package/tool version, mutation score, killed/survived/timeout/error counts, exclusions, and a link to the raw report.

Mutation score is a test-quality signal, not a substitute for integration, contract, or security testing. Do not add a numeric score here until a fresh run has been reviewed.
