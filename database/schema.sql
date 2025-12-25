-- HART-MCP Database Schema
-- Single table stores EVERYTHING (text, embeddings, AI models, all content)

-- Create database
CREATE DATABASE "HART-MCP" WITH ENCODING 'UTF8';

\c "HART-MCP"

-- Enable PostGIS
CREATE EXTENSION IF NOT EXISTS postgis;

-- ONE TABLE TO RULE THEM ALL
CREATE TABLE atom (
    id BIGSERIAL PRIMARY KEY,
    
    -- Hilbert index (4D space-filling curve for locality-preserving indexing)
    hilbert_high BIGINT NOT NULL,
    hilbert_low BIGINT NOT NULL,
    
    -- Geometry (SRID 0 = abstract 4D hypersphere, not geographic)
    -- Constants: POINTZM (single point on hypersphere surface)
    -- Compositions: LINESTRING/POLYGON/GEOMETRYCOLLECTION
    geom GEOMETRY(GEOMETRYZM, 0) NOT NULL,  
    
    -- Type flag
    is_constant BOOLEAN NOT NULL,
    
    -- For constants: the original seed value for lossless reconstruction
    seed_value BIGINT,           -- Unicode codepoint, integer, or float bits
    seed_type INT,               -- 0=Unicode, 1=Integer, 2=Float bits
    
    -- Composition structure (NULL for constants)
    refs BIGINT[],               -- Child atom IDs
    multiplicities INT[],        -- Parallel array: RLE counts or edge weights
    
    -- Content hash (BLAKE3-256, 32 bytes)
    -- Automatic deduplication: same content = same hash = same atom
    content_hash BYTEA NOT NULL UNIQUE,
    
    -- Type discriminator for app-layer entity mapping
    -- e.g., 'char', 'word', 'sentence', 'embedding', 'ai_model', 'conversation', 'turn'
    atom_type VARCHAR(64),
    
    -- JSONB metadata for app-layer data (flexible schema per AtomType)
    -- e.g., for ai_model: {"name": "GPT-4", "architecture": "transformer", "parameterCount": 1760000000}
    -- e.g., for conversation: {"sessionType": "chat", "userId": "user123"}
    metadata JSONB,
    
    -- Constraints
    CONSTRAINT atom_constant_check CHECK (
        (is_constant = TRUE AND refs IS NULL AND multiplicities IS NULL) OR
        (is_constant = FALSE AND refs IS NOT NULL AND multiplicities IS NOT NULL)
    ),
    CONSTRAINT atom_refs_mult_length CHECK (
        refs IS NULL OR array_length(refs, 1) = array_length(multiplicities, 1)
    ),
    CONSTRAINT atom_seed_check CHECK (
        (is_constant = TRUE) OR (seed_value IS NULL AND seed_type IS NULL)
    )
);

-- Indexes
CREATE INDEX idx_atom_geom ON atom USING GIST (geom);  -- Spatial queries
CREATE INDEX idx_atom_hilbert ON atom (hilbert_high, hilbert_low);  -- Range queries
CREATE INDEX idx_atom_hash ON atom USING HASH (content_hash);  -- O(1) dedup lookup
CREATE INDEX idx_atom_refs ON atom USING GIN (refs);  -- Fast traversal
CREATE INDEX idx_atom_is_constant ON atom (is_constant);
CREATE INDEX idx_atom_type ON atom (atom_type) WHERE atom_type IS NOT NULL;
CREATE INDEX idx_atom_seed_value ON atom (seed_value) WHERE seed_value IS NOT NULL;
CREATE INDEX idx_atom_metadata ON atom USING GIN (metadata);  -- JSONB queries

-- Optional: CPE vocabulary management
CREATE TABLE cpe_vocabulary (
    id BIGSERIAL PRIMARY KEY,
    name TEXT NOT NULL,
    version TEXT,
    vocab_size INT,
    min_frequency INT,
    merge_order BIGINT[][],  -- Array of [atom_a_id, atom_b_id] pairs
    created_at TIMESTAMPTZ DEFAULT NOW()
);

-- Optional: Metadata tags (for human organization, not required)
CREATE TABLE atom_tag (
    atom_id BIGINT REFERENCES atom(id) ON DELETE CASCADE,
    tag TEXT NOT NULL,
    value TEXT,
    PRIMARY KEY (atom_id, tag)
);

CREATE INDEX idx_atom_tag_tag ON atom_tag (tag);

-- Views for convenience

-- Constants only
CREATE VIEW constant_atoms AS
SELECT id, hilbert_high, hilbert_low, geom, content_hash, created_at
FROM atom
WHERE is_constant = TRUE;

-- Compositions only
CREATE VIEW composition_atoms AS
SELECT id, hilbert_high, hilbert_low, geom, refs, multiplicities, content_hash, created_at
FROM atom
WHERE is_constant = FALSE;

-- Statistics view
CREATE VIEW atom_stats AS
SELECT
    COUNT(*) AS total_atoms,
    COUNT(*) FILTER (WHERE is_constant) AS constant_count,
    COUNT(*) FILTER (WHERE NOT is_constant) AS composition_count,
    COUNT(DISTINCT content_hash) AS unique_hashes,
    pg_size_pretty(pg_total_relation_size('atom')) AS table_size
FROM atom;

-- Grant permissions (adjust as needed)
GRANT ALL PRIVILEGES ON DATABASE "HART-MCP" TO postgres;
GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO postgres;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO postgres;
