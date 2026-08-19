-- Allow reports that describe the CLUSTER rather than a single incident.
--
-- The startup report and the periodic health report answer "how is the whole
-- system doing", so neither belongs to an incident or an investigation. Both
-- columns were originally NOT NULL because the only report that existed was an
-- incident report.
--
-- The "kind" column already distinguishes them, so making the two references
-- optional is enough. An incident report still carries both, and the check
-- constraint below enforces that: a report of kind 'incident' must be linked
-- to one, so relaxing this cannot quietly produce an orphaned incident report.

ALTER TABLE reports ALTER COLUMN incident_id DROP NOT NULL;
ALTER TABLE reports ALTER COLUMN investigation_id DROP NOT NULL;

ALTER TABLE reports
    ADD CONSTRAINT reports_incident_link_required
    CHECK (
        kind <> 'incident'
        OR (incident_id IS NOT NULL AND investigation_id IS NOT NULL)
    );

-- The API serves "the latest report" across all kinds, and the startup and
-- scheduled reports are read by kind.
CREATE INDEX idx_reports_kind_created ON reports (kind, created_at_utc DESC);
