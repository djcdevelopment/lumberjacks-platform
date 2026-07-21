# Standing "Geo Latency" dashboard (backlog item D-17) for the multi-region
# synthetic-client fleet. The fleet's Ops Agent forwards the synthclient OTLP
# gauges into Cloud Monitoring as workload.googleapis.com/synthclient.* metrics,
# each carrying region + transport labels. This dashboard is a PERMANENT resource:
# it survives fleet teardown (fleet_regions = {}) so the last-observed geo latency
# stays visible and re-runs land straight onto it.

resource "google_monitoring_dashboard" "synthclient_geo_latency" {
  dashboard_json = jsonencode({
    displayName = "Synthclient — Geo Latency"
    gridLayout = {
      columns = 2
      widgets = [
        {
          title = "RTT p50 by region (ms)"
          xyChart = {
            dataSets = [{
              plotType   = "LINE"
              legendTemplate = "$${metric.label.region} / $${metric.label.transport}"
              timeSeriesQuery = {
                timeSeriesFilter = {
                  filter = "metric.type=\"workload.googleapis.com/synthclient.rtt_p50_ms\" AND resource.type=\"gce_instance\""
                  aggregation = {
                    alignmentPeriod    = "60s"
                    perSeriesAligner   = "ALIGN_MEAN"
                    crossSeriesReducer = "REDUCE_MEAN"
                    groupByFields      = ["metric.label.region", "metric.label.transport"]
                  }
                }
              }
            }]
            yAxis = { label = "milliseconds", scale = "LINEAR" }
          }
        },
        {
          title = "RTT p99 by region (ms)"
          xyChart = {
            dataSets = [{
              plotType   = "LINE"
              legendTemplate = "$${metric.label.region} / $${metric.label.transport}"
              timeSeriesQuery = {
                timeSeriesFilter = {
                  filter = "metric.type=\"workload.googleapis.com/synthclient.rtt_p99_ms\" AND resource.type=\"gce_instance\""
                  aggregation = {
                    alignmentPeriod    = "60s"
                    perSeriesAligner   = "ALIGN_MEAN"
                    crossSeriesReducer = "REDUCE_MEAN"
                    groupByFields      = ["metric.label.region", "metric.label.transport"]
                  }
                }
              }
            }]
            yAxis = { label = "milliseconds", scale = "LINEAR" }
          }
        },
        {
          title = "RTT p99 by region (latest, bar)"
          xyChart = {
            dataSets = [{
              plotType = "STACKED_BAR"
              timeSeriesQuery = {
                timeSeriesFilter = {
                  filter = "metric.type=\"workload.googleapis.com/synthclient.rtt_p99_ms\" AND resource.type=\"gce_instance\""
                  aggregation = {
                    alignmentPeriod    = "60s"
                    perSeriesAligner   = "ALIGN_MEAN"
                    crossSeriesReducer = "REDUCE_MEAN"
                    groupByFields      = ["metric.label.region"]
                  }
                }
              }
            }]
            yAxis = { label = "milliseconds", scale = "LINEAR" }
          }
        },
        {
          title = "Input loss rate by region"
          xyChart = {
            dataSets = [{
              plotType   = "LINE"
              legendTemplate = "$${metric.label.region} / $${metric.label.transport}"
              timeSeriesQuery = {
                timeSeriesFilter = {
                  filter = "metric.type=\"workload.googleapis.com/synthclient.loss_rate\" AND resource.type=\"gce_instance\""
                  aggregation = {
                    alignmentPeriod    = "60s"
                    perSeriesAligner   = "ALIGN_MEAN"
                    crossSeriesReducer = "REDUCE_MEAN"
                    groupByFields      = ["metric.label.region", "metric.label.transport"]
                  }
                }
              }
            }]
            yAxis = { label = "fraction", scale = "LINEAR" }
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
