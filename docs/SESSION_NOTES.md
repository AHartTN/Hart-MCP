# Session Notes - Raw Context

## Critical Corrections

- **Constants are characters and numbers.** The truly indivisible primitives.
- **Multi-character tokens are compositions.** "cat" = [c, a, t] = composition of three character constants.
- **Single-character tokens collapse to the constant.** Token 'a' = character 'a' constant. No separate composition. Can't have a 1-point LineString.

## Key Statements from User

### On Atoms
- "Atoms are two types... constants and compositions... pointzm vs linestring/polygon/geometry collection"
- "Constants are meaningless on their own... they only gain meaning when linked with something else... C has no meaning but Cat does"

### On Embeddings
- "what really is an embedding if not a big ass ball of linestrings waiting to be untangled?"
- "its N dimension based on the model being ingested"
- "An embedding is a series of coordinates in and of itself, is it not? they map to tokens... we dont really give a fuck about the values because its linking A to B"
- "30kx300... Nah, its 30,000 300-point linestrings"

### On Weights
- "we have no need to store non-relations and can even trim 0.6 and lower and not lose functionality from virtually any model"
- "We dont even need that weight atom C... the linking of atom A to B and how many other links it has is key"
- Initial idea was [A,B,C] as a 3-point LineString
- **RESOLVED:** Weight values are NOT atoms. Weights = edge multiplicity.
- Connection strength = SUM(multiplicities) across edges linking two atoms
- Semantics emerge from topology (which atoms connect), not random float values

### On Landmark Projection
- "we just have to put capital and lowercase together, latin characters that are the same but slightly different, etc"
- "theres a method to the madness... fibonacci, golden ratio... but segmented by letter, number, punctuation, system characters, page, code value, etc"
- NOT semantically meaningful - deterministic mathematical placement

### On the Big Picture
- "Postgres, C++, C#... database is the AI"
- "number=>character=>n-gram=>words=>phrases=>sentences=>paragraphs=>etc"
- "Think of how nesting with MAF/Tiger works... street, city, zip/county/school district/etc"
- "Think of how Hello is really H-E-L-O with L run length encoded"
- "code, audio, video, ai models, text, etc... its all the same... numbers with a merkle dag"
- "hilbert high, hilbert low, geom with the pointzm made from the landmark projection (for constants)"
- "we have to be able to reconstruct whatever we ingest"
- "this system only needs the embeddings/tokens from AI and all the weights can be tossed but aren't because they CAN add value"
- "those linestrings are the trajectories"
- "everything gets referential integrity and that referential integrity are the edges/weights/etc... just now we get to use spatial data types"

### What NOT to Use
- "dont you fucking dare think pg_vector or graphs"
- PostGIS native geometry IS the vector store
- No external graph database - PostGIS relationships ARE the graph

## Core Insight

The Merkle DAG of atoms on a 4D hypersphere, indexed via Hilbert curves, with PostGIS spatial operations = a unified substrate where:
- Storage IS the model
- Query IS inference
- Deduplication IS free
- All content types reduce to the same structure

## Tech Stack Confirmed

- PostgreSQL + PostGIS (storage, spatial ops)
- C++ (native lib for heavy computation)
- C# (orchestration)
