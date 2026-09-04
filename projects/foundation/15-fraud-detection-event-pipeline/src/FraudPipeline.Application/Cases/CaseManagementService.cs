using FraudPipeline.Application.Abstractions;
using FraudPipeline.Application.FeatureStore;
using FraudPipeline.Application.Scoring;
using FraudPipeline.Domain.Abstractions;
using FraudPipeline.Domain.Entities;
using FraudPipeline.Domain.ValueObjects;

namespace FraudPipeline.Application.Cases;

public sealed class CaseManagementOptions
{
    public int AlertThresholdScore { get; set; } = 500;
    public string PrimaryLinkageEntity { get; set; } = "Card";
}

/// <summary>
/// Watches scoring results and raises alerts + groups them into cases by
/// entity linkage. Linkage strategy: primary key by card id; when a case
/// already exists for the same primary key it is reused.
/// </summary>
public sealed class CaseManagementService
{
    private readonly IAlertRepository _alerts;
    private readonly ICaseRepository _cases;
    private readonly IIdGenerator _ids;
    private readonly IClock _clock;
    private readonly CaseManagementOptions _options;

    public CaseManagementService(
        IAlertRepository alerts,
        ICaseRepository cases,
        IIdGenerator ids,
        IClock clock,
        CaseManagementOptions options)
    {
        _alerts = alerts;
        _cases = cases;
        _ids = ids;
        _clock = clock;
        _options = options;
    }

    public async Task<(Alert Alert, Case Case)?> HandleAsync(Transaction txn, ScoreResult result, CancellationToken ct)
    {
        if (result.Shadow) return null;
        if (result.Score < _options.AlertThresholdScore) return null;

        var primaryKey = _options.PrimaryLinkageEntity switch
        {
            "Customer" => EntityId.Of(EntityType.Customer, txn.CustomerId).Composite,
            "Device" => EntityId.Of(EntityType.Device, txn.DeviceId).Composite,
            _ => EntityId.Of(EntityType.Card, txn.CardId).Composite
        };

        var now = _clock.UtcNow;
        var alert = new Alert(_ids.NewGuid(), txn.Id, primaryKey, result.Score, result.Reasons, now);

        var open = await _cases.GetOpenByEntityKeyAsync(primaryKey, ct);
        Case c;
        if (open is null)
        {
            c = new Case(_ids.NewGuid(), primaryKey, txn.Amount.Currency, now);
            await _cases.AddAsync(c, ct);
        }
        else
        {
            c = open;
        }

        c.LinkAlert(alert.Id, result.Score, txn.Amount.Amount, now);
        alert.AttachToCase(c.Id);
        await _alerts.AddAsync(alert, ct);
        await _alerts.SaveAsync(ct);
        await _cases.SaveAsync(ct);
        return (alert, c);
    }
}
