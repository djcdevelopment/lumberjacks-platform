locals {
  name = "lumberjacks-stage1"
}

data "google_project" "current" {
  project_id = var.project_id
}

resource "google_project_service" "required" {
  for_each = toset([
    "billingbudgets.googleapis.com",
    "clouderrorreporting.googleapis.com",
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

resource "google_compute_network" "stage1" {
  name                    = local.name
  auto_create_subnetworks = false

  depends_on = [google_project_service.required]
}

resource "google_compute_subnetwork" "stage1" {
  name          = local.name
  ip_cidr_range = "10.20.0.0/24"
  region        = var.region
  network       = google_compute_network.stage1.id
}

resource "google_compute_firewall" "iap_ssh" {
  name      = "${local.name}-iap-ssh"
  network   = google_compute_network.stage1.name
  direction = "INGRESS"

  source_ranges = ["35.235.240.0/20"]
  target_tags   = [local.name]

  allow {
    protocol = "tcp"
    ports    = ["22"]
  }
}

resource "google_compute_firewall" "gameplay" {
  name      = "${local.name}-gameplay"
  network   = google_compute_network.stage1.name
  direction = "INGRESS"

  source_ranges = var.gameplay_source_ranges
  target_tags   = [local.name]

  allow {
    protocol = "tcp"
    ports    = ["4000"]
  }

  allow {
    protocol = "udp"
    ports    = ["4005"]
  }
}

data "google_monitoring_uptime_check_ips" "public" {
  depends_on = [google_project_service.required]
}

resource "google_compute_firewall" "uptime_check" {
  name      = "${local.name}-uptime-check"
  network   = google_compute_network.stage1.name
  direction = "INGRESS"

  # Public uptime checks originate from a changing set of individual addresses.
  # Read the authoritative list during each plan instead of widening the gameplay
  # rule or copying addresses into configuration that can silently become stale.
  source_ranges = [
    for checker in data.google_monitoring_uptime_check_ips.public.uptime_check_ips :
    "${checker.ip_address}/32" if !strcontains(checker.ip_address, ":")
  ]
  target_tags = [local.name]

  allow {
    protocol = "tcp"
    ports    = ["4000"]
  }
}

resource "google_compute_address" "stage1" {
  name   = local.name
  region = var.region

  depends_on = [google_project_service.required]
}

resource "google_service_account" "runtime" {
  account_id   = "lumberjacks-stage1"
  display_name = "Lumberjacks Stage 1 runtime"

  depends_on = [google_project_service.required]
}

resource "google_project_iam_member" "runtime_observability" {
  for_each = toset([
    "roles/cloudtrace.agent",
    "roles/errorreporting.writer",
    "roles/logging.logWriter",
    "roles/monitoring.metricWriter",
  ])

  project = var.project_id
  role    = each.value
  member  = google_service_account.runtime.member
}

resource "google_compute_disk" "postgres" {
  name = "${local.name}-postgres"
  type = "pd-balanced"
  zone = var.zone
  size = var.data_disk_size_gb

  physical_block_size_bytes = 4096

  depends_on = [google_project_service.required]
}

resource "google_compute_instance" "stage1" {
  name         = local.name
  machine_type = var.machine_type
  zone         = var.zone
  tags         = [local.name]

  allow_stopping_for_update = true
  deletion_protection       = false

  boot_disk {
    auto_delete = true

    initialize_params {
      image = "ubuntu-os-cloud/ubuntu-2404-lts-amd64"
      size  = var.boot_disk_size_gb
      type  = "pd-balanced"
    }
  }

  attached_disk {
    source      = google_compute_disk.postgres.id
    device_name = "lumberjacks-data"
    mode        = "READ_WRITE"
  }

  network_interface {
    subnetwork = google_compute_subnetwork.stage1.id

    access_config {
      nat_ip = google_compute_address.stage1.address
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
    google_project_iam_member.runtime_observability,
    google_project_service.required,
  ]
}

resource "google_billing_budget" "experiment" {
  count = var.billing_account_id == null ? 0 : 1

  billing_account = var.billing_account_id
  display_name    = "Lumberjacks Stage 1 monthly budget"

  budget_filter {
    projects = ["projects/${data.google_project.current.number}"]
  }

  amount {
    specified_amount {
      currency_code = "USD"
      units         = tostring(var.monthly_budget_usd)
    }
  }

  threshold_rules {
    threshold_percent = 0.5
  }

  threshold_rules {
    threshold_percent = 0.9
  }

  threshold_rules {
    threshold_percent = 1.0
  }

  depends_on = [google_project_service.required]
}
