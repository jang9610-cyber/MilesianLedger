using System;
using System.Collections.Generic;

namespace MabinogiBarter
{
    public sealed class AuctionSettlementInput
    {
        // One auction sale or lot total, not a unit price multiplied by a quantity.
        public decimal GrossAmount, ExtraCost;
        public bool Premium;
        public int People = 1;
        // Missing/null means unconfirmed. An explicit zero means no coupon expense.
        public Dictionary<int, decimal?> CouponPrices = new Dictionary<int, decimal?>();
    }

    public sealed class AuctionSettlementScenario
    {
        public int DiscountPercent;
        public decimal EffectiveFeeRate, Fee;
        public decimal? CouponPrice, NetAmount, PerPerson, Remainder;
        public bool IsLoss;
        public bool IsKnown { get { return NetAmount.HasValue; } }
    }

    public sealed class AuctionSettlementReport
    {
        public decimal BaseFeeRate;
        public List<AuctionSettlementScenario> Scenarios = new List<AuctionSettlementScenario>();
        public AuctionSettlementScenario BestScenario;
    }

    // Pure estimates: the service's rounding policy for fractional gold is not assumed.
    // Only a nonnegative distributable amount is split into whole-gold shares.
    public static class AuctionSettlement
    {
        public const decimal MaxMoney = 1000000000000000m;
        public const int MaxPeople = 1000;
        public static int[] DiscountPercents { get { return new[] { 0, 10, 20, 30, 50, 100 }; } }

        public static AuctionSettlementReport Calculate(AuctionSettlementInput input)
        {
            if (input == null) throw new ArgumentNullException("input");
            ValidateMoney(input.GrossAmount, "GrossAmount");
            ValidateMoney(input.ExtraCost, "ExtraCost");
            if (input.People < 1 || input.People > MaxPeople) throw new ArgumentOutOfRangeException("People");
            if (input.CouponPrices != null) foreach (var pair in input.CouponPrices) {
                if (pair.Key != 10 && pair.Key != 20 && pair.Key != 30 && pair.Key != 50 && pair.Key != 100)
                    throw new ArgumentException("Coupon discount must be 10, 20, 30, 50, or 100 percent.", "CouponPrices");
                if (pair.Value.HasValue) ValidateMoney(pair.Value.Value, "CouponPrices");
            }

            var report = new AuctionSettlementReport { BaseFeeRate = input.Premium ? 0.04m : 0.05m };
            foreach (int discount in DiscountPercents) {
                decimal? couponPrice = discount == 0 ? (decimal?)0m : null;
                if (discount != 0 && input.CouponPrices != null) input.CouponPrices.TryGetValue(discount, out couponPrice);
                var scenario = new AuctionSettlementScenario { DiscountPercent = discount, CouponPrice = couponPrice,
                    EffectiveFeeRate = report.BaseFeeRate * (1m - discount / 100m) };
                checked {
                    scenario.Fee = input.GrossAmount * scenario.EffectiveFeeRate;
                    if (couponPrice.HasValue) {
                        decimal net = input.GrossAmount - scenario.Fee - input.ExtraCost - couponPrice.Value;
                        scenario.NetAmount = net;
                        scenario.IsLoss = net < 0m;
                        if (!scenario.IsLoss) {
                            scenario.PerPerson = Decimal.Floor(net / input.People);
                            scenario.Remainder = net - scenario.PerPerson.Value * input.People;
                        }
                    }
                }
                report.Scenarios.Add(scenario);
                // The no-coupon scenario is first, so equal net amounts keep it preferred.
                if (scenario.IsKnown && (report.BestScenario == null || scenario.NetAmount.Value > report.BestScenario.NetAmount.Value))
                    report.BestScenario = scenario;
            }
            return report;
        }

        static void ValidateMoney(decimal amount, string name)
        {
            if (amount < 0m || amount > MaxMoney) throw new ArgumentOutOfRangeException(name);
        }
    }
}
