﻿using System;
using System.Collections.Generic;
using System.Linq;
using Jobbr.ComponentModel.Execution;
using Jobbr.ComponentModel.Execution.Model;
using Jobbr.ComponentModel.JobStorage.Model;
using Jobbr.Server.Logging;
using Jobbr.Server.Storage;
using NCrontab;
using JobRunStates = Jobbr.ComponentModel.JobStorage.Model.JobRunStates;

namespace Jobbr.Server.Scheduling
{
    public class InstantJobRunPlaner
    {
        internal PlanResult Plan(InstantTrigger trigger, bool isNew = false)
        {
            if (!trigger.IsActive)
            {
                return PlanResult.FromAction(PlanAction.Obsolete);
            }

            var baseDateTimeUtc = trigger.CreatedAtUtc;
            var calculatedNextRun = baseDateTimeUtc.AddMinutes(trigger.DelayedMinutes);

            if (calculatedNextRun < DateTime.UtcNow && !isNew)
            {
                return PlanResult.FromAction(PlanAction.Obsolete);
            }

            return new PlanResult {Action = PlanAction.Possible, ExpectedStartDateUtc = calculatedNextRun};
        }
    }

    public class ScheduledJobRunPlaner
    {
        internal PlanResult Plan(ScheduledTrigger trigger)
        {
            if (!trigger.IsActive)
            {
                return PlanResult.FromAction(PlanAction.Obsolete);
            }

            var calculatedNextRun = trigger.StartDateTimeUtc;

            if (calculatedNextRun < DateTime.UtcNow)
            {
                return PlanResult.FromAction(PlanAction.Obsolete);
            }

            return new PlanResult { Action = PlanAction.Possible, ExpectedStartDateUtc = calculatedNextRun };
        }
    }

    public class RecurringJobRunPlaner
    {
        private static readonly ILog Logger = LogProvider.For<NewScheduler>();

        private readonly JobbrRepository jobbrRepository;

        public RecurringJobRunPlaner(JobbrRepository jobbrRepository)
        {
            this.jobbrRepository = jobbrRepository;
        }

        internal PlanResult Plan(RecurringTrigger trigger)
        {
            if (!trigger.IsActive)
            {
                return PlanResult.FromAction(PlanAction.Obsolete);
            }

            if (trigger.NoParallelExecution)
            {
                if (this.jobbrRepository.CheckParallelExecution(trigger.Id) == false)
                {
                    var job = this.jobbrRepository.GetJob(trigger.JobId);

                    Logger.InfoFormat("No Parallel Execution: prevented planning of new JobRun for Job '{0}' (JobId: {1}). Caused by trigger with id '{2}' (Type: '{3}', userId: '{4}', userName: '{5}')",
                        job.UniqueName,
                        job.Id,
                        trigger.Id,
                        trigger.GetType().Name,
                        trigger.UserId,
                        trigger.UserName);

                    return PlanResult.FromAction(PlanAction.Blocked);
                }
            }

            DateTime baseTime;

            // Calculate the next occurance
            if (trigger.StartDateTimeUtc.HasValue && trigger.StartDateTimeUtc.Value > DateTime.UtcNow)
            {
                baseTime = trigger.StartDateTimeUtc.Value;
            }
            else
            {
                baseTime = DateTime.UtcNow;
            }

            var schedule = CrontabSchedule.Parse(trigger.Definition);

            return new PlanResult
            {
                Action = PlanAction.Possible,
                ExpectedStartDateUtc = schedule.GetNextOccurrence(baseTime)
            };
        }
    }

    internal struct PlanResult
    {
        internal PlanAction Action;
        internal DateTime? ExpectedStartDateUtc;

        internal static PlanResult FromAction(PlanAction action)
        {
            return new PlanResult() { Action = action };
        }
    }

    internal enum PlanAction
    {
        Obsolete,
        Blocked,
        Possible
    }

    public class NewScheduler : IJobScheduler
    {
        private static readonly ILog Logger = LogProvider.For<NewScheduler>();

        private readonly IJobbrRepository repository;
        private readonly IJobExecutor executor;
        private List<TriggerPlannedJobRunCombination> currentPlan = new List<TriggerPlannedJobRunCombination>();

        private InstantJobRunPlaner instantJobRunPlaner;
        private ScheduledJobRunPlaner scheduledJobRunPlaner;
        private RecurringJobRunPlaner recurringJobRunPlaner;
        private readonly DefaultSchedulerConfiguration configuration;

        public NewScheduler(IJobbrRepository repository, IJobExecutor executor, InstantJobRunPlaner instantJobRunPlaner, ScheduledJobRunPlaner scheduledJobRunPlaner, RecurringJobRunPlaner recurringJobRunPlaner, DefaultSchedulerConfiguration configuration)
        {
            this.repository = repository;
            this.executor = executor;

            this.instantJobRunPlaner = instantJobRunPlaner;
            this.scheduledJobRunPlaner = scheduledJobRunPlaner;
            this.recurringJobRunPlaner = recurringJobRunPlaner;

            this.configuration = configuration;
        }

        public void Dispose()
        {
        }

        public void Start()
        {
            this.CreateInitialPlan();
        }

        public void Stop()
        {
        }

        public void OnTriggerDefinitionUpdated(long triggerId)
        {
            Logger.Info($"The trigger with id '{triggerId}' has been updated. Reflecting changes to Plan if any.");

            var trigger = this.repository.GetTriggerById(triggerId);

            PlanResult planResult = this.GetPlanResult(trigger as dynamic, false);

            if (planResult.Action != PlanAction.Possible)
            {
                Logger.Debug($"The trigger was not considered to be relevant to the plan, skipping. PlanResult was '{planResult.Action}'");
                return;
            }

            var dateTime = planResult.ExpectedStartDateUtc;

            if (!dateTime.HasValue)
            {
                Logger.Warn($"Unable to gather an expected start date for trigger, skipping.");
                return;
            }

            // Get the next occurence from database
            var dependentJobRun = this.repository.GetNextJobRunByTriggerId(trigger.Id);

            if (dependentJobRun == null)
            {
                Logger.Error($"Trigger was updated before job run has been created. Cannot apply update.");
                return;
            }

            this.UpdatePlannedJobRun(dependentJobRun, trigger, dateTime.Value);
        }

        public void OnTriggerStateUpdated(long triggerId)
        {
            Logger.Info($"The trigger with id '{triggerId}' has been changed its state. Reflecting changes to Plan if any.");

            var trigger = this.repository.GetTriggerById(triggerId);

            PlanResult planResult = this.GetPlanResult(trigger as dynamic, false);

            if (planResult.Action == PlanAction.Obsolete)
            {
                // Remove from in memory plan to not publish this in future
                this.currentPlan.RemoveAll(e => e.TriggerId == triggerId);

                // Set the JobRun to deleted if any
                var dependentJobRun = this.repository.GetNextJobRunByTriggerId(trigger.Id);

                if (dependentJobRun != null)
                {
                    dependentJobRun.State = JobRunStates.Deleted;
                    this.repository.Update(dependentJobRun);
                }

                this.PublishCurrentPlan();

                return;
            }

            if (planResult.Action == PlanAction.Possible)
            {
                var newItem = this.CreateNew(planResult, trigger);

                if (newItem != null)
                {
                    this.currentPlan.Add(newItem);

                    this.PublishCurrentPlan();
                }
            }
        }

        public void OnTriggerAdded(long triggerId)
        {
            Logger.Info($"The trigger with id '{triggerId}' has been added. Reflecting changes to the current plan.");

            var trigger = this.repository.GetTriggerById(triggerId);

            PlanResult planResult = this.GetPlanResult(trigger as dynamic, true);

            if (planResult.Action != PlanAction.Possible)
            {
                Logger.Debug($"The trigger was not considered to be relevant to the plan, skipping. PlanResult was '{planResult.Action}'");
                return;
            }

            var newItem = this.CreateNew(planResult, trigger);

            if (newItem == null)
            {
                Logger.Error($"Unable to create a new Planned Item with a JobRun.");
                return;
            }

            this.currentPlan.Add(newItem);

            this.PublishCurrentPlan();
        }

        public void OnJobRunEnded(Guid uniqueId)
        {
            // Remove from in memory plan to not publish this in future
            this.currentPlan.RemoveAll(e => e.UniqueId == uniqueId);
        }

        private TriggerPlannedJobRunCombination CreateNew(PlanResult planResult, JobTriggerBase trigger)
        {
            var dateTime = planResult.ExpectedStartDateUtc;

            if (!dateTime.HasValue)
            {
                Logger.Warn($"Unable to gather an expected start date for trigger, skipping.");

                return null;
            }

            // Create the next occurence from database
            var newJobRun = this.CreateNewJobRun(trigger, dateTime.Value);

            // Add to the initial plan
            var newItem = new TriggerPlannedJobRunCombination
            {
                TriggerId = trigger.Id,
                UniqueId = newJobRun.UniqueId,
                PlannedStartDateTimeUtc = newJobRun.PlannedStartDateTimeUtc
            };

            return newItem;
        }

        private void CreateInitialPlan()
        {
            var activeTriggers = this.repository.GetActiveTriggers();

            var newPlan = new List<TriggerPlannedJobRunCombination>();
            foreach (var trigger in activeTriggers)
            {
                PlanResult planResult = this.GetPlanResult(trigger as dynamic, false);

                if (planResult.Action == PlanAction.Obsolete)
                {
                    Logger.WarnFormat($"Disabling trigger with id '{trigger.Id}', because startdate is in the past. (Type: '{trigger.GetType().Name}', userId: '{trigger.UserId}', userName: '{trigger.UserName}')");

                    this.repository.DisableTrigger(trigger.Id);
                    continue;
                }

                if (planResult.Action == PlanAction.Blocked)
                {
                    // Cannot schedule jobrun, one reason could be that this job is not allowed to run because another jobrun is active
                    continue;
                }

                if (planResult.Action == PlanAction.Possible)
                {
                    if (planResult.ExpectedStartDateUtc == null)
                    {
                        // Move to ctor of PlanResult
                        throw new ArgumentNullException("ExpectedStartDateUtc");
                    }

                    var dateTime = planResult.ExpectedStartDateUtc;

                    // Get the next occurence from database
                    var dependentJobRun = this.repository.GetNextJobRunByTriggerId(trigger.Id);

                    if (dependentJobRun != null)
                    {
                        this.UpdatePlannedJobRun(dependentJobRun, trigger, dateTime.Value);
                    }
                    else
                    {
                        dependentJobRun = this.CreateNewJobRun(trigger, dateTime.Value);
                    }

                    // Add to the initial plan
                    newPlan.Add(new TriggerPlannedJobRunCombination()
                    {
                        TriggerId = trigger.Id,
                        UniqueId = dependentJobRun.UniqueId,
                        PlannedStartDateTimeUtc = dependentJobRun.PlannedStartDateTimeUtc
                    });
                }
            }

            // Set current plan
            this.currentPlan = newPlan;

            // Publish the initial plan top the Excutor
            this.PublishCurrentPlan();
        }

        private void PublishCurrentPlan()
        {
            Logger.Info($"Publishing new plan for upcoming jobs to the executor. Number of Items: {this.currentPlan.Count}");

            var clone = this.currentPlan.Select(e => new PlannedJobRun() { PlannedStartDateTimeUtc = e.PlannedStartDateTimeUtc, UniqueId = e.UniqueId }).ToList();

            try
            {
                this.executor.OnPlanChanged(clone);
            }
            catch (Exception e)
            {
                Logger.WarnException("Unable to publish current plan to Executor", e);
            }
        }

        private JobRun CreateNewJobRun(JobTriggerBase trigger, DateTime dateTime)
        {
            var job = this.repository.GetJob(trigger.JobId);

            var jobRun = this.repository.SaveNewJobRun(job, trigger, dateTime);

            return jobRun;
        }

        private void UpdatePlannedJobRun(JobRun plannedNextRun, JobTriggerBase trigger, DateTime calculatedNextRun)
        {
            // Is this value in sync with the schedule table?
            if (plannedNextRun.PlannedStartDateTimeUtc == calculatedNextRun)
            {
                Logger.DebugFormat(
                    "The previously planned startdate '{0}' is still correct for JobRun (id: {1}) triggered by trigger with id '{2}' (Type: '{3}', userId: '{4}', userName: '{5}')",
                    calculatedNextRun,
                    plannedNextRun.Id,
                    trigger.Id,
                    trigger.GetType().Name,
                    trigger.UserId,
                    trigger.UserName);

                return;
            }

            // Was the change too close before the execution date?
            if (DateTime.UtcNow.AddSeconds(this.configuration.AllowChangesBeforeStartInSec) >= calculatedNextRun)
            {
                Logger.WarnFormat(
                    "The planned startdate '{0}' has changed to '{1}'. This change was done too close (less than {2} seconds) to the next planned run and cannot be adjusted",
                    plannedNextRun.PlannedStartDateTimeUtc,
                    calculatedNextRun,
                    this.configuration.AllowChangesBeforeStartInSec);

                return;
            }

            Logger.WarnFormat("The calculated startdate '{0}' has changed to '{1}', the planned jobRun needs to be updated as next step", plannedNextRun.PlannedStartDateTimeUtc, calculatedNextRun);

            plannedNextRun.PlannedStartDateTimeUtc = calculatedNextRun;
            this.repository.Update(plannedNextRun);
        }

        private PlanResult GetPlanResult(InstantTrigger trigger, bool isNew = false) => this.instantJobRunPlaner.Plan(trigger, isNew);

        // ReSharper disable once UnusedParameter.Local
        // Reason: Parameter is used by dynamic overload
        private PlanResult GetPlanResult(ScheduledTrigger trigger, bool isNew = false) => this.scheduledJobRunPlaner.Plan(trigger);

        // ReSharper disable once UnusedParameter.Local
        // Reason: Parameter is used by dynamic overload
        private PlanResult GetPlanResult(RecurringTrigger trigger, bool isNew = false) => this.recurringJobRunPlaner.Plan(trigger);

        // ReSharper disable once UnusedParameter.Local
        // Reason: Parameter is used by dynamic overload
        private PlanResult GetPlanResult(object trigger, bool isNew)
        {
            throw new NotImplementedException($"Unable to dynamic dispatch trigger of type '{trigger?.GetType().Name}'");
        }
    }

    internal class TriggerPlannedJobRunCombination
    {
        public Guid UniqueId { get; set; }

        public DateTime PlannedStartDateTimeUtc { get; set; }

        public long TriggerId { get; set; }
    }
}