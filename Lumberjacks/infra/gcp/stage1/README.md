# Google Cloud Stage 1 infrastructure

This Terraform root provisions only the infrastructure required for the first
local-to-cloud parity experiment:

- a custom VPC and subnet;
- IAP-only SSH and explicit gameplay firewall rules;
- one Ubuntu Compute Engine VM with a reserved public IPv4 address;
- one separately managed persistent disk for PostgreSQL data;
- a dedicated runtime service account limited to telemetry-writer roles; and
- an optional billing budget.

The Terraform root also creates the Stage 1 dashboard, public uptime check, six
alert policies, an operator email notification channel, and the log-based
application-error metric. The uptime check receives its current checker addresses
from the Monitoring API, so gameplay access can remain restricted to the explicit
tester CIDRs.

It deliberately does not provision Cloud SQL, DNS, TLS, Secret Manager, Artifact
Registry, Cloud Build, or automated deployment.

Use the [Stage 1 runbook](../../../docs/google-cloud-stage1-runbook.md) to provision,
deploy, validate, and remove the experiment.
