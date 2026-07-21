# Google Cloud synthetic-monitoring (synthmon)

An **on-demand** Terraform root that stands up a Lumberjacks server (the *system
under test*, SUT) and a **multi-region synthetic-client fleet** that measures real
end-to-end latency, packet loss, and transport-path distribution against it — from
several GCP regions at once. It reuses the Stage 1 pattern (VPC, IAP-only SSH,
gameplay firewall, telemetry service account, Cloud Monitoring dashboard + alerts,
budget guard) and adds the client fleet.

See the [community dashboard strategy doc](../../../docs/dashboard/index.html)
(§03 "Live geo snapshot", backlog D-14→D-17) for the results and the why.

## SUT (system under test)

`main.tf` + `observability.tf` — one Ubuntu VM in one region running the Lumberjacks
gateway. Provisioning is infra-only; deploy the code separately (copy the repo to
`/opt/lumberjacks` and run the gateway image, DB-less is fine — `region-spawn` exists
by default). Public `ws://<public_ip>:4000` + UDP 4005.

```
cp terraform.tfvars.example terraform.tfvars   # fill in project/billing/email
terraform init
terraform apply                                # SUT only (fleet_regions defaults to {})
```

## Fleet (`fleet.tf` + `scripts/fleet-client.sh.tftpl`)

`e2-micro` clients in each region in `var.fleet_regions`, each downloading the
published `tools/synthclient` binary and running it against the SUT. Results are
read off the serial console (`gcloud compute instances get-serial-port-output`,
grep `SYNTHJSON`).

```
# create fleet.auto.tfvars with the regions you want (see the .example), then:
terraform apply                                # brings up the fleet
# ... collect serial output ...
# set fleet_regions = {} (or delete fleet.auto.tfvars), then:
terraform apply                                # destroys ONLY the fleet, keeps the SUT
```

## Teardown

`terraform destroy` removes everything (SUT + any fleet). The fleet-only teardown
above lets you keep the SUT as a standing baseline between runs.

This root deliberately does not provision Cloud SQL, DNS, TLS, Secret Manager,
Artifact Registry, or automated deployment.
