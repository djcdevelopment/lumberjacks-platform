variable "project_id" {
  description = "Existing Google Cloud project with billing enabled."
  type        = string
}

variable "region" {
  description = "Region for the experiment resources."
  type        = string
  default     = "us-west1"
}

variable "zone" {
  description = "Zone for the VM and persistent disk. Must belong to region."
  type        = string
  default     = "us-west1-b"
}

variable "machine_type" {
  description = "Compute Engine machine type for the parity VM."
  type        = string
  default     = "e2-medium"
}

variable "boot_disk_size_gb" {
  description = "Boot disk capacity in GiB."
  type        = number
  default     = 20
}

variable "data_disk_size_gb" {
  description = "Persistent PostgreSQL data disk capacity in GiB."
  type        = number
  default     = 20
}

variable "gameplay_source_ranges" {
  description = "IPv4 CIDR ranges allowed to reach TCP 4000 and UDP 4005. Narrow this when practical."
  type        = list(string)
  default     = ["0.0.0.0/0"]
}

variable "billing_account_id" {
  description = "Optional billing account ID used to create the experiment budget."
  type        = string
  default     = null
  nullable    = true
}

variable "monthly_budget_usd" {
  description = "Monthly experiment budget in whole USD when billing_account_id is set."
  type        = number
  default     = 25

  validation {
    condition     = var.monthly_budget_usd >= 1 && floor(var.monthly_budget_usd) == var.monthly_budget_usd
    error_message = "monthly_budget_usd must be a positive whole number."
  }
}

variable "alert_email" {
  description = "Email address for required Stage 1 operational alert notifications."
  type        = string

  validation {
    condition     = can(regex("^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$", var.alert_email))
    error_message = "alert_email must be a valid email address."
  }
}
