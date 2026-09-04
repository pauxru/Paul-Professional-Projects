namespace Idp.Domain.Review;

public enum ReviewStatus
{
    Pending = 0,
    Claimed = 1,
    Completed = 2
}

public enum ReviewResolution
{
    None = 0,
    Approved = 1,
    Corrected = 2,
    Rejected = 3
}
