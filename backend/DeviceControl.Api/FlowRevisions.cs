using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeviceControl.Api;

public sealed record FlowRevision(string Id,string Status,string? SourceId,string Author,string CreatedAt,
    string Hash,List<SequenceStep> Definition,string Reason);
public sealed record FlowDraftRequest(List<SequenceStep> Definition, string? ExpectedActiveVersionId = null);

/// <summary>Immutable workflow snapshots with server-side static validation and SQLite transactions.</summary>
public sealed class FlowRevisions
{
    private readonly object _sync=new();
    private readonly SqliteStore _db;
    private readonly StationConfig _base;
    private readonly List<FlowRevision> _versions;
    private string _active;
    public FlowRevisions(SqliteStore db,StationConfig config)
    {
        _db=db;_base=config;
        _versions=db.ReadFlows();_active=db.ActiveFlowId()??"";
        if(_versions.Count==0)
        {
            var first=Create(Clone(config.Sequence),"system","Published",null,"初始流程配置");
            var seedGate=WorkflowQualityGate.Evaluate(_base,first);
            if(!seedGate.Pass)throw new InvalidOperationException("初始仿真流程未通过服务端质量门禁");
            _db.SaveFlowVersion(first,activate:true,report:seedGate,requirePassingReport:true);
            _versions.Add(first);_active=first.Id;
        }
        if(!_versions.Any(v=>v.Id==_active&&v.Status=="Published"))
            throw new InvalidOperationException("SQLite 中的已发布流程版本状态不一致");
        // Legacy v0.8 databases may have a published snapshot without gate evidence.
        // Re-evaluate and bind it to immutable evidence instead of silently trusting it.
        var current=_versions.First(v=>v.Id==_active);
        if(!_db.QualityReports(current.Id).Any(r=>r.Pass&&r.DefinitionSha256==current.Hash))
        {
            var legacyGate=WorkflowQualityGate.Evaluate(_base,current);
            if(!legacyGate.Pass)throw new InvalidOperationException("既有发布流程未通过 V0.9 门禁，启动已中止");
            _db.SaveFlowVersion(current,report:legacyGate,requirePassingReport:true);
        }
    }
    public FlowRevision Active{get{lock(_sync)return _versions.First(v=>v.Id==_active);}}
    public object List(){lock(_sync)return new{active=Active,versions=_versions.AsEnumerable().Reverse().ToArray()};}
    public IReadOnlyList<WorkflowQualityGate.Report> QualityReports(string id)
    {
        lock (_sync)
        {
            if(!_versions.Any(v=>v.Id==id))throw new ArgumentException("流程版本不存在");
            return _db.QualityReports(id);
        }
    }
    private static List<SequenceStep> Clone(List<SequenceStep> sequence)=>
        JsonSerializer.Deserialize<List<SequenceStep>>(JsonSerializer.Serialize(sequence))!;
    private FlowRevision Create(List<SequenceStep> definition,string actor,string status,string? source,string reason)
    {
        var bytes=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(definition));
        return new FlowRevision("flow-"+Guid.NewGuid().ToString("N")[..10],status,source,actor,
            DateTimeOffset.UtcNow.ToString("O"),Convert.ToHexString(SHA256.HashData(bytes)),Clone(definition),reason);
    }
    public void Check(List<SequenceStep> steps)
    {
        if(steps.Count is < 1 or > 60)throw new ArgumentException("顶层流程节点必须是 1～60 个");
        // Clone base config to avoid modifying the currently running profile.
        var config=new StationConfig
        {
            ProfileId=_base.ProfileId,Driver=_base.Driver,FlowVersion="candidate",
            Signals=_base.Signals,Axes=_base.Axes,Cylinders=_base.Cylinders,
            Interlocks=_base.Interlocks,Sequence=Clone(steps)
        };
        config.Validate();
    }
    public FlowRevision Draft(List<SequenceStep> steps,string actor,string? expectedActiveVersionId=null)
    {
        lock(_sync)
        {
            if (expectedActiveVersionId != null && expectedActiveVersionId != _active)
                throw new InvalidOperationException("版本冲突：工作区来源已过期，请刷新并合并改动");
            Check(steps);
            var v=Create(steps,actor,"Draft",_active,"工作区草稿");
            _db.SaveFlowVersion(v);_versions.Add(v);return v;
        }
    }
    public FlowRevision Validate(string id)
    {
        lock(_sync)
        {
            var i=_versions.FindIndex(v=>v.Id==id);
            if(i<0||_versions[i].Status!="Draft")throw new InvalidOperationException("只能验证现有草稿");
            Check(_versions[i].Definition);
            var gate=WorkflowQualityGate.Evaluate(_base,_versions[i]);
            if(!gate.Pass)
            {
                // Failed checks are preserved as immutable diagnostics; draft remains unchanged.
                _db.SaveFlowVersion(_versions[i],report:gate);
                throw new InvalidOperationException("服务端质量门禁未通过: "+
                    (gate.Errors.FirstOrDefault() ?? gate.Results.FirstOrDefault(x=>!x.Pass)?.Name ?? gate.Warnings.FirstOrDefault()));
            }
            var next=_versions[i] with {Status="Validated",Reason="服务端质量门禁通过: "+gate.Id};
            // Snapshot status and gate evidence commit in the same SQLite transaction.
            _db.SaveFlowVersion(next,report:gate,requirePassingReport:true);
            _versions[i]=next;return next;
        }
    }
    public FlowRevision Publish(string id)
    {
        lock(_sync)
        {
            var i=_versions.FindIndex(v=>v.Id==id);
            if(i<0||_versions[i].Status!="Validated")throw new InvalidOperationException("只能发布已验证版本");
            if (_versions[i].SourceId != _active)
                throw new InvalidOperationException("版本冲突：流程草稿的基准版本已被替换，拒绝发布");
            Check(_versions[i].Definition);
            var gate=WorkflowQualityGate.Evaluate(_base,_versions[i]);
            if(!gate.Pass)
            {
                _db.SaveFlowVersion(_versions[i],report:gate);
                throw new InvalidOperationException("发布拒绝：服务端质量门禁不通过");
            }
            var next=_versions[i] with {Status="Published",Reason="管理员发布 / 门禁 "+gate.Id};
            // Publication, superseding the prior version and gate evidence are atomic.
            _db.SaveFlowVersion(next,_active,activate:true,report:gate,requirePassingReport:true);
            var p=_versions.FindIndex(v=>v.Id==_active);
            if(p>=0)_versions[p]=_versions[p] with {Status="Superseded"};
            _versions[i]=next;_active=next.Id;return next;
        }
    }
    public FlowRevision Rollback(string id,string actor)
    {
        lock(_sync)
        {
            var source=_versions.FirstOrDefault(v=>v.Id==id && (v.Status is "Published" or "Superseded"))
                ??throw new InvalidOperationException("只能回滚到曾经发布的流程快照");
            Check(source.Definition);
            var next=Create(source.Definition,actor,"Published",id,"从历史快照回滚发布");
            var gate=WorkflowQualityGate.Evaluate(_base,next);
            if(!gate.Pass)throw new InvalidOperationException("回滚发布拒绝：服务端质量门禁不通过");
            next=next with {Reason="历史快照回滚发布 / 门禁 "+gate.Id};
            _db.SaveFlowVersion(next,_active,activate:true,report:gate,requirePassingReport:true);
            var p=_versions.FindIndex(v=>v.Id==_active);
            if(p>=0)_versions[p]=_versions[p] with {Status="Superseded"};
            _versions.Add(next);_active=next.Id;return next;
        }
    }
}
