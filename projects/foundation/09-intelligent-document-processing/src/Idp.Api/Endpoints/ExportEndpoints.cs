using Idp.Api.Auth;
using Idp.Api.Contracts;
using Idp.Application.Exporting;
using Idp.Domain.Exports;

namespace Idp.Api.Endpoints;

public static class ExportEndpoints
{
    public static RouteGroupBuilder MapExportEndpoints(this RouteGroupBuilder group)
    {
        var exports = group.MapGroup("/exports").WithTags("Exports");

        exports.MapGet("/", ListAsync)
            .RequireAuthorization(Permissions.ExportManage)
            .WithName("ListExports");

        exports.MapPost("/{documentId:guid}/reexport", ReexportAsync)
            .RequireAuthorization(Permissions.ExportManage)
            .WithName("ReexportDocument");

        return group;
    }

    private static async Task<IResult> ListAsync(
        IExportRepository exports, ExportStatus? status, CancellationToken ct)
    {
        var records = await exports.ListAsync(status, ct);
        return Results.Ok(records.Select(DtoMapper.ToExport).ToList());
    }

    private static async Task<IResult> ReexportAsync(
        Guid documentId, ExportService export, CancellationToken ct)
    {
        var result = await export.ReexportAsync(documentId, ct);
        if (result is null)
            return EndpointHelpers.Problem($"Document {documentId} not found.", 404, "Not found");
        return Results.Ok(new
        {
            status = result.Status.ToString(),
            reference = result.Reference,
            attempts = result.Attempts,
            error = result.Error
        });
    }
}
