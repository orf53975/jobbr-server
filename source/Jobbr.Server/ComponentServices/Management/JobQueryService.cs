﻿using System.Collections.Generic;
using AutoMapper;
using Jobbr.ComponentModel.JobStorage;
using Jobbr.ComponentModel.Management;
using Jobbr.ComponentModel.Management.Model;
using Jobbr.Server.Storage;

namespace Jobbr.Server.ComponentServices.Management
{
    internal class JobQueryService : IQueryService
    {
        private readonly IJobStorageProvider jobStorageProvider;
        private readonly IJobbrRepository repository;
        private readonly IMapper mapper;

        public JobQueryService(IJobStorageProvider jobStorageProvider, IJobbrRepository repository, IMapper mapper)
        {
            this.jobStorageProvider = jobStorageProvider;
            this.repository = repository;
            this.mapper = mapper;
        }

        public List<Job> GetAllJobs()
        {
            var jobs = this.repository.GetAllJobs();

            return this.mapper.Map<List<Job>>(jobs);
        }

        public Job GetJobById(long id)
        {
            var job = this.jobStorageProvider.GetJobById(id);

            return this.mapper.Map<Job>(job);
        }

        public Job GetJobByUniqueName(string uniqueName)
        {
            var job = this.repository.GetJobByUniqueName(uniqueName);

            return this.mapper.Map<Job>(job);
        }

        public IJobTrigger GetTriggerById(long triggerId)
        {
            var trigger = this.repository.GetTriggerById(triggerId);

            return this.mapper.Map<IJobTrigger>(trigger);
        }

        public List<IJobTrigger> GetTriggersByJobId(long jobId)
        {
            var triggers = this.repository.GetTriggersByJobId(jobId);

            return this.mapper.Map<List<IJobTrigger>>(triggers);
        }

        public List<IJobTrigger> GetActiveTriggers()
        {
            var triggers = this.repository.GetActiveTriggers();

            return this.mapper.Map<List<IJobTrigger>>(triggers);
        }

        public List<JobRun> GetJobRuns()
        {
            var jobRuns = this.repository.GetAllJobRuns();

            return this.mapper.Map<List<JobRun>>(jobRuns);
        }

        public JobRun GetJobRunById(long id)
        {
            var jobRun = this.repository.GetJobRunById(id);

            return this.mapper.Map<JobRun>(jobRun);
        }

        public List<JobRun> GetJobRunsByTriggerId(long triggerId)
        {
            var jobRun = this.jobStorageProvider.GetJobRunsByTriggerId(triggerId);

            return this.mapper.Map<List<JobRun>>(jobRun);
        }

        public List<JobRun> GetJobRunsByUserIdOrderByIdDesc(long userId)
        {
            var jobRun = this.jobStorageProvider.GetJobRunsForUserId(userId);

            return this.mapper.Map<List<JobRun>>(jobRun);
        }

        public List<JobRun> GetJobRunsByUserNameOrderByIdDesc(string userName)
        {
            var jobRun = this.jobStorageProvider.GetJobRunsForUserName(userName);

            return this.mapper.Map<List<JobRun>>(jobRun);
        }
    }
}