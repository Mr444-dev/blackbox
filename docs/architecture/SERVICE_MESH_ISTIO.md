# Istio deployment examples

`deploy/k8s/istio` contains example Kubernetes resources for an Istio gateway, virtual service, destination rule, peer authentication, service accounts, and authorization policy.

These manifests are not installed by Docker Compose and have not been validated against a live Kubernetes cluster or a pinned Istio release. They do not make the local API/worker deployment production-ready by themselves. Review namespaces, selectors, certificates, gateway hosts, authorization principals, and resource limits before adapting them.

The example Gateway accepts HTTPS only. Provision the referenced TLS credential and the `blackbox-runtime` Kubernetes Secret outside this repository before applying the workloads. The Secret must contain `connection-string` and `api-key` keys; do not put either value in a ConfigMap or commit a Secret manifest containing real credentials. See [the Kubernetes deployment notes](../../deploy/k8s/README.md).

## Validation checklist

1. Pin and document Kubernetes and Istio versions.
2. Run `istioctl analyze` against the target cluster and namespace.
3. Apply in a disposable cluster and verify mTLS, routing, and denial behavior.
4. Add deployment tests and an upgrade/rollback procedure.

Start with the resources in this directory; treat them as configuration examples rather than a tested platform feature.
