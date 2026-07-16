locals {
  notification_channels = [google_monitoring_notification_channel.operator.name]
}

resource "google_monitoring_notification_channel" "operator" {
  display_name = "Comfy P7 operator email"
  type         = "email"
  labels = {
    email_address = var.alert_email
  }
  depends_on = [google_project_service.required]
}

resource "google_logging_metric" "host_oom_kill" {
  name        = "comfy_p7_host_oom_kill"
  description = "Linux kernel out-of-memory kill on the P7 VM."
  filter      = <<-FILTER
    resource.type="gce_instance"
    (textPayload=~"Out of memory: Killed process|oom-kill" OR jsonPayload.message=~"Out of memory: Killed process|oom-kill")
  FILTER

  metric_descriptor {
    metric_kind  = "DELTA"
    value_type   = "INT64"
    unit         = "1"
    display_name = "Comfy P7 host OOM kills"
  }

  depends_on = [google_project_service.required]
}

resource "google_monitoring_alert_policy" "memory_pressure" {
  display_name          = "Comfy P7 memory pressure"
  combiner              = "OR"
  notification_channels = local.notification_channels

  conditions {
    display_name = "Memory above 90 percent for 5 minutes"
    condition_threshold {
      filter          = "metric.type=\"agent.googleapis.com/memory/percent_used\" AND resource.type=\"gce_instance\" AND metric.label.state=\"used\""
      comparison      = "COMPARISON_GT"
      threshold_value = 90
      duration        = "300s"
      aggregations {
        alignment_period   = "60s"
        per_series_aligner = "ALIGN_MEAN"
      }
    }
  }

  alert_strategy { auto_close = "1800s" }
}

resource "google_monitoring_alert_policy" "swap_pressure" {
  display_name          = "Comfy P7 swap pressure"
  combiner              = "OR"
  notification_channels = local.notification_channels

  conditions {
    display_name = "Swap above 25 percent for 5 minutes"
    condition_threshold {
      filter          = "metric.type=\"agent.googleapis.com/swap/percent_used\" AND resource.type=\"gce_instance\" AND metric.label.state=\"used\""
      comparison      = "COMPARISON_GT"
      threshold_value = 25
      duration        = "300s"
      aggregations {
        alignment_period   = "60s"
        per_series_aligner = "ALIGN_MEAN"
      }
    }
  }

  alert_strategy { auto_close = "1800s" }
}

resource "google_monitoring_alert_policy" "oom_kill" {
  display_name          = "Comfy P7 OOM kill detected"
  combiner              = "OR"
  notification_channels = local.notification_channels

  conditions {
    display_name = "Kernel emitted an OOM kill"
    condition_threshold {
      filter          = "metric.type=\"logging.googleapis.com/user/${google_logging_metric.host_oom_kill.name}\" AND resource.type=\"gce_instance\""
      comparison      = "COMPARISON_GT"
      threshold_value = 0
      duration        = "0s"
      aggregations {
        alignment_period     = "60s"
        per_series_aligner   = "ALIGN_DELTA"
        cross_series_reducer = "REDUCE_SUM"
      }
      trigger { count = 1 }
    }
  }

  alert_strategy { auto_close = "1800s" }
}

resource "google_monitoring_alert_policy" "disk_capacity" {
  display_name          = "Comfy P7 disk capacity"
  combiner              = "OR"
  notification_channels = local.notification_channels

  conditions {
    display_name = "Filesystem above 85 percent for 10 minutes"
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

  alert_strategy { auto_close = "1800s" }
}

resource "google_monitoring_alert_policy" "ops_agent_absent" {
  display_name          = "Comfy P7 telemetry absent"
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

  alert_strategy { auto_close = "1800s" }
}
