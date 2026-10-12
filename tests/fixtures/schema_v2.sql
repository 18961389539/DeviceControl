CREATE TABLE IF NOT EXISTS schema_version(version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS workflow_revisions(
              id TEXT PRIMARY KEY, status TEXT NOT NULL CHECK(status IN ('Draft','Validated','Published','Superseded')),
              source_id TEXT, author TEXT NOT NULL, created_utc TEXT NOT NULL,
              sha256 TEXT NOT NULL, definition_json TEXT NOT NULL, reason TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS active_workflow(singleton INTEGER PRIMARY KEY CHECK(singleton=1), version_id TEXT NOT NULL REFERENCES workflow_revisions(id));
            CREATE TABLE IF NOT EXISTS production_runs(
              id TEXT PRIMARY KEY, started_utc TEXT NOT NULL, ended_utc TEXT NOT NULL,
              outcome TEXT NOT NULL, duration_ms INTEGER NOT NULL, flow_version TEXT,
              recipe_id TEXT, reason TEXT, payload_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_runs_started ON production_runs(started_utc DESC);
            CREATE TABLE IF NOT EXISTS run_node_traces(
              run_id TEXT NOT NULL REFERENCES production_runs(id) ON DELETE CASCADE,
              ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
              node_id TEXT NOT NULL, parent_id TEXT, label TEXT NOT NULL,
              node_type TEXT NOT NULL, outcome TEXT NOT NULL, duration_ms INTEGER NOT NULL CHECK(duration_ms >= 0),
              attempt INTEGER NOT NULL, ended_utc TEXT NOT NULL, error TEXT,
              PRIMARY KEY(run_id,ordinal)
            );
            CREATE INDEX IF NOT EXISTS idx_run_trace_node ON run_node_traces(node_id,outcome);
            CREATE TABLE IF NOT EXISTS audit_events(
              id TEXT PRIMARY KEY, time_utc TEXT NOT NULL, actor TEXT NOT NULL,
              role TEXT NOT NULL, method TEXT NOT NULL, path TEXT NOT NULL,
              http_status INTEGER NOT NULL, trace_id TEXT NOT NULL, payload_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_audit_time ON audit_events(time_utc DESC);
            CREATE TABLE IF NOT EXISTS recipe_revisions(
              id TEXT PRIMARY KEY, state TEXT NOT NULL, payload_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS active_recipe(singleton INTEGER PRIMARY KEY CHECK(singleton=1), id TEXT NOT NULL REFERENCES recipe_revisions(id));
            INSERT OR IGNORE INTO schema_version(version,applied_utc) VALUES(1,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            INSERT OR IGNORE INTO schema_version(version,applied_utc) VALUES(2,strftime('%Y-%m-%dT%H:%M:%fZ','now'));