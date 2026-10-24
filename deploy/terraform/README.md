# Terraform deployment example

The Terraform files are infrastructure examples. They have not been applied or validated against a live Azure subscription. Review the plan, region, and resource costs before provisioning; the configuration creates billable AKS, PostgreSQL, networking, and Key Vault resources.

## PostgreSQL administrator password

`postgres_admin_password` is a required sensitive Terraform variable with no source default. Supply it through an approved deployment secret mechanism, such as a protected CI secret or a secured shell environment variable named `TF_VAR_postgres_admin_password`. Never place the value in a tracked `.tfvars` file.

Terraform's `sensitive` marker redacts normal CLI output; it does not remove the password from Terraform state. Configure a secured remote state backend with encryption, access controls, and locking before applying this configuration. The repository ignores local state, plans, and variable files as a defense against accidental commits, but `.gitignore` is not a substitute for protecting state.

Run `terraform plan` and review its output before any `terraform apply`. Rotate the administrator password if an earlier revision of this configuration was applied. The Key Vault resource in `main.tf` is not currently wired to the PostgreSQL password input.
