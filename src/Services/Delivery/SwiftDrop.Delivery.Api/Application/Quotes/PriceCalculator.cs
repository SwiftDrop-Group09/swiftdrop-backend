using System.Numerics;

namespace SwiftDrop.Delivery.Api.Application.Quotes;

public readonly record struct PriceBreakdown(decimal BaseFee, decimal WeightFee, decimal Total);

public static class PriceCalculator
{
    private const decimal MoneyLimit = 10000000000000000m;

    public static bool TryCalculate(decimal baseFee, decimal perKgFee, decimal weightKg, out PriceBreakdown price)
    {
        price = default;
        if (!ValidMoney(baseFee) || !ValidMoney(perKgFee) || weightKg is <= 0 or > 30)
        {
            return false;
        }

        // A decimal multiplication can round before the final monetary rounding.
        // Keep the exact weight coefficient until the single half-up cent decision.
        var (weightCoefficient, weightScale) = Parts(weightKg);
        var denominator = BigInteger.Pow(10, weightScale);
        var excessWeight = BigInteger.Max(BigInteger.Zero, weightCoefficient - denominator);
        var numerator = Cents(perKgFee) * excessWeight;
        var weightCents = BigInteger.DivRem(numerator, denominator, out var remainder);
        if ((remainder * 2) >= denominator)
        {
            weightCents++;
        }

        var totalCents = Cents(baseFee) + weightCents;
        var limitCents = new BigInteger(MoneyLimit * 100m);
        if (weightCents >= limitCents || totalCents >= limitCents)
        {
            return false;
        }

        price = new(baseFee, (decimal)weightCents / 100m, (decimal)totalCents / 100m);
        return true;
    }

    private static BigInteger Cents(decimal value)
    {
        var (coefficient, scale) = Parts(value);
        return (coefficient * 100) / BigInteger.Pow(10, scale);
    }

    private static (BigInteger Coefficient, int Scale) Parts(decimal value)
    {
        var bits = decimal.GetBits(value);
        var coefficient = (BigInteger)(uint)bits[0]
            | ((BigInteger)(uint)bits[1] << 32)
            | ((BigInteger)(uint)bits[2] << 64);
        return (coefficient, (bits[3] >> 16) & 0xff);
    }

    private static bool ValidMoney(decimal value) => value is >= 0 and < MoneyLimit
        && decimal.Round(value, 2, MidpointRounding.AwayFromZero) == value;
}
