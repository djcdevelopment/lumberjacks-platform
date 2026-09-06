# The cost of telemetry on P7

Written 2026-09-06, after the **second** time an idle P7 quietly billed real money for
observing itself. Cumulative damage across both incidents is roughly $100. Neither
incident involved a busy server, a traffic spike, or a bug. Both were the default
instrumentation doing exactly what it was configured to do, on a machine with nobody
connected to it.

Read this before you add a metric, change an export interval, or enable an agent
receiver.

---

## The one idea

**Cloud Monitoring does not bill for information. It bills for samples.**

    chargeable MiB = series x samples-per-day x bytes-per-sample

Nothing in that formula is your traffic. A gauge that never changes costs the same as one
that swings wildly. A request histogram with zero requests costs the same as one serving
a thousand a second, because a CUMULATIVE histogram is re-sent **in full** on every export
whether or not anything happened.

That is why "the server is idle, it can't be costing anything" is wrong, and why it fooled
us twice. Idle is not free. Idle is full price.

Cloud Logging bills the opposite way, on bytes of actual content, which is why logs were
never the problem here even at 36 MiB/day. Do not reason about the two the same way.

---

## Prices

Cloud Monitoring, SKU "Metric Volume" (verified against the Cloud Billing Catalog API on
2026-09-06):

| Tier | Price |
|---|---|
| First 150 MiB / month / billing account | free |
| 150 MiB to 100,000 MiB | **$0.258 / MiB** |
| 100,000 to 250,000 MiB | $0.151 / MiB |
| Above 250,000 MiB | $0.061 / MiB |

The free tier is 150 **MiB**, not GiB. On this project it is consumed before lunch on day
one. Treat it as zero.

**Chargeable metric domains:** `workload.googleapis.com` (your OTel app metrics),
`agent.googleapis.com` (Ops Agent), `custom.googleapis.com`, and Prometheus. Google's own
infrastructure metrics (`compute.googleapis.com` and friends) are free. If a metric name
starts with `workload.` or `agent.`, you are paying for it.

Cloud Logging for contrast: 50 GiB per project per month free, then $0.50/GiB. Three
orders of magnitude more forgiving per byte.

---

## Bytes per sample, measured on this VM

Not from the docs. Derived by dividing observed billable bytes by observed sample counts.

| Value type | Bytes/sample | Example |
|---|---|---|
| DISTRIBUTION, 14 bucket bounds | **66 to 75** | `http.server.request.duration` |
| INT64 / DOUBLE scalar | **8 to 30** | `dotnet.gc.last_collection.heap.size` |

A histogram costs **8.8x** the cheapest gauge measured here and **2.3x** the chunkier
agent scalars. It is the single most expensive choice in the instrumentation and it is
invisible at the call site: in .NET, `Histogram<T>` and `Counter<T>` look equally innocent.

Samples per day, by export interval:

| Interval | Samples/day/series |
|---|---|
| 10s (the floor) | 8,640 |
| 30s | 2,880 |
| 60s | 1,440 |
| 300s | 288 |

**10 seconds is the fastest the API accepts.** It rejects points less than 10s apart on the
same series. So an exporter set to 10s is not "high resolution", it is pinned at the
maximum billable rate.

---

## Ready reckoner: what one series costs per month

At list price, 30-day month, ignoring the free tier.

| | 10s | 30s | 60s | 300s |
|---|---|---|---|---|
| **one scalar series** | $0.51 | $0.17 | $0.09 | $0.02 |
| **one histogram series** | $4.46 | $1.49 | $0.74 | $0.15 |

Read the top-left against the bottom-right. **One histogram at 10s costs as much as 52
scalars at 60s.** Then multiply by series count, which is where cardinality bites: a metric
labelled by process, route, or status code is not one series, it is one series per distinct
label combination.

Worked example, the incident: 65 workload series at 10s, mostly histograms, plus 365 agent
series at 30s. 69 MiB/day.

---

## The three traps, all of which we walked into

1. **Cumulative histograms on an idle service.** Re-sent whole every interval. Zero
   traffic, full price. 44 MiB/day of this with no players connected.

2. **Per-process agent metrics.** `agent.googleapis.com/processes/*` emits one series per
   running process **per metric**. 73 processes x 5 metrics = 365 series, billing every
   30s, for `agetty`, `chronyd` and `containerd`. 18 MiB/day to observe things nobody
   will ever look at. Disabled via an `exclude_metrics` processor in
   `ops-agent-config.yaml`.

3. **An export interval nobody chose deliberately.** `OTEL_METRIC_EXPORT_INTERVAL` was
   `10000` in `docker-compose.yml`. It is a linear multiplier on the entire app-side bill
   and it had no comment explaining why. It now does, and it is 60000.

---

## Measure it yourself, before and after

The authority is `monitoring.googleapis.com/billing/bytes_ingested`, broken down by
`metric_type`. This is how the incident was diagnosed and how each fix was verified.

```bash
TOKEN=$(gcloud auth print-access-token)
P=lumberjacks-exp-20260711-djc
curl -s -G "https://monitoring.googleapis.com/v3/projects/$P/timeSeries" \
  -H "Authorization: Bearer $TOKEN" \
  --data-urlencode 'filter=metric.type="monitoring.googleapis.com/billing/bytes_ingested"' \
  --data-urlencode "interval.startTime=$(date -u -d '7 days ago' +%Y-%m-%dT%H:%M:%SZ)" \
  --data-urlencode "interval.endTime=$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  --data-urlencode 'aggregation.alignmentPeriod=86400s' \
  --data-urlencode 'aggregation.perSeriesAligner=ALIGN_SUM'
```

Group by `metric.label."metric_domain"` for the agent-vs-app split, or read
`metric_type` per series for the full ranking. To confirm a change actually landed, query
the specific metric type and check that its newest point stops advancing.

**Known discrepancy, stated honestly:** measured volume multiplied by the list price
overshoots the invoice by roughly 3x. 69 MiB/day implies about $495/month at $0.258/MiB,
while observed spend was nearer $180/month all-in. Use this metric for **ranking and for
before/after**, where it is reliable, and do not quote its dollar figure as fact. The fix
for that gap is item A in `RUNBOOK-cost-and-cycle.md`: enable a BigQuery billing export.
There still is not one, which is the reason the original question took a full
investigation instead of one query.

---

## Preflight, before shipping any telemetry change

- [ ] Is the new metric a histogram? If yes, it costs ~9x a gauge. Do you need the buckets?
- [ ] How many series will it actually create? Count the distinct label combinations, not
      the metric names. Any label carrying a PID, a request id, or a user id is a bug.
- [ ] What interval? 60s unless you can say why not. 10s is the billable ceiling.
- [ ] Does it land in `workload.` or `agent.`? Then it is chargeable.
- [ ] Estimate with the ready reckoner above, then **verify against
      `billing/bytes_ingested` a day later.** Estimating is not verifying.
- [ ] If you disable a metric, check `monitoring.tf` first. Alert policies read
      `memory/percent_used`, `swap/percent_used`, `disk/percent_used` and `agent/uptime`.
      Silently disarming an alert is a worse outcome than the cost you saved.

## Why this happened twice

`RUNBOOK-cost-and-cycle.md` grounds itself on
`docs/audit/2026-07-25-gcp-burn-rate-review.md` — the burn memo from the **first**
incident. That file does not exist. It is not in the working tree and
`git log --all -- <path>` returns nothing, so it was never committed.

The analysis of incident one lived in a chat session and a set of dollar estimates that
survived into a decision table, while the actual mechanism — samples times series times
bytes, idle costs the same as busy — did not survive anywhere a person would trip over it.
Six weeks later the same class of spend recurred, and it was diagnosed from scratch.

That is the real root cause of the recurrence, and it is why this file exists as a
reference rather than as another dated incident memo. Incident memos age out. A price list
with a preflight checklist does not.

If the 2026-07-25 memo exists somewhere outside this repo, commit it next to this file. If
it does not, that dangling link should be removed from the runbook's grounding section so
nobody else goes looking for a document that was never written.

## And the thing that would have caught both incidents in days

There is a `google_billing_budget` resource in `main.tf`, but it is `count = 0` unless
`billing_account_id` is set, and it is not set. A budget with a 50% threshold would have
sent an email in week one of each incident instead of the spend being noticed by accident
on a statement. That is the highest-value item on this page and it is not code, it is one
variable.
