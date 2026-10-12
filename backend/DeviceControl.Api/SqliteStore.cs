using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace DeviceControl.Api;

/// <summary>Local simulation persistence. SQLite is NOT a tamper-proof audit ledger.</summary>
public sealed class SqliteStore
{
    private readonly object _lock = new();
    private readonly string _connectionString;
    public string DatabasePath { get; }
    public SqliteStore(IHostEnvironment env, string? scope = null)
    {
        var folder = StationStoragePaths.Folder(env, scope);
        DatabasePath = Path.Combine(folder, "devicecontrol.db");
        _connectionString = new SqliteConnectionStringBuilder
        { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true }.ToString();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
        cmd.CommandText = """
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
            CREATE TABLE IF NOT EXISTS workflow_quality_reports(
              report_id TEXT PRIMARY KEY,
              flow_id TEXT NOT NULL REFERENCES workflow_revisions(id),
              definition_sha256 TEXT NOT NULL, report_sha256 TEXT NOT NULL,
              passed INTEGER NOT NULL CHECK(passed IN (0,1)),
              evaluated_utc TEXT NOT NULL, payload_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_quality_flow ON workflow_quality_reports(flow_id,evaluated_utc DESC);
            CREATE TRIGGER IF NOT EXISTS quality_reports_no_update BEFORE UPDATE ON workflow_quality_reports
              BEGIN SELECT RAISE(ABORT, 'quality reports are immutable'); END;
            CREATE TRIGGER IF NOT EXISTS quality_reports_no_delete BEFORE DELETE ON workflow_quality_reports
              BEGIN SELECT RAISE(ABORT, 'quality reports are immutable'); END;
            CREATE TABLE IF NOT EXISTS run_inflight(
              run_id TEXT PRIMARY KEY, started_utc TEXT NOT NULL,
              flow_version TEXT, recipe_id TEXT
            );
            CREATE TABLE IF NOT EXISTS run_variable_snapshots(
              run_id TEXT NOT NULL, ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
              node_id TEXT NOT NULL, captured_utc TEXT NOT NULL,
              payload_json TEXT NOT NULL, sha256 TEXT NOT NULL,
              PRIMARY KEY(run_id,ordinal),
              FOREIGN KEY(run_id,ordinal) REFERENCES run_node_traces(run_id,ordinal) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_variable_snapshot_node ON run_variable_snapshots(node_id);
            CREATE TRIGGER IF NOT EXISTS variable_snapshots_no_update BEFORE UPDATE ON run_variable_snapshots
              BEGIN SELECT RAISE(ABORT, 'variable snapshots are immutable'); END;
            CREATE TRIGGER IF NOT EXISTS variable_snapshots_no_delete BEFORE DELETE ON run_variable_snapshots
              BEGIN SELECT RAISE(ABORT, 'variable snapshots are immutable'); END;
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
            INSERT OR IGNORE INTO schema_version(version,applied_utc) VALUES(3,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            INSERT OR IGNORE INTO schema_version(version,applied_utc) VALUES(4,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
        return c;
    }
    /// <summary>Consistent SQLite online backup (including committed WAL pages).
    /// This is recovery evidence; deploying a version never restores the DB blindly.</summary>
    public string CreateVerifiedBackup(string destination)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination)) throw new InvalidOperationException("备份路径已存在，拒绝覆盖");
            using (var source = Open())
            using (var target = new SqliteConnection(new SqliteConnectionStringBuilder
                       { DataSource = destination, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
            {
                target.Open();
                source.BackupDatabase(target);
                using var command = target.CreateCommand();
                command.CommandText = "PRAGMA quick_check";
                if (!string.Equals(command.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("部署前备份完整性检查失败");
            }
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination)));
        }
    }

    public void BeginCycle(CycleStartRecord row)
    {
        lock (_lock)
        {
            using var c=Open();using var q=c.CreateCommand();
            q.CommandText="""
                INSERT INTO run_inflight(run_id,started_utc,flow_version,recipe_id)
                VALUES($id,$started,$flow,$recipe)
                """;
            q.Parameters.AddWithValue("$id",row.Id);
            q.Parameters.AddWithValue("$started",row.StartedAt);
            q.Parameters.AddWithValue("$flow",(object?)row.FlowVersion??DBNull.Value);
            q.Parameters.AddWithValue("$recipe",(object?)row.RecipeId??DBNull.Value);
            q.ExecuteNonQuery();
        }
    }
    // On restart: convert orphaned in-flight markers into explicit Interrupted runs.
    // No actions or lost variable values are guessed or replayed.
    public int RecoverInterruptedCycles()
    {
        lock (_lock)
        {
            using var c=Open();using var tr=c.BeginTransaction();using var q=c.CreateCommand();q.Transaction=tr;
            q.CommandText="SELECT run_id,started_utc,flow_version,recipe_id FROM run_inflight ORDER BY started_utc";
            var pending=new List<CycleStartRecord>();
            using(var r=q.ExecuteReader()) while(r.Read())
                pending.Add(new CycleStartRecord(r.GetString(0),r.GetString(1),r.IsDBNull(3)?null:r.GetString(3),
                    r.IsDBNull(2)?null:r.GetString(2)));
            foreach(var item in pending)
            {
                var row=new CycleRecord(item.Id,item.StartedAt,DateTimeOffset.UtcNow.ToString("O"),
                    "Interrupted",0,Array.Empty<StepRecord>(),"进程意外退出；执行轨迹不可恢复",item.RecipeId,item.FlowVersion);
                q.Parameters.Clear();q.CommandText="""
                    INSERT OR IGNORE INTO production_runs
                    (id,started_utc,ended_utc,outcome,duration_ms,flow_version,recipe_id,reason,payload_json)
                    VALUES($id,$start,$end,$outcome,0,$flow,$recipe,$reason,$json)
                    """;
                q.Parameters.AddWithValue("$id",row.Id);q.Parameters.AddWithValue("$start",row.StartedAt);
                q.Parameters.AddWithValue("$end",row.EndedAt);q.Parameters.AddWithValue("$outcome",row.Outcome);
                q.Parameters.AddWithValue("$flow",(object?)row.FlowVersion??DBNull.Value);
                q.Parameters.AddWithValue("$recipe",(object?)row.RecipeId??DBNull.Value);
                q.Parameters.AddWithValue("$reason",row.Reason);
                q.Parameters.AddWithValue("$json",JsonSerializer.Serialize(row));q.ExecuteNonQuery();
            }
            q.Parameters.Clear();q.CommandText="DELETE FROM run_inflight";q.ExecuteNonQuery();
            tr.Commit();return pending.Count;
        }
    }
    public void UpsertCycle(CycleRecord row)
    {
        if(row.Snapshots is not null && (row.Trace is null || row.Snapshots.Count!=row.Trace.Count ||
            row.Snapshots.Where((x,i)=>x.Ordinal!=i || x.NodeId!=row.Trace[i].Id).Any()))
            throw new InvalidOperationException("节点轨迹与变量快照序号不一致，拒绝提交周期");
        lock (_lock)
        {
            using var c = Open();using var transaction=c.BeginTransaction();using var q=c.CreateCommand();q.Transaction=transaction;
            q.CommandText="""
                INSERT OR IGNORE INTO production_runs(id,started_utc,ended_utc,outcome,duration_ms,flow_version,recipe_id,reason,payload_json)
                VALUES($id,$start,$end,$status,$ms,$flow,$recipe,$reason,$json)
                """;
            q.Parameters.AddWithValue("$id",row.Id);q.Parameters.AddWithValue("$start",row.StartedAt);
            q.Parameters.AddWithValue("$end",row.EndedAt);q.Parameters.AddWithValue("$status",row.Outcome);
            q.Parameters.AddWithValue("$ms",row.DurationMs);q.Parameters.AddWithValue("$flow",(object?)row.FlowVersion??DBNull.Value);
            q.Parameters.AddWithValue("$recipe",(object?)row.RecipeId??DBNull.Value);
            q.Parameters.AddWithValue("$reason",(object?)row.Reason??DBNull.Value);
            // Avoid embedding a second, unverified copy of the node snapshots in cycle JSON.
            // Query methods hydrate strictly from the SHA-checked evidence table.
            q.Parameters.AddWithValue("$json",JsonSerializer.Serialize(row with { Snapshots = null }));q.ExecuteNonQuery();
            if (row.Trace is not null)
            {
                q.Parameters.Clear();
                q.CommandText="""
                    INSERT OR IGNORE INTO run_node_traces
                    (run_id,ordinal,node_id,parent_id,label,node_type,outcome,duration_ms,attempt,ended_utc,error)
                    VALUES($run,$ord,$node,$parent,$label,$type,$outcome,$ms,$attempt,$ended,$error)
                    """;
                for (var i=0;i<row.Trace.Count;i++)
                {
                    var t=row.Trace[i];
                    q.Parameters.Clear();
                    q.Parameters.AddWithValue("$run",row.Id);
                    q.Parameters.AddWithValue("$ord",i);
                    q.Parameters.AddWithValue("$node",t.Id);
                    q.Parameters.AddWithValue("$parent",(object?)t.ParentId??DBNull.Value);
                    q.Parameters.AddWithValue("$label",t.Label);
                    q.Parameters.AddWithValue("$type",t.Type);
                    q.Parameters.AddWithValue("$outcome",t.Status);
                    q.Parameters.AddWithValue("$ms",t.DurationMs);
                    q.Parameters.AddWithValue("$attempt",t.Attempt);
                    q.Parameters.AddWithValue("$ended",t.EndedAt);
                    q.Parameters.AddWithValue("$error",(object?)t.Error??DBNull.Value);
                    q.ExecuteNonQuery();
                }
            }
            if(row.Snapshots is not null)
            {
                q.CommandText="""
                    INSERT INTO run_variable_snapshots
                    (run_id,ordinal,node_id,captured_utc,payload_json,sha256)
                    VALUES($run,$ord,$node,$time,$json,$sha)
                    ON CONFLICT(run_id,ordinal) DO NOTHING
                    """;
                foreach(var snapshot in row.Snapshots)
                {
                    var payload=JsonSerializer.Serialize(snapshot);
                    var digest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
                    q.Parameters.Clear();q.Parameters.AddWithValue("$run",row.Id);
                    q.Parameters.AddWithValue("$ord",snapshot.Ordinal);
                    q.Parameters.AddWithValue("$node",snapshot.NodeId);
                    q.Parameters.AddWithValue("$time",snapshot.CapturedUtc);
                    q.Parameters.AddWithValue("$json",payload);
                    q.Parameters.AddWithValue("$sha",digest);q.ExecuteNonQuery();
                }
            }
            q.Parameters.Clear();q.CommandText="DELETE FROM run_inflight WHERE run_id=$run";
            q.Parameters.AddWithValue("$run",row.Id);q.ExecuteNonQuery();
            transaction.Commit();
        }
    }
    public CycleRecord? Cycle(string id)
    {
        lock(_lock)
        {
            using var c=Open();using var q=c.CreateCommand();
            q.CommandText="SELECT payload_json FROM production_runs WHERE id=$id";q.Parameters.AddWithValue("$id",id);
            var raw=q.ExecuteScalar() as string;
            if(raw is null)return null;
            var row=JsonSerializer.Deserialize<CycleRecord>(raw);
            return row is null?null:row with { Snapshots=Snapshots(id) };
        }
    }
    public IReadOnlyList<NodeVariableSnapshot> Snapshots(string runId)
    {
        lock(_lock)
        {
            using var c=Open();using var q=c.CreateCommand();
            q.CommandText="""
                SELECT s.ordinal,s.node_id,s.payload_json,s.sha256
                FROM run_variable_snapshots s WHERE s.run_id=$run ORDER BY s.ordinal
                """;q.Parameters.AddWithValue("$run",runId);
            using var r=q.ExecuteReader();var rows=new List<NodeVariableSnapshot>();
            while(r.Read())
            {
                var json=r.GetString(2);
                var expected=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
                if(!string.Equals(expected,r.GetString(3),StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("节点快照内容校验失败");
                var snapshot=JsonSerializer.Deserialize<NodeVariableSnapshot>(json)
                    ??throw new InvalidOperationException("节点快照反序列化失败");
                if(snapshot.Ordinal!=r.GetInt32(0)||snapshot.NodeId!=r.GetString(1))
                    throw new InvalidOperationException("节点快照元数据不一致");
                rows.Add(snapshot);
            }
            return rows;
        }
    }
    public List<CycleRecord> Cycles()
    {
        lock (_lock)
        {
            using var c=Open();using var q=c.CreateCommand();
            q.CommandText="SELECT payload_json FROM production_runs ORDER BY started_utc DESC LIMIT 100";
            using var reader=q.ExecuteReader();var list=new List<CycleRecord>();
            while(reader.Read())
            {
                var row=JsonSerializer.Deserialize<CycleRecord>(reader.GetString(0));
                if(row!=null)list.Add(row with { Snapshots=Snapshots(row.Id) });
            }
            return list;
        }
    }
    public void UpsertAudit(AuditEntry row)
    {
        lock (_lock)
        {
            using var c=Open();using var q=c.CreateCommand();
            q.CommandText="""
                INSERT OR IGNORE INTO audit_events(id,time_utc,actor,role,method,path,http_status,trace_id,payload_json)
                VALUES($id,$time,$actor,$role,$method,$path,$status,$trace,$json)
                """;
            q.Parameters.AddWithValue("$id",row.Id);q.Parameters.AddWithValue("$time",row.Time);
            q.Parameters.AddWithValue("$actor",row.Actor);q.Parameters.AddWithValue("$role",row.Role);
            q.Parameters.AddWithValue("$method",row.Method);q.Parameters.AddWithValue("$path",row.Path);
            q.Parameters.AddWithValue("$status",row.Status);q.Parameters.AddWithValue("$trace",row.TraceId);
            q.Parameters.AddWithValue("$json",JsonSerializer.Serialize(row));q.ExecuteNonQuery();
        }
    }
    public List<AuditEntry> Audits()
    {
        lock (_lock)
        {
            using var c=Open();using var q=c.CreateCommand();
            q.CommandText="SELECT payload_json FROM audit_events ORDER BY time_utc ASC LIMIT 50000";
            using var reader=q.ExecuteReader();var list=new List<AuditEntry>();
            while(reader.Read()){var row=JsonSerializer.Deserialize<AuditEntry>(reader.GetString(0));if(row!=null)list.Add(row);}
            return list;
        }
    }
    public (string ActiveId,List<RecipeRevision> Revisions)? ReadRecipes()
    {
        lock (_lock)
        {
            using var c=Open();using var cmd=c.CreateCommand();
            cmd.CommandText="SELECT id FROM active_recipe WHERE singleton=1";
            var active=cmd.ExecuteScalar() as string;if(active==null)return null;
            cmd.CommandText="SELECT payload_json FROM recipe_revisions ORDER BY rowid";
            using var r=cmd.ExecuteReader();var result=new List<RecipeRevision>();
            while(r.Read()){var row=JsonSerializer.Deserialize<RecipeRevision>(r.GetString(0));if(row!=null)result.Add(row);}
            return(active,result);
        }
    }
    public void SaveRecipes(string active,List<RecipeRevision> rows)
    {
        lock (_lock)
        {
            using var c=Open();using var tr=c.BeginTransaction();
            using var cmd=c.CreateCommand();cmd.Transaction=tr;
            cmd.CommandText="DELETE FROM active_recipe; DELETE FROM recipe_revisions;";cmd.ExecuteNonQuery();
            cmd.CommandText="INSERT INTO recipe_revisions(id,state,payload_json) VALUES($id,$state,$json)";
            var pId=cmd.Parameters.Add("$id",SqliteType.Text);
            var pState=cmd.Parameters.Add("$state",SqliteType.Text);
            var pJson=cmd.Parameters.Add("$json",SqliteType.Text);
            foreach(var row in rows){pId.Value=row.Id;pState.Value=row.Status;pJson.Value=JsonSerializer.Serialize(row);cmd.ExecuteNonQuery();}
            cmd.Parameters.Clear();cmd.CommandText="INSERT INTO active_recipe(singleton,id) VALUES(1,$active)";
            cmd.Parameters.AddWithValue("$active",active);cmd.ExecuteNonQuery();tr.Commit();
        }
    }
    public List<FlowRevision> ReadFlows()
    {
        lock (_lock)
        {
            using var c=Open();using var q=c.CreateCommand();
            q.CommandText="SELECT id,status,source_id,author,created_utc,sha256,definition_json,reason FROM workflow_revisions ORDER BY rowid";
            using var reader=q.ExecuteReader();var list=new List<FlowRevision>();
            while(reader.Read())list.Add(new FlowRevision(reader.GetString(0),reader.GetString(1),reader.IsDBNull(2)?null:reader.GetString(2),
                reader.GetString(3),reader.GetString(4),reader.GetString(5),
                JsonSerializer.Deserialize<List<SequenceStep>>(reader.GetString(6),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!,reader.GetString(7)));
            return list;
        }
    }
    public string? ActiveFlowId()
    {
        lock (_lock)
        {
            using var c=Open();using var q=c.CreateCommand();q.CommandText="SELECT version_id FROM active_workflow WHERE singleton=1";
            return q.ExecuteScalar() as string;
        }
    }
    public void SaveFlowVersion(FlowRevision r, string? supersedeId=null, bool activate=false,
        WorkflowQualityGate.Report? report=null, bool requirePassingReport=false)
    {
        lock (_lock)
        {
            using var c=Open();using var t=c.BeginTransaction();using var q=c.CreateCommand();q.Transaction=t;
            if(supersedeId!=null){q.CommandText="UPDATE workflow_revisions SET status='Superseded' WHERE id=$previous";q.Parameters.AddWithValue("$previous",supersedeId);q.ExecuteNonQuery();q.Parameters.Clear();}
            q.CommandText="""
                INSERT INTO workflow_revisions(id,status,source_id,author,created_utc,sha256,definition_json,reason)
                VALUES($id,$status,$source,$author,$created,$sha,$json,$reason)
                ON CONFLICT(id) DO UPDATE SET status=excluded.status,reason=excluded.reason
                """;
            q.Parameters.AddWithValue("$id",r.Id);q.Parameters.AddWithValue("$status",r.Status);
            q.Parameters.AddWithValue("$source",(object?)r.SourceId??DBNull.Value);
            q.Parameters.AddWithValue("$author",r.Author);q.Parameters.AddWithValue("$created",r.CreatedAt);
            q.Parameters.AddWithValue("$sha",r.Hash);q.Parameters.AddWithValue("$json",JsonSerializer.Serialize(r.Definition));
            q.Parameters.AddWithValue("$reason",r.Reason);q.ExecuteNonQuery();
            if (report is not null)
            {
                if (!WorkflowQualityGate.Verify(report) ||
                    !string.Equals(report.FlowId,r.Id,StringComparison.Ordinal) ||
                    !string.Equals(report.DefinitionSha256,r.Hash,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("质量报告和流程快照的 SHA-256 不匹配");
                q.Parameters.Clear();
                q.CommandText="""
                    INSERT INTO workflow_quality_reports(report_id,flow_id,definition_sha256,report_sha256,passed,evaluated_utc,payload_json)
                    VALUES($rid,$fid,$sha,$digest,$passed,$time,$json)
                    """;
                q.Parameters.AddWithValue("$rid",report.Id);q.Parameters.AddWithValue("$fid",report.FlowId);
                q.Parameters.AddWithValue("$sha",report.DefinitionSha256);
                q.Parameters.AddWithValue("$digest",report.ReportSha256);
                q.Parameters.AddWithValue("$passed",report.Pass?1:0);
                q.Parameters.AddWithValue("$time",report.EvaluatedUtc);
                q.Parameters.AddWithValue("$json",JsonSerializer.Serialize(report));
                q.ExecuteNonQuery();
            }
            if (requirePassingReport)
            {
                q.Parameters.Clear();
                q.CommandText="""
                    SELECT COUNT(*) FROM workflow_quality_reports
                    WHERE flow_id=$fid AND definition_sha256=$sha AND passed=1
                    """;
                q.Parameters.AddWithValue("$fid",r.Id);q.Parameters.AddWithValue("$sha",r.Hash);
                if (Convert.ToInt64(q.ExecuteScalar())<1)
                    throw new InvalidOperationException("缺少与当前流程哈希一致的通过质量门禁报告");
            }
            if(activate){q.Parameters.Clear();q.CommandText="INSERT INTO active_workflow(singleton,version_id) VALUES(1,$active) ON CONFLICT(singleton) DO UPDATE SET version_id=excluded.version_id";
                q.Parameters.AddWithValue("$active",r.Id);q.ExecuteNonQuery();}
            t.Commit();
        }
    }
    public IReadOnlyList<WorkflowQualityGate.Report> QualityReports(string flowId)
    {
        lock (_lock)
        {
            using var c=Open();using var q=c.CreateCommand();
            q.CommandText="SELECT payload_json FROM workflow_quality_reports WHERE flow_id=$id ORDER BY evaluated_utc DESC LIMIT 20";
            q.Parameters.AddWithValue("$id",flowId);
            using var reader=q.ExecuteReader();var rows=new List<WorkflowQualityGate.Report>();
            while(reader.Read())
            {
                var row=JsonSerializer.Deserialize<WorkflowQualityGate.Report>(reader.GetString(0));
                if(row is not null)
                {
                    if (!WorkflowQualityGate.Verify(row))
                        throw new InvalidOperationException("SQLite 中的流程质量报告完整性校验失败");
                    rows.Add(row);
                }
            }
            return rows;
        }
    }

}
