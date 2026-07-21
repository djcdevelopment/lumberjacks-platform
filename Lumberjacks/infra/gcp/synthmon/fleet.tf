# Synthetic-client fleet — multi-region latency probes against the live SUT.
#
# All fleet resources are gated on var.fleet_regions (map of region => zone).
# Set fleet_regions = {} to destroy ONLY the fleet, leaving the SUT's resources
# untouched. Each VM downloads the public synthclient binary from GCS, runs a
# json/binary/udp probe sweep against the SUT, and prints results to its serial
# console (SYNTHJSON lines).

variable "fleet_regions" {
  description = "Map of region => zone for the synthetic-client fleet. Empty destroys the fleet."
  type        = map(string)
  default     = {}
}

variable "sut_ws_url" {
  description = "WebSocket URL of the live SUT gateway that the fleet measures."
  type        = string
  default     = "ws://136.118.58.123:4000/"
}

variable "synthclient_url" {
  description = "Public URL of the self-contained synthclient binary in GCS."
  type        = string
  default     = "https://storage.googleapis.com/lumberjacks-synthmon-artifacts/bin/synthclient"
}

locals {
  # Deterministic per-region index (0-based, sorted by region key) so each
  # fleet subnet gets a stable, non-colliding CIDR 10.30.<index>.0/24.
  fleet_region_index = {
    for idx, region in sort(keys(var.fleet_regions)) : region => idx
  }
}

resource "google_compute_subnetwork" "fleet" {
  for_each = var.fleet_regions

  name          = "${local.name}-fleet-${each.key}"
  ip_cidr_range = "10.30.${local.fleet_region_index[each.key]}.0/24"
  region        = each.key
  network       = google_compute_network.stage1.id
}

# Egress-only fleet: no inbound rules needed (probes are outbound to the SUT).
# The default-deny ingress on a custom network already blocks unsolicited inbound.

resource "google_compute_instance" "fleet" {
  for_each = var.fleet_regions

  name         = "${local.name}-fleet-${each.key}"
  machine_type = "e2-micro"
  zone         = each.value
  tags         = ["synth-client"]

  allow_stopping_for_update = true
  deletion_protection       = false

  boot_disk {
    auto_delete = true

    initialize_params {
      image = "ubuntu-os-cloud/ubuntu-2404-lts-amd64"
      size  = 20
      type  = "pd-balanced"
    }
  }

  network_interface {
    subnetwork = google_compute_subnetwork.fleet[each.key].id

    access_config {} # ephemeral external IP for outbound reachability
  }

  metadata_startup_script = templatefile("${path.module}/scripts/fleet-client.sh.tftpl", {
    region_label     = each.key
    sut_ws_url       = var.sut_ws_url
    synthclient_url  = var.synthclient_url
    ops_agent_config = file("${path.module}/ops-agent-config.yaml")
  })

  # Reuse the SUT runtime SA (holds roles/monitoring.metricWriter) so the fleet's
  # Ops Agent can write the synthclient OTLP gauges into Cloud Monitoring.
  service_account {
    email  = google_service_account.runtime.email
    scopes = ["cloud-platform"]
  }

  shielded_instance_config {
    enable_secure_boot          = true
    enable_vtpm                 = true
    enable_integrity_monitoring = true
  }

  depends_on = [google_project_service.required]
}
