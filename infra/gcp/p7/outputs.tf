output "project_id" {
  value = var.project_id
}

output "vm_name" {
  value = google_compute_instance.p7.name
}

output "zone" {
  value = var.zone
}

output "public_ip" {
  value = google_compute_address.p7.address
}

output "valheim_endpoint" {
  value = "${google_compute_address.p7.address}:2456"
}

output "lumberjacks_gateway" {
  value = "http://${google_compute_address.p7.address}:4000"
}

output "iap_ssh_command" {
  value = "gcloud compute ssh ${google_compute_instance.p7.name} --project ${var.project_id} --zone ${var.zone} --tunnel-through-iap"
}
