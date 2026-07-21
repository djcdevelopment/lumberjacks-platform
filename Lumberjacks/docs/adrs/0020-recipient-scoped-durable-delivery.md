# ADR 0020: Recipient-scoped durable delivery and the legacy bucket

**Status:** Accepted  
**Date:** 2026-07-18  
**Depends on:** M1 capability and enrollment contracts

## Context

The redirected ZDO queue is currently keyed only by `window_id`, while a
consumer can name its own identity in the injection query string. A producer
credential does not carry an enrollment: private-plane producers resolve with
`Enrollment = null`, and the frozen producer sends no recipient field. Treating
that absence as an enrollment identity is impossible and treating it as the
window id would preserve the existing shared queue under a new name.

## Decision

Introduce the explicit `ValheimRecipient.Legacy` bucket. Missing producer
identity, private-plane principals, and shared-client-key principals use that
bucket. An enrollment principal must have a non-blank server-side
`Enrollment.RecipientId`; otherwise the consumer operation fails closed. Poll,
inspect, ACK, activity, sequence tracking, and durable counters are keyed by
`(window_id, recipient_id)`. Caller-supplied labels are compatibility inputs at
most and never choose the resolved recipient.

The producer envelope/request recipient field is optional and additive. v1 WAL
records without it replay into `Legacy`; new WAL records carry an explicit schema
version and optional recipient. The producer-side mod change that emits a
per-peer recipient is stage 3 in the sibling `comfy` repository and is not part
of this commit.

Until that producer cut is enabled, enrollment consumers are deliberately
resolved to the legacy bucket as well; enabling recipient scoping before the
producer emits `recipient_id` would leave enrolled consumers polling an empty
partition and silently stop delivery.

## Consequences

Enrollment consumers are isolated and can be tested without HTTP or Postgres.
The frozen shared-key lane remains operational through the named legacy bucket.
Aggregate status and the existing `{acknowledged, unknown}` redirect ACK shape
remain compatibility projections; recipient-scoped status is the authoritative
test seam. M4a's producer outbox and M3 terminal lifecycle remain open.
