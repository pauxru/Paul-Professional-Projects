using Contoso.Storefront.Application.Ports;

namespace Contoso.Storefront.Application.Release;

public static class FeatureFlags
{
    public const string NewPricingDarkLaunch = "NewPricingDarkLaunch";
}

public sealed record PricePreview(
    decimal CurrentPrice,
    string Currency,
    bool DarkLaunchEvaluated,
    decimal? CandidatePrice);

public sealed class PricingPreviewService(IFeatureFlagProvider featureFlags)
{
    public PricePreview Preview(ProductSnapshot product)
    {
        var darkLaunch = featureFlags.IsEnabled(FeatureFlags.NewPricingDarkLaunch);
        decimal? candidate = darkLaunch
            ? decimal.Round(product.Price * 0.95m, 2, MidpointRounding.AwayFromZero)
            : null;

        return new PricePreview(product.Price, product.Currency, darkLaunch, candidate);
    }
}
