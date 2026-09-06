variable "project_id" {
  description = "Google Cloud project for the combined Comfy/Lumberjacks P7 environment."
  type        = string
}

variable "region" {
  description = "Region for the P7 VM and reserved address."
  type        = string
  default     = "us-west1"
}

variable "zone" {
  description = "Zone for the P7 VM and persistent disk."
  type        = string
  default     = "us-west1-b"
}

variable "machine_type" {
  description = "P7 VM size. Downsized to n2-highmem-2 (16 GiB) on 2026-07-23 for cost; n2-highmem-8 was the original memory-safe sizing (ADR-0004) and should be restored only for heavy playtest sessions."
  type        = string
  default     = "n2-highmem-2"
}

variable "boot_disk_size_gb" {
  # 20, down from 40. Real usage after the 2026-09-06 reclamation is ~14 GiB: OS+apt 2.4,
  # /var (docker images) ~7, swapfile 4. The old 40 was carrying a 16 GiB swapfile and
  # 5.7 GiB of docker build cache, neither of which is load-bearing.
  # NOTE: docker build cache regrows. `docker builder prune -af` is the maintenance knob;
  # without it this disk fills again and 20 GiB is not generous.
  description = "Boot disk size for the OS, Docker layers, and build cache."
  type        = number
  default     = 20
}

variable "data_disk_size_gb" {
  # 15, down from a 150 default that never matched anything deployed: the live disk was 32 GiB
  # (comfy-lumberjacks-p7-state-v2) and is now 15 GiB (comfy-p7-state-v3, rebuilt 2026-09-06).
  # Real usage is 8.4 GiB: Valheim world+server 7.5, lumberjacks 766 MiB (of which
  # boundary-events 705 MiB and postgres 47 MiB), backups 124 MiB.
  # boundary-events grows without bound — it is append-only evidence. Watch it.
  description = "Persistent disk size for Valheim, PostgreSQL, and evidence."
  type        = number
  default     = 15
}

variable "valheim_source_ranges" {
  description = "IPv4 CIDRs allowed to reach the Steam-only Valheim UDP ports."
  type        = list(string)
  default     = ["0.0.0.0/0"]
}

variable "lumberjacks_player_port" {
  description = "Authenticated direct TCP port used by enrolled ComfyNetworkSense clients."
  type        = number
  default     = 42317
}

variable "lumberjacks_player_udp_port" {
  description = "Session-token-authenticated Lumberjacks datagram port used by enrolled clients."
  type        = number
  default     = 4005
}

variable "lumberjacks_player_source_ranges" {
  description = "IPv4 CIDRs allowed to reach the authenticated Lumberjacks player port."
  type        = list(string)
  default     = ["0.0.0.0/0"]
}

variable "lumberjacks_tls_source_ranges" {
  description = <<-EOT
    IPv4 CIDRs allowed to reach the TLS volunteer endpoint (80 and 443).

    Narrowing this breaks certificate issuance: Let's Encrypt answers HTTP-01 from arbitrary
    validator addresses, so port 80 must stay reachable from the internet or renewal silently fails
    and the endpoint drops off TLS ~60 days later, long after anyone connects the two events. Use
    DNS-01 instead if this must be restricted.
  EOT
  type        = list(string)
  default     = ["0.0.0.0/0"]
}

variable "alert_email" {
  description = "Operator address for memory, swap, OOM-risk, disk, agent, and gateway alerts."
  type        = string

  validation {
    condition     = can(regex("^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$", var.alert_email))
    error_message = "alert_email must be a valid email address."
  }
}

variable "billing_account_id" {
  description = "Optional billing account used to create a project-scoped monthly budget."
  type        = string
  default     = null
  nullable    = true
}

variable "monthly_budget_usd" {
  description = "Monthly budget alert amount when billing_account_id is set."
  type        = number
  default     = 250
}
