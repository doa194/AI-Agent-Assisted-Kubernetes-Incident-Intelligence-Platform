-- Semantic incident memory.
--
-- What goes in here, and just as importantly what does not.
--
-- STORED: short, high-value text that describes a PROBLEM in language
-- someone would recognise later - an incident summary, a root cause, a
-- resolution, a normalised error signature, a runbook section.
--
-- NOT STORED: raw log lines, metric samples, individual Kubernetes events.
-- Those stay in Loki and Prometheus. Embedding them would be expensive, would
-- grow without bound, and would make retrieval worse rather than better:
-- thousands of near-identical log lines would crowd out the one incident
-- summary that actually answers the question.
--
-- The relational source record is always linked, so a retrieval result can be
-- traced back to the incident or runbook it came from rather than floating
-- free as a piece of text a model half-remembers.

CREATE TABLE semantic_memory (
    id                uuid        PRIMARY KEY,

    -- 'incident' or 'runbook'. Retrieval can be filtered by kind, because the
    -- two answer different questions: "has this happened before?" versus
    -- "what does our documentation say about this?".
    kind              text        NOT NULL,

    -- Link back to the incident this memory summarises. Null for runbooks.
    -- ON DELETE CASCADE keeps memory from outliving the incident it describes.
    incident_id       uuid        REFERENCES incidents(id) ON DELETE CASCADE,

    -- Stable identity of the source, used to update in place rather than
    -- accumulating duplicates: an incident id for incidents, a file path plus
    -- section for runbooks.
    source_ref        text        NOT NULL,

    title             text        NOT NULL,

    -- The text that was embedded. Kept so a retrieval result can be shown to
    -- an agent and to a human without a second lookup, and so the corpus can
    -- be re-embedded if the embedding model ever changes.
    content           text        NOT NULL,

    -- Hash of the content. Re-indexing skips anything unchanged, which makes
    -- start-up cheap: embedding is the slow part, and runbooks rarely change.
    content_hash      text        NOT NULL,

    -- Filterable facets. Restricting a search to the same workload or the same
    -- incident category before comparing vectors makes results far more
    -- relevant than similarity alone, and is much cheaper than a wider search.
    workload          text,
    category          text,
    root_cause_category text,
    severity          text,

    -- 768 dimensions, matching EmbeddingGemma. This is fixed at schema level
    -- on purpose: changing the embedding model requires a migration and a
    -- re-index, not just a configuration change, because vectors from two
    -- different models are not comparable.
    embedding         vector(768) NOT NULL,

    occurred_at_utc   timestamptz,
    created_at_utc    timestamptz NOT NULL DEFAULT (now() AT TIME ZONE 'utc'),
    updated_at_utc    timestamptz NOT NULL DEFAULT (now() AT TIME ZONE 'utc')
);

-- One memory per source. Re-indexing the same incident or runbook section
-- updates the existing row instead of adding a near-duplicate that would then
-- compete with itself in every search.
CREATE UNIQUE INDEX idx_semantic_memory_source ON semantic_memory (kind, source_ref);

CREATE INDEX idx_semantic_memory_kind ON semantic_memory (kind);
CREATE INDEX idx_semantic_memory_workload ON semantic_memory (workload) WHERE workload IS NOT NULL;

-- HNSW index for approximate nearest-neighbour search, using cosine distance
-- to match the operator the queries use.
--
-- HNSW rather than IVFFlat because IVFFlat needs training data to build its
-- lists and behaves poorly on a small, growing corpus - which is exactly what
-- this is on a fresh install. HNSW works well from the very first row.
CREATE INDEX idx_semantic_memory_embedding
    ON semantic_memory
    USING hnsw (embedding vector_cosine_ops)
    WITH (m = 16, ef_construction = 64);
