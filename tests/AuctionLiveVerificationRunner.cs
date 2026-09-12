using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using MabinogiBarter;

// Explicit live verification only. This runner is excluded from desktop ZIPs and CI.
static class AuctionLiveVerificationRunner
{
    static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            var app = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            Guid id;
            if (app.Name != "app" || app.Parent == null || !Guid.TryParse(app.Parent.Name, out id) ||
                app.Parent.Parent == null || app.Parent.Parent.Name != "live-auction")
                throw new InvalidOperationException("Use scripts/test-live-auction.ps1 to create an isolated run.");
            string data = Path.Combine(app.FullName, "data");
            string profile = Path.Combine(app.Parent.FullName, "profile");
            Directory.CreateDirectory(profile);
            File.Copy(Path.Combine(data, "auction-proxy.json"), Path.Combine(profile, "auction-proxy.json"));
            var catalog = Catalog.Load(Path.Combine(data, "barter-data.json"));
            var names = new ProcurementPlanner(catalog).GetAllQuoteNames();
            var settings = new AuctionSettings();
            var json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
            using (var service = new AuctionService(profile, settings))
            {
                if (!service.IsConfigured) throw new InvalidOperationException(service.ConfigurationMessage);
                var result = service.RefreshAsync(names, null, CancellationToken.None).GetAwaiter().GetResult();
                var quotes = names.Select(name => service.GetQuote(name)).ToArray();
                var report = new {
                    CompletedUtc = DateTime.UtcNow.ToString("o"), Result = result, Quotes = quotes,
                    ValidItems = quotes.Count(q => q != null && q.Complete && (q.Status == "ok" || q.Status == "empty")),
                    EmptyItems = quotes.Count(q => q != null && q.Status == "empty")
                };
                string reportPath = Path.Combine(app.Parent.FullName, "report.json");
                File.WriteAllText(reportPath, json.Serialize(report), new UTF8Encoding(false));
                Console.WriteLine(json.Serialize(result));
                Console.WriteLine("Verified items: " + report.ValidItems + "/" + names.Length + "; empty: " + report.EmptyItems);
                Console.WriteLine("Report: " + reportPath);
                if (result.Cancelled || result.FailedMaterials != 0 || report.ValidItems != names.Length ||
                    !String.IsNullOrEmpty(result.StoppedReason) || !String.IsNullOrEmpty(service.Notice)) return 1;
                using (var restored = new AuctionService(profile, settings))
                    foreach (string name in names)
                    {
                        var before = service.GetQuote(name);
                        var after = restored.GetQuote(name);
                        // JavaScriptSerializer stores DateTime at millisecond precision.
                        // Compare the complete persisted representation, including every quote field.
                        if (after == null || json.Serialize(after) != json.Serialize(before))
                            throw new InvalidDataException("Restored quote differs: " + name);
                    }
                Console.WriteLine("PASS all live auction items and offline cache restoration.");
                return 0;
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
            return 3;
        }
    }
}
