using FieldOps.Domain;

namespace FieldOps.UnitTests;

public sealed class JobStateMachineTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-03T00:00:00Z");

    public static TheoryData<JobStatus, JobStatus, bool> TransitionCases => new()
    {
        { JobStatus.Draft, JobStatus.Scheduled, true },
        { JobStatus.Draft, JobStatus.Cancelled, true },
        { JobStatus.Draft, JobStatus.Completed, false },
        { JobStatus.Scheduled, JobStatus.Dispatched, true },
        { JobStatus.Scheduled, JobStatus.InProgress, false },
        { JobStatus.Dispatched, JobStatus.InProgress, true },
        { JobStatus.Dispatched, JobStatus.Failed, true },
        { JobStatus.InProgress, JobStatus.Completed, true },
        { JobStatus.InProgress, JobStatus.Failed, true },
        { JobStatus.Completed, JobStatus.Draft, false },
        { JobStatus.Cancelled, JobStatus.Scheduled, false },
        { JobStatus.Failed, JobStatus.InProgress, false }
    };

    [Theory]
    [MemberData(nameof(TransitionCases))]
    public void CanTransition_Matrix_ReturnsExpected(JobStatus from, JobStatus to, bool expected)
    {
        Assert.Equal(expected, Job.CanTransition(from, to));
    }

    [Fact]
    public void TransitionTo_InvalidTransition_ThrowsDomainRule()
    {
        var job = CreateJob();
        Assert.Throws<DomainRuleException>(() => job.TransitionTo(JobStatus.Completed, Now));
    }

    [Fact]
    public void TransitionTo_CompletedAfterSla_MarksBreachAndCompletionTime()
    {
        var job = CreateJob(slaDueAt: Now.AddHours(2));
        job.TransitionTo(JobStatus.Scheduled, Now);
        job.TransitionTo(JobStatus.Dispatched, Now);
        job.TransitionTo(JobStatus.InProgress, Now.AddHours(1));
        job.TransitionTo(JobStatus.Completed, Now.AddHours(3));
        Assert.True(job.IsSlaBreached);
        Assert.Equal(Now.AddHours(3), job.CompletedAt);
    }

    [Fact]
    public void Assign_EmptyUser_ThrowsDomainRule()
    {
        var job = CreateJob();
        Assert.Throws<DomainRuleException>(() => job.Assign(Guid.Empty));
    }

    [Fact]
    public void Constructor_InvalidSchedule_ThrowsDomainRule()
    {
        Assert.Throws<DomainRuleException>(() => new Job(
            Guid.NewGuid(), "Service", "", JobPriority.Normal, Now, Now, Now.AddHours(1), Now));
    }

    [Fact]
    public void RefreshSla_OpenLateJob_MarksBreach()
    {
        var job = CreateJob(slaDueAt: Now.AddHours(1));
        job.RefreshSla(Now.AddHours(2));
        Assert.True(job.IsSlaBreached);
    }

    private static Job CreateJob(DateTimeOffset? slaDueAt = null) =>
        new(
            Guid.NewGuid(),
            "Service equipment",
            "Synthetic test job",
            JobPriority.High,
            Now,
            Now.AddHours(2),
            slaDueAt ?? Now.AddHours(3),
            Now);
}
