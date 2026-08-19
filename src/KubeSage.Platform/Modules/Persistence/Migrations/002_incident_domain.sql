-- The incident domain and the durable work queue.
--
-- Design note on why there is no message broker here.
--
-- The project needs work to survive a restart, to be retried after a failure,
-- to be processed at most once, and to be limited to a small number of
-- concurrent investigations. PostgreSQL gives all four with SELECT ... FOR
-- UPDATE SKIP LOCKED and a lease column, in one datastore that is already
-- required for storing incidents. Adding RabbitMQ or Kafka would introduce a
-- second thing to run, monitor and reason about, in exchange for throughput
-- this platform will never need - a local 12B model can serve roughly one
-- investigation at a time.

-- ---------------------------------------------------------------------------
-- Incidents
-- ---------------------------------------------------------------------------
CREATE TABLE incidents (
    id                    uuid        PRIMARY KEY,

    -- Identity of the recurring CONDITION, not of this row. Several
    -- detections of the same ongoing problem share a fingerprint and update
    -- one incident instead of creating many.
    fingerprint           text        NOT NULL,

    state                 text        NOT NULL,
    severity              text        NOT NULL,
    category              text        NOT NULL,
    title                 text        NOT NULL,
    detection_rule        text        NOT NULL,
    namespace             text        NOT NULL,

    -- Workloads showing the symptom. Not a claim about which is at fault.
    affected_workloads    text[]      NOT NULL DEFAULT '{}',

    -- The measured values that made the rule fire, so the arithmetic can be
    -- checked later without rerunning anything.
    signals               jsonb       NOT NULL DEFAULT '{}'::jsonb,

    first_detected_at_utc timestamptz NOT NULL,
    last_detected_at_utc  timestamptz NOT NULL,
    recovered_at_utc      timestamptz,
    occurrence_count      integer     NOT NULL DEFAULT 1,
    outcome               text,
    updated_at_utc        timestamptz NOT NULL
);

-- Deduplication depends on finding the most recent open incident for a
-- fingerprint quickly, and this runs on every detection pass.
CREATE INDEX idx_incidents_fingerprint_recent
    ON incidents (fingerprint, last_detected_at_utc DESC);

CREATE INDEX idx_incidents_state ON incidents (state)
    WHERE state NOT IN ('Reported', 'Ignored', 'Inconclusive', 'Recovered');

CREATE INDEX idx_incidents_detected ON incidents (first_detected_at_utc DESC);

-- ---------------------------------------------------------------------------
-- Evidence attached to an incident
-- ---------------------------------------------------------------------------
-- Evidence is COPIED here rather than only referenced, because Loki and
-- Prometheus age their data out. A report read next week must still be able
-- to show what it was based on. This is the one place raw telemetry is
-- persisted, and only the small slice that supported a conclusion.
CREATE TABLE incident_evidence (
    id                text        PRIMARY KEY,
    incident_id       uuid        NOT NULL REFERENCES incidents(id) ON DELETE CASCADE,
    kind              text        NOT NULL,
    source            text        NOT NULL,
    observed_at_utc   timestamptz NOT NULL,
    workload          text,
    namespace         text,
    summary           text        NOT NULL,
    attributes        jsonb       NOT NULL DEFAULT '{}'::jsonb,

    -- The exact query that produced this item, so a human can reproduce it.
    query             text,
    redacted_count    integer     NOT NULL DEFAULT 0,
    created_at_utc    timestamptz NOT NULL DEFAULT (now() AT TIME ZONE 'utc')
);

CREATE INDEX idx_incident_evidence_incident ON incident_evidence (incident_id);

-- ---------------------------------------------------------------------------
-- Investigations
-- ---------------------------------------------------------------------------
CREATE TABLE investigations (
    id                 uuid        PRIMARY KEY,
    incident_id        uuid        NOT NULL REFERENCES incidents(id) ON DELETE CASCADE,
    state              text        NOT NULL,
    attempt            integer     NOT NULL DEFAULT 1,
    started_at_utc     timestamptz NOT NULL,
    completed_at_utc   timestamptz,
    duration_ms        integer,

    -- Set when the run ended without a usable result. Kept separate from the
    -- report so a failure is never mistaken for a conclusion.
    failure_reason     text,

    -- Whether every telemetry source was reachable. A conclusion drawn from
    -- partial data has to be presented as such.
    evidence_complete  boolean     NOT NULL DEFAULT true,
    unavailable_sources text[]     NOT NULL DEFAULT '{}'
);

CREATE INDEX idx_investigations_incident ON investigations (incident_id, started_at_utc DESC);

-- ---------------------------------------------------------------------------
-- Agent executions
-- ---------------------------------------------------------------------------
-- One row per agent per investigation.
--
-- Note what is NOT stored: the model's private reasoning. Only the structured
-- result, the tools it called and how long it took are kept. Persisting chain
-- of thought would grow without bound and would put unverified model text
-- into the incident record, where it could later be mistaken for evidence.
CREATE TABLE agent_executions (
    id                uuid        PRIMARY KEY,
    investigation_id  uuid        NOT NULL REFERENCES investigations(id) ON DELETE CASCADE,
    agent_name        text        NOT NULL,
    started_at_utc    timestamptz NOT NULL,
    completed_at_utc  timestamptz,
    duration_ms       integer,
    tool_call_count   integer     NOT NULL DEFAULT 0,

    -- Names of the tools called, for auditing which evidence the agent
    -- actually asked for.
    tools_used        text[]      NOT NULL DEFAULT '{}',
    succeeded         boolean     NOT NULL DEFAULT false,
    failure_reason    text,

    -- The validated structured output, never free-form prose.
    result            jsonb
);

CREATE INDEX idx_agent_executions_investigation ON agent_executions (investigation_id);

-- ---------------------------------------------------------------------------
-- Hypotheses
-- ---------------------------------------------------------------------------
CREATE TABLE hypotheses (
    id                 uuid        PRIMARY KEY,
    investigation_id   uuid        NOT NULL REFERENCES investigations(id) ON DELETE CASCADE,
    rank               integer     NOT NULL,
    statement          text        NOT NULL,
    root_cause_category text,
    suspected_workload text,
    confidence         double precision NOT NULL,

    -- Identifiers of the evidence supporting this hypothesis. Validated
    -- against incident_evidence before the row is written, so a hypothesis
    -- can never cite something that was not collected.
    evidence_ids       text[]      NOT NULL DEFAULT '{}',
    created_at_utc     timestamptz NOT NULL DEFAULT (now() AT TIME ZONE 'utc')
);

CREATE INDEX idx_hypotheses_investigation ON hypotheses (investigation_id, rank);

-- ---------------------------------------------------------------------------
-- Reports
-- ---------------------------------------------------------------------------
CREATE TABLE reports (
    id                  uuid        PRIMARY KEY,
    incident_id         uuid        NOT NULL REFERENCES incidents(id) ON DELETE CASCADE,
    investigation_id    uuid        NOT NULL REFERENCES investigations(id) ON DELETE CASCADE,
    kind                text        NOT NULL,
    title               text        NOT NULL,
    summary             text        NOT NULL,
    severity            text        NOT NULL,
    affected_workloads  text[]      NOT NULL DEFAULT '{}',
    impact              text,
    timeline            jsonb       NOT NULL DEFAULT '[]'::jsonb,
    likely_root_cause   text,
    root_cause_category text,
    confidence          double precision,
    alternatives        jsonb       NOT NULL DEFAULT '[]'::jsonb,
    recommended_actions jsonb       NOT NULL DEFAULT '[]'::jsonb,
    verification_steps  jsonb       NOT NULL DEFAULT '[]'::jsonb,
    evidence_ids        text[]      NOT NULL DEFAULT '{}',
    created_at_utc      timestamptz NOT NULL DEFAULT (now() AT TIME ZONE 'utc')
);

CREATE INDEX idx_reports_created ON reports (created_at_utc DESC);
CREATE INDEX idx_reports_incident ON reports (incident_id);

-- ---------------------------------------------------------------------------
-- Durable work queue
-- ---------------------------------------------------------------------------
-- This table is the reason the platform survives a restart. A crash while an
-- investigation is running leaves a claimed row whose lease expires, and the
-- next poll picks it up again.
CREATE TABLE work_items (
    id                uuid        PRIMARY KEY,
    kind              text        NOT NULL,

    -- Natural key for the work. A unique index over (kind, dedup_key) for
    -- unfinished rows is what makes enqueueing idempotent: the same event
    -- raised twice cannot produce two investigations.
    dedup_key         text        NOT NULL,

    payload           jsonb       NOT NULL DEFAULT '{}'::jsonb,
    state             text        NOT NULL DEFAULT 'Pending',
    attempt           integer     NOT NULL DEFAULT 0,
    max_attempts      integer     NOT NULL DEFAULT 3,

    -- Set when a worker claims the row. A row whose lease has passed is
    -- treated as abandoned and becomes claimable again.
    leased_until_utc  timestamptz,
    leased_by         text,

    -- Retry backoff: the row is invisible to claims until this time.
    available_at_utc  timestamptz NOT NULL DEFAULT (now() AT TIME ZONE 'utc'),

    last_error        text,
    created_at_utc    timestamptz NOT NULL DEFAULT (now() AT TIME ZONE 'utc'),
    updated_at_utc    timestamptz NOT NULL DEFAULT (now() AT TIME ZONE 'utc'),
    completed_at_utc  timestamptz
);

-- Idempotent enqueue. The partial index applies only to work that has not
-- finished, so the same dedup_key may legitimately be used again later - for
-- example when the same incident recurs after being reported.
CREATE UNIQUE INDEX idx_work_items_active_dedup
    ON work_items (kind, dedup_key)
    WHERE state IN ('Pending', 'Claimed');

-- The claim query orders by availability and only looks at claimable rows.
CREATE INDEX idx_work_items_claimable
    ON work_items (available_at_utc)
    WHERE state = 'Pending';

CREATE INDEX idx_work_items_lease
    ON work_items (leased_until_utc)
    WHERE state = 'Claimed';

-- ---------------------------------------------------------------------------
-- Detection bookkeeping
-- ---------------------------------------------------------------------------
-- Restart counts observed on the previous pass. Detection rules need the
-- INCREASE in restarts, not the absolute count, or a pod that crash-looped
-- yesterday would raise a new incident on every evaluation today.
CREATE TABLE detection_state (
    key             text        PRIMARY KEY,
    value           jsonb       NOT NULL,
    updated_at_utc  timestamptz NOT NULL DEFAULT (now() AT TIME ZONE 'utc')
);
