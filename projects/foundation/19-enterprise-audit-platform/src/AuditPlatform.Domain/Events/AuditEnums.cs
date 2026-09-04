namespace AuditPlatform.Domain.Events;

public enum ActorType
{
    User = 0,
    Service = 1,
    System = 2,
    Anonymous = 3
}

public enum EventOutcome
{
    Success = 0,
    Failure = 1,
    Denied = 2
}

public enum EventSeverity
{
    Info = 0,
    Notice = 1,
    Warning = 2,
    Critical = 3
}

public enum EventCategory
{
    General = 0,
    Authentication = 1,
    Authorization = 2,
    DataAccess = 3,
    DataChange = 4,
    Configuration = 5,
    PrivilegedAccess = 6,
    Export = 7,
    Verification = 8,
    Retention = 9,
    LegalHold = 10,
    Schema = 11,
    MetaAudit = 12
}
