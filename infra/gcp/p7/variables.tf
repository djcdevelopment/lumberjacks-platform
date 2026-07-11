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
  description = "Memory-safe VM size. n2-highmem-8 provides 64 GiB for the measured P7 workload."
  type        = string
  default     = "n2-highmem-8"
}

variable "boot_disk_size_gb" {
  description = "Boot disk size for the OS, Docker layers, and build cache."
  type        = number
  default     = 40
}

variable "data_disk_size_gb" {
  description = "Persistent disk size for Valheim, PostgreSQL, and evidence."
  type        = number
  default     = 150
}

variable "control_source_ranges" {
  description = "IPv4 CIDRs allowed to reach Lumberjacks TCP 4000 and UDP 4005. Use the OMEN egress CIDR."
  type        = list(string)

  validation {
    condition     = length(var.control_source_ranges) > 0 && alltrue([for cidr in var.control_source_ranges : can(cidrhost(cidr, 0))])
    error_message = "control_source_ranges must contain at least one valid CIDR."
  }
}

variable "valheim_source_ranges" {
  description = "IPv4 CIDRs allowed to reach the Steam-only Valheim UDP ports."
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
