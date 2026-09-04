namespace AuditPlatform.Domain.Errors;

public enum DomainErrorCode
{
    None = 0,
    InvalidInput = 1,
    AppendOnlyViolation = 2,
    HashMismatch = 3,
    ChainBroken = 4,
    MerkleProofInvalid = 5,
    SchemaValidationFailed = 6,
    SchemaIncompatible = 7,
    TenantMissing = 8,
    LegalHoldBlocksAction = 9,
    RetentionPolicyNotFound = 10,
    NotFound = 11,
    Conflict = 12,
    Forbidden = 13,
    UnknownSchema = 14,
    RedactionForbidden = 15,
    SignatureInvalid = 16
}

public sealed class DomainException : Exception
{
    public DomainErrorCode Code { get; }
    public DomainException(DomainErrorCode code, string message) : base(message) => Code = code;
}

public readonly record struct Result<T>(bool IsSuccess, T? Value, DomainErrorCode ErrorCode, string? Error)
{
    public static Result<T> Ok(T value) => new(true, value, DomainErrorCode.None, null);
    public static Result<T> Fail(DomainErrorCode code, string error) => new(false, default, code, error);
}
