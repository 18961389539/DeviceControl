using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace DeviceControl.Api;

public sealed record SandboxCheck(string Code, bool Passed, string Message);
public sealed record RecoverySandboxReport(string DeploymentId, string StationId, string CheckedAt,
    bool ReadOnly, bool WritesProduction, bool RequiresManualReview, bool BackupValidated,
    string? BackupFlowId, string? BackupRecipeId, string CurrentFlowId, string CurrentRecipeId,
    SandboxCheck[] Checks);

/// <summary>
/// V1.9: diagnostic probe of a PRIVATE TEMPORARY COPY of a deployment backup.
/// Never opens production DB files, restores data or changes a deployment's status.
/// This is not an automated recovery authorization.
/// </summary>
public sealed class RecoverySandboxInspector
{
    private readonly object _gate = new();
    private readonly string _deployments;
    private readonly string _sandboxes;
    private readonly StationUpgradeManager _upgrades;
    private readonly MultiStationRegistry _fleet;
    private const long MaxBackupBytes = 128L * 1024 * 1024;

    public RecoverySandboxInspector(IHostEnvironment env, StationUpgradeManager upgrades, MultiStationRegistry fleet)
    {
        _upgrades = upgrades; _fleet = fleet;
        _deployments = Path.GetFullPath(Path.Combine(env.ContentRootPath, "data", "deployments"));
        _sandboxes = Path.GetFullPath(Path.Combine(env.ContentRootPath, "data", "recovery-sandboxes"));
        Directory.CreateDirectory(_sandboxes);
    }

    public RecoverySandboxReport Inspect(string deploymentId)
    {
        lock (_gate)
        {
            // The user supplies only an opaque ID; never a filesystem path.
            var record = _upgrades.History().SingleOrDefault(x => x.Id == deploymentId)
                ?? throw new ArgumentException("部署记录不存在");
            if (!string.Equals(record.Id, deploymentId, StringComparison.Ordinal))
                throw new ArgumentException("部署日志编号不一致");
            var station = _fleet.Get(record.StationId);
            var checks = new List<SandboxCheck>();
            string? flow = null, recipe = null;
            string? snapshotFile = null, scratch = null;
            void Check(string code, bool pass, string message) => checks.Add(new(code, pass, message));
            try
            {
                var expectedFile = Path.GetFullPath(Path.Combine(_deployments, "backups", station.Id, record.Id + ".db"));
                // The persisted journal is not trusted to choose arbitrary files.
                var relative = record.BackupFile ?? "";
                var candidate = Path.GetFullPath(Path.Combine(_deployments, relative));
                var validPath = relative.Length > 0 && Path.IsPathFullyQualified(relative) == false &&
                    string.Equals(candidate, expectedFile, StringComparison.Ordinal) &&
                    (!File.Exists(expectedFile) || !new FileInfo(expectedFile).Attributes.HasFlag(FileAttributes.ReparsePoint)) &&
                    (!Directory.Exists(_deployments) || !new DirectoryInfo(_deployments).Attributes.HasFlag(FileAttributes.ReparsePoint)) &&
                    (!Directory.Exists(Path.Combine(_deployments, "backups")) ||
                        !new DirectoryInfo(Path.Combine(_deployments, "backups")).Attributes.HasFlag(FileAttributes.ReparsePoint)) &&
                    (!Directory.Exists(Path.GetDirectoryName(expectedFile)!) ||
                        !new DirectoryInfo(Path.GetDirectoryName(expectedFile)!).Attributes.HasFlag(FileAttributes.ReparsePoint));
                Check("backup-path", validPath, validPath ? "只允许使用部署编号对应的受控备份路径" : "备份路径非法或指向符号链接");
                if (validPath)
                {
                    var file = new FileInfo(expectedFile);
                    var present = file.Exists && file.Length > 0 && file.Length <= MaxBackupBytes;
                    Check("backup-size", present, present ? "备份存在且大小在 128 MiB 限制内" : "备份缺失、为空或超过诊断大小限制");
                    if (present)
                    {
                        scratch = Path.Combine(_sandboxes, "scan-" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(scratch);
                        snapshotFile = Path.Combine(scratch, "snapshot.db");
                        File.Copy(expectedFile, snapshotFile, overwrite: false);
                        using var stream = File.OpenRead(snapshotFile);
                        var hash = Convert.ToHexString(SHA256.HashData(stream));
                        var matches = !string.IsNullOrWhiteSpace(record.BackupSha256) &&
                            hash.Equals(record.BackupSha256, StringComparison.OrdinalIgnoreCase);
                        Check("sha256", matches, matches ? "沙箱副本 SHA-256 与原部署日志匹配" : "哈希不匹配；不允许检查或恢复此备份");
                        if (matches)
                        {
                            var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
                                DataSource = snapshotFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false
                            }.ToString());
                            using (connection)
                            {
                                connection.Open();
                                using var integrity = connection.CreateCommand();
                                integrity.CommandText = "PRAGMA integrity_check";
                                var passed = string.Equals(integrity.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase);
                                Check("sqlite-integrity", passed, passed ? "沙箱数据库完整性检查通过" : "SQLite integrity_check 未通过");
                                if (passed)
                                {
                                    using var revisions = connection.CreateCommand();
                                    revisions.CommandText = "SELECT version_id FROM active_workflow WHERE singleton=1";
                                    flow = revisions.ExecuteScalar()?.ToString();
                                    revisions.CommandText = "SELECT id FROM active_recipe WHERE singleton=1";
                                    recipe = revisions.ExecuteScalar()?.ToString();
                                    Check("baseline", flow == record.BeforeFlow.Id && recipe == record.BeforeRecipe.Id,
                                        "沙箱原始流程/配方 " + (flow ?? "(无)") + " / " + (recipe ?? "(无)"));
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or ArgumentException)
            {
                Check("probe-error", false, "沙箱检查失败：" + ex.GetType().Name);
            }
            finally
            {
                if (scratch is not null && Directory.Exists(scratch))
                {
                    // Best effort cleanup: leftover copies are never used by the live runtime.
                    try { Directory.Delete(scratch, recursive: true); }
                    catch (IOException) { Check("sandbox-cleanup", false, "临时副本清理失败，需要运维清理隔离目录"); }
                    catch (UnauthorizedAccessException) { Check("sandbox-cleanup", false, "临时副本清理失败，需要运维清理隔离目录"); }
                }
            }
            return new RecoverySandboxReport(record.Id, station.Id, DateTimeOffset.UtcNow.ToString("O"),
                ReadOnly: true, WritesProduction: false, RequiresManualReview: true,
                BackupValidated: checks.Any(c => c.Code == "baseline" && c.Passed) && checks.All(c => c.Passed),
                BackupFlowId: flow, BackupRecipeId: recipe,
                CurrentFlowId: station.Flows.Active.Id, CurrentRecipeId: station.Recipes.Active.Id,
                Checks: checks.ToArray());
        }
    }
}
