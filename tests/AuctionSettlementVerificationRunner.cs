using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MabinogiBarter;

public static class AuctionSettlementVerificationRunner
{
    static readonly List<string> reports = new List<string>();
    static int assertions;
    static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    static void Pass(string message) { reports.Add("PASS " + message); Console.WriteLine(reports[reports.Count - 1]); }
    static AuctionSettlementScenario Row(AuctionSettlementReport report, int percent) { return report.Scenarios.Single(r => r.DiscountPercent == percent); }
    static AuctionSettlementInput ScreenshotInput(bool premium)
    {
        return new AuctionSettlementInput { GrossAmount = 100000000m, People = 4, Premium = premium,
            CouponPrices = new Dictionary<int, decimal?> { { 10, 220000m }, { 20, 740000m }, { 30, 1680000m }, { 50, 12940000m }, { 100, 19550000m } } };
    }
    static void Reject(Action action, string message)
    {
        try { action(); } catch (ArgumentException) { assertions++; return; }
        throw new Exception("Accepted invalid input: " + message);
    }
    static void VerifyScreenshotValues()
    {
        var regular = AuctionSettlement.Calculate(ScreenshotInput(false));
        var premium = AuctionSettlement.Calculate(ScreenshotInput(true));
        Check(regular.BaseFeeRate == 0.05m && premium.BaseFeeRate == 0.04m, "Base fee rates");
        Check(regular.Scenarios.Select(r => r.DiscountPercent).SequenceEqual(new[] { 0, 10, 20, 30, 50, 100 }), "Exactly six ordered coupon scenarios");
        decimal[] regularFees = { 5000000m, 4500000m, 4000000m, 3500000m, 2500000m, 0m };
        decimal[] regularNets = { 95000000m, 95280000m, 95260000m, 94820000m, 84560000m, 80450000m };
        decimal[] premiumFees = { 4000000m, 3600000m, 3200000m, 2800000m, 2000000m, 0m };
        decimal[] premiumNets = { 96000000m, 96180000m, 96060000m, 95520000m, 85060000m, 80450000m };
        decimal[] regularShares = { 23750000m, 23820000m, 23815000m, 23705000m, 21140000m, 20112500m };
        decimal[] premiumShares = { 24000000m, 24045000m, 24015000m, 23880000m, 21265000m, 20112500m };
        for (int i = 0; i < 6; i++) {
            var r = regular.Scenarios[i]; var p = premium.Scenarios[i];
            Check(r.Fee == regularFees[i] && r.NetAmount == regularNets[i] && r.PerPerson == regularShares[i], "Regular screenshot row " + i);
            Check(p.Fee == premiumFees[i] && p.NetAmount == premiumNets[i] && p.PerPerson == premiumShares[i], "Premium screenshot row " + i);
            Check(r.Remainder == 0m && p.Remainder == 0m && !r.IsLoss && !p.IsLoss, "Screenshot distribution status " + i);
        }
        Check(regular.BestScenario.DiscountPercent == 10 && premium.BestScenario.DiscountPercent == 10, "Purchase cost must influence coupon recommendation");
        Check(Row(regular, 100).CouponPrice == 19550000m && Row(regular, 100).Fee == 0m, "Full discount removes the fee, not coupon expense");
        Pass("100,000,000 G sale: six exact standard/premium fee, coupon-cost, net and four-person screenshot rows; 10% coupon wins both");
    }
    static void VerifyPrecisionAndSplits()
    {
        var input = new AuctionSettlementInput { GrossAmount = 101m, People = 4 };
        var row = Row(AuctionSettlement.Calculate(input), 0);
        Check(row.Fee == 5.05m && row.NetAmount == 95.95m && row.PerPerson == 23m && row.Remainder == 3.95m, "Raw fractional fee and four-person remainder");
        input.GrossAmount = 0.01m; input.CouponPrices[10] = 0m;
        row = Row(AuctionSettlement.Calculate(input), 10);
        Check(row.Fee == 0.00045m && row.NetAmount == 0.00955m && row.PerPerson == 0m && row.Remainder == 0.00955m, "Sub-gold fee precision must not be rounded or discarded");
        input.GrossAmount = 1234567.89m; input.ExtraCost = 12.34m; input.CouponPrices[10] = 4.56m;
        foreach (int people in new[] { 1, 2, 4, 7, 1000 }) {
            input.People = people;
            foreach (var scenario in AuctionSettlement.Calculate(input).Scenarios.Where(s => s.IsKnown)) {
                Check(scenario.PerPerson == Decimal.Floor(scenario.PerPerson.Value), "Shares are whole gold");
                Check(scenario.PerPerson * people + scenario.Remainder == scenario.NetAmount, "Distribution conserves the precise net amount");
                Check(scenario.Remainder >= 0m && scenario.Remainder < people, "Nonnegative remainder is less than the party size");
                Check(input.GrossAmount == scenario.Fee + input.ExtraCost + scenario.CouponPrice + scenario.NetAmount, "Net subtracts each cost once from the sale total");
            }
        }
        Pass("fractional fees stay precise; four-person residual gold and 1/2/4/7/1,000-person distributions conserve all proceeds and costs");
    }
    static void VerifyUnknownFreeAndLoss()
    {
        var input = new AuctionSettlementInput { GrossAmount = 100m, People = 4, ExtraCost = 1m };
        input.CouponPrices[10] = null;
        input.CouponPrices[20] = 0m;
        var result = AuctionSettlement.Calculate(input);
        foreach (int discount in new[] { 10, 30, 50, 100 }) {
            var row = Row(result, discount);
            Check(!row.IsKnown && !row.CouponPrice.HasValue && !row.NetAmount.HasValue && !row.PerPerson.HasValue && !row.Remainder.HasValue && !row.IsLoss,
                "Unknown coupon price must not imply free, a loss, or distributable proceeds: " + discount);
        }
        Check(Row(result, 10).Fee == 4.5m && Row(result, 100).Fee == 0m, "Unknown coupon prices still show estimated fees");
        Check(Row(result, 20).NetAmount == 95m && result.BestScenario.DiscountPercent == 20, "Explicit zero-priced coupon participates as free");
        input.CouponPrices = null;
        Check(AuctionSettlement.Calculate(input).BestScenario.DiscountPercent == 0, "Missing coupon dictionary recommends the known no-coupon case");
        input.CouponPrices = new Dictionary<int, decimal?> { { 100, 5m } }; input.ExtraCost = 0m;
        result = AuctionSettlement.Calculate(input);
        Check(Row(result, 100).NetAmount == Row(result, 0).NetAmount && result.BestScenario.DiscountPercent == 0, "Equal proceeds prefer no coupon");
        input.CouponPrices[100] = 200m;
        var loss = Row(AuctionSettlement.Calculate(input), 100);
        Check(loss.NetAmount == -100m && loss.IsKnown && loss.IsLoss && !loss.PerPerson.HasValue && !loss.Remainder.HasValue, "A coupon purchase loss is not floored into negative payouts");
        input.ExtraCost = 1000m;
        result = AuctionSettlement.Calculate(input);
        Check(result.BestScenario.DiscountPercent == 0 && result.BestScenario.IsLoss && result.BestScenario.NetAmount == -905m, "All-loss comparison still identifies the least loss explicitly");
        input.GrossAmount = 0m; input.ExtraCost = 0m; input.CouponPrices[100] = 0m;
        result = AuctionSettlement.Calculate(input);
        Check(result.BestScenario.DiscountPercent == 0 && Row(result, 0).PerPerson == 0m && Row(result, 0).Remainder == 0m && !Row(result, 0).IsLoss, "Zero proceeds are a known zero distribution");
        Pass("unknown coupon costs exclude net/recommendations; explicit free coupons, no-coupon ties, zero proceeds and purchase/extra-cost losses remain distinct");
    }
    static void VerifyBounds()
    {
        Reject(delegate { AuctionSettlement.Calculate(null); }, "null input");
        foreach (decimal amount in new[] { -0.01m, AuctionSettlement.MaxMoney + 1m, Decimal.MaxValue, Decimal.MinValue }) {
            Reject(delegate { AuctionSettlement.Calculate(new AuctionSettlementInput { GrossAmount = amount }); }, "gross range");
            Reject(delegate { AuctionSettlement.Calculate(new AuctionSettlementInput { ExtraCost = amount }); }, "extra cost range");
            Reject(delegate { AuctionSettlement.Calculate(new AuctionSettlementInput { CouponPrices = new Dictionary<int, decimal?> { { 10, amount } } }); }, "coupon price range");
        }
        foreach (int count in new[] { -1, 0, 1001, Int32.MinValue, Int32.MaxValue })
            Reject(delegate { AuctionSettlement.Calculate(new AuctionSettlementInput { People = count }); }, "party size");
        foreach (int discount in new[] { -10, 0, 1, 40, 99, 101 })
            Reject(delegate { AuctionSettlement.Calculate(new AuctionSettlementInput { CouponPrices = new Dictionary<int, decimal?> { { discount, 0m } } }); }, "unsupported coupon tier");
        var input = new AuctionSettlementInput { GrossAmount = AuctionSettlement.MaxMoney, ExtraCost = AuctionSettlement.MaxMoney, People = AuctionSettlement.MaxPeople,
            CouponPrices = new Dictionary<int, decimal?> { { 10, AuctionSettlement.MaxMoney }, { 20, 0m }, { 100, 0m } } };
        var result = AuctionSettlement.Calculate(input);
        Check(Row(result, 10).NetAmount == -1045000000000000m && Row(result, 10).IsLoss, "Largest allowed inputs remain within decimal bounds");
        Check(Row(result, 100).NetAmount == 0m && result.BestScenario.DiscountPercent == 100, "Maximum-sized fee-free cost recovery");
        int[] externalTiers = AuctionSettlement.DiscountPercents; externalTiers[0] = 999;
        Check(AuctionSettlement.DiscountPercents[0] == 0, "External tier array mutation cannot change the calculator");
        input.GrossAmount = 1m; input.CouponPrices[10] = null;
        Check(Row(result, 10).NetAmount == -1045000000000000m, "Later input edits cannot change an existing result");
        Pass("money and party bounds reject malformed numeric domains before arithmetic; maximum valid amounts cannot overflow; tier/result snapshots stay isolated");
    }
    public static int Main(string[] args)
    {
        try {
            VerifyScreenshotValues(); VerifyPrecisionAndSplits(); VerifyUnknownFreeAndLoss(); VerifyBounds();
            string summary = "PASS " + assertions + " settlement assertions; pure C# model, no WPF, network, market cache, or production writes.";
            Console.WriteLine(summary); reports.Add(summary);
            if (args.Length > 0) { Directory.CreateDirectory(args[0]); File.WriteAllLines(Path.Combine(args[0], "report.txt"), reports, Encoding.UTF8); }
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
