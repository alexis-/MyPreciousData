﻿using MyPreciousData.Models;
using MyPreciousData.Utils;
using MyPreciousData.VSS;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MyPreciousData.Data
{
  public class PruningMgr
  {
    private ConcurrentDictionary<long, List<SnapshotInstance>> SnapshotRuleInstanceMap { get; set; }
    private object _lockSave = new object();
    
    protected PruningMgr() { }

    protected static PruningMgr _instance = null;
    public static PruningMgr Instance => _instance ?? (_instance = new PruningMgr());

    public void LoadInstances(IEnumerable<SnapshotInstance> instances)
    {
      SnapshotRuleInstanceMap = new ConcurrentDictionary<long, List<SnapshotInstance>>();

      foreach (var instance in instances)
      {
        List<SnapshotInstance> ruleInstances = SafeGetInstances(instance.SnapshotRuleId);

        ruleInstances.Add(instance);
      }
    }

    public void DoPruning(VssClient vss)
    {
      CleanupGhostInstances(vss);
      IEnumerable<SnapshotInstance> pruneList = GetPruningList();

      foreach (SnapshotInstance instance in pruneList)
        vss.DeleteSnapshot(instance.SnapshotId);

      CleanupInstances(pruneList.Select(i => i.SnapshotId));

      Save();
    }

    public void CreateNewInstances(long ruleId, IEnumerable<Guid> snapshotIds)
    {
      List<SnapshotInstance> ruleInstances = SafeGetInstances(ruleId);
      DateTime now = DateTime.Now;

      ruleInstances.AddRange(snapshotIds.Select(id => new SnapshotInstance()
      {
        SnapshotId = id,
        SnapshotRuleId = ruleId,
        CreatedAt = now
      }));

      Save();
    }

    // Ugly fix this
    private void Save()
    {
      lock (_lockSave)
      {
        IEnumerable<SnapshotInstance> allInstances = new List<SnapshotInstance>();

        allInstances = SnapshotRuleInstanceMap.Values.Aggregate(allInstances, (acc, it) => acc.Concat(it)).ToList();

        ConfigLoader.SaveToFile(allInstances, ConfigLoader.SnapshotInstancesFileName);
      }
    }

    private List<SnapshotInstance> SafeGetInstances(long ruleId)
    {
      List<SnapshotInstance> ret = SnapshotRuleInstanceMap.SafeGet(
        ruleId,
        new List<SnapshotInstance>()
      );

      SnapshotRuleInstanceMap[ruleId] = ret;

      return ret;
    }

    private void CleanupInstances(IEnumerable<Guid> toRm)
    {
      foreach (List<SnapshotInstance> instances in SnapshotRuleInstanceMap.Values)
        instances.RemoveAll(i => toRm.Contains(i.SnapshotId));
    }

    private void CleanupGhostInstances(VssClient vss)
    {
      IEnumerable<Guid> existingSnapshots = vss.QuerySnapshotSet().Select(sp => sp.SnapshotId);

      foreach (List<SnapshotInstance> instances in SnapshotRuleInstanceMap.Values)
        instances.RemoveAll(i => existingSnapshots.Contains(i.SnapshotId) == false);
    }

    private IEnumerable<SnapshotInstance> GetPruningList()
    {
      return RuleMgr.Instance.Rules.SelectMany(r => GetPruningListForRule(r));
    }

    private IEnumerable<SnapshotInstance> GetPruningListForRule(SnapshotRule rule)
    {
      List<SnapshotInstance> instances = SnapshotRuleInstanceMap.SafeGet(rule.Id);

      if (instances == null)
        return new List<SnapshotInstance>();

      long lifetime = rule.GetLifeTimeSeconds();

      return instances.Where(i => i.CreatedAt.AddSeconds(lifetime) < DateTime.Now).ToList();
    }
  }
}
