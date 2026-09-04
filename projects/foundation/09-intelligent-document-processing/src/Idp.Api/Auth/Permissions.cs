namespace Idp.Api.Auth;

/// <summary>The authorization permissions (JWT "perm" claims) and their policy names.</summary>
public static class Permissions
{
    public const string ClaimType = "perm";

    public const string DocumentsSubmit = "documents:submit";
    public const string ReviewProcess = "review:process";
    public const string ReviewApprove = "review:approve";
    public const string ExportManage = "export:manage";

    public static readonly string[] All =
        { DocumentsSubmit, ReviewProcess, ReviewApprove, ExportManage };
}
