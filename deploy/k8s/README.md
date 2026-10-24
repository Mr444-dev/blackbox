# Kubernetes deployment example

This directory contains deployment examples, not a verified, self-contained Kubernetes release. The manifests expect PostgreSQL and Kafka endpoints, application images, service accounts, and an Istio installation to be provided by the target environment. No live cluster validation is claimed.

## Runtime secrets

The API and worker read their PostgreSQL connection string from a Kubernetes Secret named `blackbox-runtime`. The API also requires an API key of at least 32 UTF-8 bytes from that Secret. The checked-in manifests contain only `secretKeyRef` references; create the Secret through your cluster's approved secret manager or inject it from files kept outside this repository.

The Secret must provide these keys:

| Key | Consumer | Contents |
| --- | --- | --- |
| `connection-string` | API and worker | PostgreSQL connection string for the target environment |
| `api-key` | API | Random API key with at least 32 UTF-8 bytes |

Do not commit a local secret file or a Secret YAML containing real values. Kubernetes Secret objects are base64-encoded by default; configure encryption at rest and restrict RBAC in the target cluster.

## Apply order and dependencies

1. Provision PostgreSQL, Kafka, and the required topics. The base manifest does not create those dependencies.
2. Create the namespace so your secret manager can inject the Secret before pods are scheduled:

   ```bash
   kubectl create namespace blackbox --dry-run=client -o yaml | kubectl apply -f -
   ```

3. Create the `blackbox-runtime` Secret through the approved secret workflow.
4. Build and publish the API and worker images, then update the image references in `blackbox-stack.yaml`.
5. Apply the workload manifest after the Secret exists:

   ```bash
   kubectl apply -f blackbox-stack.yaml
   ```

4. If using the optional Istio overlay, install Istio, create the TLS credential referenced by `gateway.yaml`, review the hosts and policies, and apply the overlay. The example Gateway accepts HTTPS only; it does not expose port 80.

The API readiness probe calls `/health/ready`, which includes the PostgreSQL check. The liveness probe uses `/health` so a temporary database outage does not restart otherwise healthy API processes.

These manifests still require environment-specific review and cluster validation before use beyond a local demonstration.
