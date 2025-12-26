-- HART-MCP Database Schema
-- Three tables: constant (leaf nodes), composition (internal nodes), relation (edges)

-- Create database
CREATE DATABASE "HART-MCP" WITH ENCODING 'UTF8';

\c "HART-MCP"

-- Enable PostGIS
CREATE EXTENSION IF NOT EXISTS postgis;

-- =============================================================================
-- CONSTANT TABLE - Irreducible atomic values (Unicode codepoints, bytes, floats)
-- =============================================================================
CREATE TABLE constant (
    id BIGSERIAL PRIMARY KEY,

    -- Seed value for lossless reconstruction
    seed_value BIGINT NOT NULL,      -- Unicode codepoint, integer, or float bits
    seed_type INT NOT NULL,          -- 0=Unicode, 1=byte, 2=float32 bits, 3=integer

    -- Content hash (BLAKE3-256, 32 bytes) for deduplication
    content_hash BYTEA NOT NULL UNIQUE,

    -- Hilbert index (128-bit space-filling curve for locality)
    hilbert_high BIGINT NOT NULL,
    hilbert_low BIGINT NOT NULL,

    -- 4D hypersphere geometry (POINTZM)
    -- Coordinates derived deterministically from seed_value
    geom GEOMETRY(POINTZM, 0) NOT NULL
);

-- Indexes for constant
CREATE INDEX idx_constant_hash ON constant USING HASH (content_hash);
CREATE INDEX idx_constant_seed ON constant (seed_type, seed_value);
CREATE INDEX idx_constant_hilbert ON constant (hilbert_high, hilbert_low);
CREATE INDEX idx_constant_geom ON constant USING GIST (geom);

-- =============================================================================
-- COMPOSITION TABLE - Composite nodes referencing other nodes
-- =============================================================================
CREATE TABLE composition (
    id BIGSERIAL PRIMARY KEY,

    -- Content hash (BLAKE3-256) for deduplication
    -- Computed from ordered child references
    content_hash BYTEA NOT NULL UNIQUE,

    -- Hilbert index
    hilbert_high BIGINT NOT NULL,
    hilbert_low BIGINT NOT NULL,

    -- 4D geometry (centroid of children, or trajectory)
    geom GEOMETRY(GEOMETRYZM, 0),

    -- Optional type reference (for typed compositions)
    type_id BIGINT REFERENCES composition(id) ON DELETE SET NULL
);

-- Indexes for composition
CREATE INDEX idx_composition_hash ON composition USING HASH (content_hash);
CREATE INDEX idx_composition_hilbert ON composition (hilbert_high, hilbert_low);
CREATE INDEX idx_composition_geom ON composition USING GIST (geom);
CREATE INDEX idx_composition_type ON composition (type_id) WHERE type_id IS NOT NULL;

-- =============================================================================
-- RELATION TABLE - Edges linking compositions to their children
-- =============================================================================
CREATE TABLE relation (
    id BIGSERIAL PRIMARY KEY,

    -- Parent composition
    composition_id BIGINT NOT NULL REFERENCES composition(id) ON DELETE CASCADE,

    -- Child node (exactly one must be set)
    child_constant_id BIGINT REFERENCES constant(id) ON DELETE CASCADE,
    child_composition_id BIGINT REFERENCES composition(id) ON DELETE CASCADE,

    -- Order within parent (0-indexed)
    position INT NOT NULL,

    -- RLE multiplicity (default 1)
    multiplicity INT NOT NULL DEFAULT 1,

    -- Constraints
    CONSTRAINT relation_exactly_one_child CHECK (
        (child_constant_id IS NOT NULL AND child_composition_id IS NULL) OR
        (child_constant_id IS NULL AND child_composition_id IS NOT NULL)
    ),
    CONSTRAINT relation_positive_multiplicity CHECK (multiplicity > 0)
);

-- Indexes for relation
CREATE INDEX idx_relation_composition ON relation (composition_id);
CREATE INDEX idx_relation_child_constant ON relation (child_constant_id) WHERE child_constant_id IS NOT NULL;
CREATE INDEX idx_relation_child_composition ON relation (child_composition_id) WHERE child_composition_id IS NOT NULL;
CREATE UNIQUE INDEX idx_relation_position ON relation (composition_id, position);

-- =============================================================================
-- VIEWS
-- =============================================================================

-- Statistics view
CREATE VIEW schema_stats AS
SELECT
    (SELECT COUNT(*) FROM constant) AS constant_count,
    (SELECT COUNT(*) FROM composition) AS composition_count,
    (SELECT COUNT(*) FROM relation) AS relation_count,
    (SELECT pg_size_pretty(pg_total_relation_size('constant'))) AS constant_size,
    (SELECT pg_size_pretty(pg_total_relation_size('composition'))) AS composition_size,
    (SELECT pg_size_pretty(pg_total_relation_size('relation'))) AS relation_size;

-- =============================================================================
-- PERMISSIONS
-- =============================================================================
GRANT ALL PRIVILEGES ON DATABASE "HART-MCP" TO hartonomous;
GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO hartonomous;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO hartonomous;
