output "project_id" {
  description = "Google Cloud project containing the experiment."
  value       = var.project_id
}

output "vm_name" {
  description = "Compute Engine VM name."
  value       = google_compute_instance.stage1.name
}

output "zone" {
  description = "Compute Engine VM zone."
  value       = var.zone
}

output "public_ip" {
  description = "Reserved public IPv4 address for Gate 1 tests."
  value       = google_compute_address.stage1.address
}

output "websocket_url" {
  description = "Plaintext WebSocket URL used only for the Gate 1 parity test."
  value       = "ws://${google_compute_address.stage1.address}:4000"
}

output "iap_ssh_command" {
  description = "Command for reaching the VM without exposing SSH publicly."
  value       = "gcloud compute ssh ${google_compute_instance.stage1.name} --project ${var.project_id} --zone ${var.zone} --tunnel-through-iap"
}
