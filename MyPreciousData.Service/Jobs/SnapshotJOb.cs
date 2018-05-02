﻿using MyPreciousData.Data;
using MyPreciousData.Models;
using MyPreciousData.Service.Scheduler;
using MyPreciousData.Utils;
using MyPreciousData.VSS;
using Quartz;
using Serilog;
using System;
using System.Threading.Tasks;

namespace MyPreciousData.Service.Jobs
{
  // https://www.quartz-scheduler.net/documentation/best-practices.html
  [DisallowConcurrentExecution]
  public class SnapshotJob : IJob
  {
    public long RuleId { get; set; }
    public int RetryCount { get; set; }

    public Task Execute(IJobExecutionContext context)
    {
      VssClient vss = null;
      int maxRetryCount = -1;

      try
      {
        Log.Debug("Executing SnapshotJob for {RuleId}", RuleId);

        SnapshotRule rule = RuleMgr.Instance.GetRule(RuleId);

        if (rule == null)
        {
          Log.Error("Failed to retrieve SnapshotRule {RuleId}", RuleId);

          throw new InvalidOperationException(String.Format("Failed to retrieve SnapshotRule {0}", RuleId));
        }

        vss = new VssClient(new VssHost());
        vss.Initialize(rule.VssContext, rule.VssBackupType);

        var snapshotIds = vss.CreateSnapshot(rule.Volumes, null, rule.VssExcludeWriters, rule.VssIncludeWriters);

        PruningMgr.Instance.CreateNewInstances(rule.Id, snapshotIds);

        if (rule.BackupEnabled && rule.BackupRules != null && rule.BackupRules.Count > 0)
          ScheduleBackup(context.Scheduler);

        Log.Debug("Completed SnapshotJob for {RuleName}", RuleId);
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Failed SnapshotJob for {RuleName}", RuleId);

        Retry(ex, context.Scheduler, context.JobDetail, maxRetryCount);

        return TaskConst.Canceled;
      }
      finally
      {
        if (vss != null)
        {
          vss.Dispose();
          vss = null;
        }
      }

      return TaskConst.Completed;
    }

    protected void ScheduleBackup(IScheduler scheduler)
    {
      Log.Debug("Scheduling BackupJob for {RuleName}", RuleId);

      ITrigger backupTrigger = TriggerBuilder.Create()
        .ForJob(VssScheduler.BackupJob)
        .StartAt(DateTime.Now.AddSeconds(5))
        .UsingJobData("RuleName", RuleId)
        .Build();

      scheduler.ScheduleJob(backupTrigger).Wait();
    }

    protected void Retry(Exception ex, IScheduler scheduler, IJobDetail jobDetail, int maxRetryCount)
    {
      if (RetryCount < maxRetryCount)
      {
        Log.Information("Retrying SnapshotJob for {RuleName} in 1 minute (attempt {RetryCount}/{MaxRetryCount})", RuleId, RetryCount, maxRetryCount);

        ITrigger trigger = TriggerBuilder.Create()
          .WithIdentity(RuleId + " Retry " + RetryCount, "SnapshotJob")
          .ForJob(jobDetail)
          .StartAt(DateTime.Now.AddMinutes(1))
          .UsingJobData("RuleName", RuleId)
          .UsingJobData("RetryCount", ++RetryCount)
          .Build();

        scheduler.ScheduleJob(trigger).Wait();
      }

      else
        throw new JobExecutionException(ex, false);
    }
  }
}
