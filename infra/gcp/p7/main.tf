locals {
  name = "comfy-lumberjacks-p7"
  labels = {
    application = "comfy-network-replacement"
    environment = "p7"
    managed_by  = "terraform"
  }
}

data "google_project" "current" {
  project_id = var.project_id
}

resource "google_project_service" "required" {
  for_each = toset([
    "billingbudgets.googleapis.com",
    "cloudresourcemanager.googleapis.com",
    "cloudtrace.googleapis.com",
    "compute.googleapis.com",
    "iam.googleapis.com",
    "logging.googleapis.com",
    "monitoring.googleapis.com",
    "serviceusage.googleapis.com",
  ])

  project            = var.project_id
  service            = each.value
  disable_on_destroy = false
}

resource "google_compute_network" "p7" {
  name                    = local.name
  auto_create_subnetworks = false
  depends_on              = [google_project_service.required]
}

resource "google_compute_subnetwork" "p7" {
  name          = local.name
  region        = var.region
  network       = google_compute_network.p7.id
  ip_cidr_range = "10.27.0.0/24"
}

resource "google_compute_firewall" "iap_ssh" {
  name          = "${local.name}-iap-ssh"
  network       = google_compute_network.p7.name
  direction     = "INGRESS"
  source_ranges = ["35.235.240.0/20"]
  target_tags   = [local.name]

  allow {
    protocol = "tcp"
    ports    = ["22"]
  }
}

resource "google_compute_firewall" "valheim" {
  name          = "${local.name}-valheim"
  network       = google_compute_network.p7.name
  direction     = "INGRESS"
  source_ranges = var.valheim_source_ranges
  target_tags   = [local.name]

  allow {
    protocol = "udp"
    ports    = ["2456-2457"]
  }
}

resource "google_compute_firewall" "lumberjacks_player" {
  name          = "${local.name}-player-gateway"
  network       = google_compute_network.p7.name
  direction     = "INGRESS"
  source_ranges = var.lumberjacks_player_source_ranges
  target_tags   = [local.name]

  allow {
    protocol = "tcp"
    ports    = [tostring(var.lumberjacks_player_port)]
  }
}

resource "google_compute_address" "p7" {
  name       = local.name
  region     = var.region
  depends_on = [google_project_service.required]
}

resource "google_service_account" "runtime" {
  account_id   = "comfy-lumberjacks-p7"
  display_name = "Comfy and Lumberjacks P7 runtime"
  depends_on   = [google_project_service.required]
}

resource "google_project_iam_member" "runtime_observability" {
  for_each = toset([
    "roles/cloudtrace.agent",
    "roles/logging.logWriter",
    "roles/monitoring.metricWriter",
  ])

  project = var.project_id
  role    = each.value
  member  = google_service_account.runtime.member
}

resource "google_compute_resource_policy" "daily_snapshot" {
  name   = "${local.name}-daily-snapshot"
  region = var.region

  snapshot_schedule_policy {
    schedule {
      daily_schedule {
        days_in_cycle = 1
        start_time    = "10:00"
      }
    }

    retention_policy {
      max_retention_days    = 7
      on_source_disk_delete = "KEEP_AUTO_SNAPSHOTS"
    }

    snapshot_properties {
      labels = local.labels
    }
  }

  depends_on = [google_project_service.required]
}

resource "google_compute_disk" "state" {
  name                      = "${local.name}-state"
  type                      = "pd-balanced"
  zone                      = var.zone
  size                      = var.data_disk_size_gb
  labels                    = local.labels
  physical_block_size_bytes = 4096

  lifecycle {
    prevent_destroy = true
  }

  depends_on = [google_project_service.required]
}

resource "google_compute_disk_resource_policy_attachment" "state_snapshot" {
  name = google_compute_resource_policy.daily_snapshot.name
  disk = google_compute_disk.state.name
  zone = var.zone
}

resource "google_compute_instance" "p7" {
  name         = local.name
  machine_type = var.machine_type
  zone         = var.zone
  tags         = [local.name]
  labels       = local.labels

  allow_stopping_for_update = true
  deletion_protection       = false

  boot_disk {
    auto_delete = true
    initialize_params {
      # Minimal variant: bootstrap.sh.tftpl installs everything it needs via apt.
      # Changing the image forces instance replacement — apply only in a planned
      # rebuild window, never casually (the docker image store lives on this disk).
      image = "ubuntu-os-cloud/ubuntu-minimal-2404-lts-amd64"
      size  = var.boot_disk_size_gb
      type  = "pd-balanced"
    }
  }

  attached_disk {
    source      = google_compute_disk.state.id
    device_name = "comfy-p7-state"
    mode        = "READ_WRITE"
  }

  network_interface {
    subnetwork = google_compute_subnetwork.p7.id
    access_config {
      nat_ip = google_compute_address.p7.address
    }
  }

  metadata = {
    enable-oslogin = "TRUE"
  }

  metadata_startup_script = templatefile("${path.module}/scripts/bootstrap.sh.tftpl", {
    ops_agent_config = file("${path.module}/ops-agent-config.yaml")
  })

  service_account {
    email  = google_service_account.runtime.email
    scopes = ["cloud-platform"]
  }

  shielded_instance_config {
    enable_secure_boot          = true
    enable_vtpm                 = true
    enable_integrity_monitoring = true
  }

  depends_on = [
    google_compute_disk_resource_policy_attachment.state_snapshot,
    google_project_iam_member.runtime_observability,
  ]
}

resource "google_billing_budget" "p7" {
  count = var.billing_account_id == null ? 0 : 1

  billing_account = var.billing_account_id
  display_name    = "Comfy Lumberjacks P7 monthly budget"

  budget_filter {
    projects = ["projects/${data.google_project.current.number}"]
  }

  amount {
    specified_amount {
      currency_code = "USD"
      units         = tostring(var.monthly_budget_usd)
    }
  }

  threshold_rules { threshold_percent = 0.5 }
  threshold_rules { threshold_percent = 0.9 }
  threshold_rules { threshold_percent = 1.0 }

  depends_on = [google_project_service.required]
}
