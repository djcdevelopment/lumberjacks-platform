locals {
  notification_channels = var.alert_email == null ? [] : [google_monitoring_notification_channel.email[0].name]
}

resource "google_monitoring_notification_channel" "email" {
  count = var.alert_email == null ? 0 : 1

  display_name = "Lumberjacks operator email"
  type         = "email"
  labels = {
    email_address = var.alert_email
  }

  depends_on = [google_project_service.required]
}

resource "google_monitoring_uptime_check_config" "gateway" {
  display_name = "Lumberjacks gateway health"
  timeout      = "10s"
  period       = "60s"

  monitored_resource {
    type = "uptime_url"
    labels = {
      host       = google_compute_address.stage1.address
      project_id = var.project_id
    }
  }

  http_check {
    path           = "/health"
    port           = 4000
    request_method = "GET"
    use_ssl        = false
    validate_ssl   = false
  }

  content_matchers {
    content = "\"status\":\"ok\""
    matcher = "CONTAINS_STRING"
  }

  depends_on = [google_project_service.required]
}

resource "google_logging_metric" "application_errors" {
  name        = "lumberjacks_application_errors"
  description = "Error and critical log entries emitted by Lumberjacks services."
  filter      = <<-FILTER
    resource.type="gce_instance"
    severity>=ERROR
    jsonPayload.service=~"lumberjacks-.*"
  FILTER

  metric_descriptor {
    metric_kind  = "DELTA"
    value_type   = "INT64"
    unit         = "1"
    display_name = "Lumberjacks application errors"
  }

  depends_on = [google_project_service.required]
}

resource "google_monitoring_alert_policy" "gateway_unavailable" {
  display_name          = "Lumberjacks gateway unavailable"
  combiner              = "OR"
  notification_channels = local.notification_channels

  conditions {
    display_name = "Public health check is failing"

    condition_threshold {
      filter          = "metric.type=\"monitoring.googleapis.com/uptime_check/check_passed\" AND resource.type=\"uptime_url\" AND metric.label.check_id=\"${google_monitoring_uptime_check_config.gateway.uptime_check_id}\""
      comparison      = "COMPARISON_LT"
      threshold_value = 1
      duration        = "120s"

      aggregations {
        alignment_period   = "60s"
        per_series_aligner = "ALIGN_NEXT_OLDER"
      }

      trigger {
        percent = 0.5
      }
    }
  }

  alert_strategy {
    auto_close = "1800s"
  }

  documentation {
    content   = "The public Gateway /health endpoint has failed from at least half of Google Cloud's uptime-check locations for two minutes. Check the VM, Docker Compose stack, disk capacity, and recent deployments."
    mime_type = "text/markdown"
  }
}

resource "google_monitoring_alert_policy" "vm_cpu" {
  display_name          = "Lumberjacks VM sustained CPU saturation"
  combiner              = "OR"
  notification_channels = local.notification_channels

  conditions {
    display_name = "CPU usage above 80% for 10 minutes"

    condition_threshold {
      filter          = "metric.type=\"agent.googleapis.com/cpu/utilization\" AND resource.type=\"gce_instance\" AND metric.label.cpu_state=\"used\""
      comparison      = "COMPARISON_GT"
      threshold_value = 0.8
      duration        = "600s"

      aggregations {
        alignment_period   = "60s"
        per_series_aligner = "ALIGN_MEAN"
      }
    }
  }

  alert_strategy {
    auto_close = "1800s"
  }

  documentation {
    content   = "The single authoritative VM has sustained CPU saturation. Correlate with tick duration, active sessions, and deployment activity before resizing."
    mime_type = "text/markdown"
  }
}

resource "google_monitoring_alert_policy" "vm_memory" {
  display_name          = "Lumberjacks VM sustained memory pressure"
  combiner              = "OR"
  notification_channels = local.notification_channels

  conditions {
    display_name = "Memory used above 85% for 10 minutes"

    condition_threshold {
      filter          = "metric.type=\"agent.googleapis.com/memory/percent_used\" AND resource.type=\"gce_instance\" AND metric.label.state=\"used\""
      comparison      = "COMPARISON_GT"
      threshold_value = 85
      duration        = "600s"

      aggregations {
        alignment_period   = "60s"
        per_series_aligner = "ALIGN_MEAN"
      }
    }
  }

  alert_strategy {
    auto_close = "1800s"
  }

  documentation {
    content   = "Memory pressure is sustained on the authoritative VM. Inspect container memory, .NET runtime metrics, and PostgreSQL before resizing or separating services."
    mime_type = "text/markdown"
  }
}

resource "google_monitoring_alert_policy" "disk_capacity" {
  display_name          = "Lumberjacks disk capacity risk"
  combiner              = "OR"
  notification_channels = local.notification_channels

  conditions {
    display_name = "A filesystem is above 85% used for 10 minutes"

    condition_threshold {
      filter          = "metric.type=\"agent.googleapis.com/disk/percent_used\" AND resource.type=\"gce_instance\""
      comparison      = "COMPARISON_GT"
      threshold_value = 85
      duration        = "600s"

      aggregations {
        alignment_period   = "60s"
        per_series_aligner = "ALIGN_MEAN"
      }
    }
  }

  alert_strategy {
    auto_close = "1800s"
  }

  documentation {
    content   = "A VM filesystem is nearing capacity. Check Docker layers/logs and PostgreSQL growth; snapshot and resize the persistent disk if required."
    mime_type = "text/markdown"
  }
}

resource "google_monitoring_alert_policy" "application_errors" {
  display_name          = "Lumberjacks application errors"
  combiner              = "OR"
  notification_channels = local.notification_channels

  conditions {
    display_name = "Application emitted error logs"

    condition_threshold {
      filter          = "metric.type=\"logging.googleapis.com/user/${google_logging_metric.application_errors.name}\" AND resource.type=\"gce_instance\""
      comparison      = "COMPARISON_GT"
      threshold_value = 0
      duration        = "0s"

      aggregations {
        alignment_period     = "300s"
        per_series_aligner   = "ALIGN_DELTA"
        cross_series_reducer = "REDUCE_SUM"
      }

      trigger {
        count = 1
      }
    }
  }

  alert_strategy {
    auto_close = "1800s"
  }

  documentation {
    content   = "One or more Lumberjacks services emitted ERROR or CRITICAL logs. Follow the trace ID from the structured log into Cloud Trace and inspect the correlated request or gameplay operation."
    mime_type = "text/markdown"
  }
}

resource "google_monitoring_alert_policy" "ops_agent_absent" {
  display_name          = "Lumberjacks telemetry pipeline absent"
  combiner              = "OR"
  notification_channels = local.notification_channels

  conditions {
    display_name = "Ops Agent stopped reporting"

    condition_absent {
      filter   = "metric.type=\"agent.googleapis.com/agent/uptime\" AND resource.type=\"gce_instance\""
      duration = "300s"

      aggregations {
        alignment_period   = "60s"
        per_series_aligner = "ALIGN_RATE"
      }
    }
  }

  alert_strategy {
    auto_close = "1800s"
  }

  documentation {
    content   = "The Ops Agent has stopped reporting for five minutes. Check google-cloud-ops-agent services, configuration validation, VM connectivity, and service-account IAM."
    mime_type = "text/markdown"
  }
}

resource "google_monitoring_dashboard" "operations" {
  dashboard_json = jsonencode({
    displayName = "Lumberjacks — Gate 1 Operations"
    gridLayout = {
      columns = 2
      widgets = [
        {
          title = "VM CPU utilization"
          xyChart = {
            dataSets = [{
              plotType = "LINE"
              timeSeriesQuery = {
                timeSeriesFilter = {
                  filter = "metric.type=\"agent.googleapis.com/cpu/utilization\" AND resource.type=\"gce_instance\" AND metric.label.cpu_state=\"used\""
                  aggregation = {
                    alignmentPeriod  = "60s"
                    perSeriesAligner = "ALIGN_MEAN"
                  }
                }
              }
            }]
            yAxis = { label = "utilization", scale = "LINEAR" }
          }
        },
        {
          title = "VM memory used"
          xyChart = {
            dataSets = [{
              plotType = "LINE"
              timeSeriesQuery = {
                timeSeriesFilter = {
                  filter = "metric.type=\"agent.googleapis.com/memory/percent_used\" AND resource.type=\"gce_instance\" AND metric.label.state=\"used\""
                  aggregation = {
                    alignmentPeriod  = "60s"
                    perSeriesAligner = "ALIGN_MEAN"
                  }
                }
              }
            }]
            yAxis = { label = "percent", scale = "LINEAR" }
          }
        },
        {
          title = "Simulation tick duration (p99)"
          xyChart = {
            dataSets = [{
              plotType = "LINE"
              timeSeriesQuery = {
                timeSeriesFilter = {
                  filter = "metric.type=\"workload.googleapis.com/lumberjacks.tick.duration\" AND resource.type=\"gce_instance\""
                  aggregation = {
                    alignmentPeriod  = "60s"
                    perSeriesAligner = "ALIGN_PERCENTILE_99"
                  }
                }
              }
            }]
            thresholds = [{ value = 50 }]
            yAxis      = { label = "milliseconds", scale = "LINEAR" }
          }
        },
        {
          title = "Active gameplay sessions"
          xyChart = {
            dataSets = [{
              plotType = "LINE"
              timeSeriesQuery = {
                timeSeriesFilter = {
                  filter = "metric.type=\"workload.googleapis.com/lumberjacks.sessions.active\" AND resource.type=\"gce_instance\""
                  aggregation = {
                    alignmentPeriod  = "60s"
                    perSeriesAligner = "ALIGN_MAX"
                  }
                }
              }
            }]
            yAxis = { label = "sessions", scale = "LINEAR" }
          }
        },
        {
          title = "Application logs"
          logsPanel = {
            filter = "resource.type=\"gce_instance\" jsonPayload.service=~\"lumberjacks-.*\""
          }
        },
        {
          title = "Open incidents"
          incidentList = {
            monitoredResources = [{ type = "gce_instance" }, { type = "uptime_url" }]
          }
        }
      ]
    }
  })

  depends_on = [google_project_service.required]

  # The Monitoring API injects name, etag, targetAxis, and string-encoded grid
  # defaults into dashboard_json, producing a perpetual non-semantic diff.
  # Replace this resource explicitly when intentionally changing the dashboard.
  lifecycle {
    ignore_changes = [dashboard_json]
  }
}
