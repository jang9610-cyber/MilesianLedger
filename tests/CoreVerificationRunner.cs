using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MabinogiBarter;

// Uses public offline suites and static bundled data. No real API key or user profile is loaded.
static class CoreVerificationRunner
{
    static readonly List<string> Reports = new List<string>();
    static bool failed;

    static void RunSuite(string name, Func<string> run)
    {
        try
        {
            string result = run();
            Reports.Add(result);
            Console.WriteLine(result);
        }
        catch (Exception error)
        {
            failed = true;
            string result = "FAIL " + name + ": " + error;
            Reports.Add(result);
            Console.Error.WriteLine(result);
        }
    }

    // Runners are copied beside an isolated application under artifacts/verification/<guid>/app.
    // Reject arbitrary output paths before creating profiles or writing reports.
    static string ValidateOutputRoot(string argument)
    {
        var app = new DirectoryInfo(Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory));
        var run = app.Parent;
        Guid runId;
        if (!String.Equals(app.Name, "app", StringComparison.OrdinalIgnoreCase) || run == null ||
            !Guid.TryParse(run.Name, out runId) || run.Parent == null ||
            !String.Equals(run.Parent.Name, "verification", StringComparison.OrdinalIgnoreCase) ||
            run.Parent.Parent == null || !String.Equals(run.Parent.Parent.Name, "artifacts", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Run this test from an isolated artifacts/verification/<guid>/app directory.");
        string root = Path.GetFullPath(argument).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string runPrefix = run.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string appRoot = app.FullName.TrimEnd(Path.DirectorySeparatorChar);
        if (!root.StartsWith(runPrefix, StringComparison.OrdinalIgnoreCase) ||
            root.Equals(appRoot, StringComparison.OrdinalIgnoreCase) ||
            root.StartsWith(appRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("QA output must be inside this isolated run, outside its app directory.");
        return root;
    }

    [STAThread] static int Main(string[] args)
    {
        string root = null;
        try
        {
            if (args.Length != 1) throw new ArgumentException("Expected one isolated QA output directory.");
            root = ValidateOutputRoot(args[0]);
            Directory.CreateDirectory(root);
            string data = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            var catalog = Catalog.Load(Path.Combine(data, "barter-data.json"));
            RunSuite("catalog", delegate { return Verification.Run(catalog); });
            RunSuite("procurement", delegate { return ProcurementVerification.Run(catalog); });
            RunSuite("readiness", delegate { return ProcurementReadinessVerification.Run(catalog); });
            RunSuite("shared planning", delegate { return ProcurementSharedPlanningVerification.Run(catalog); });
            RunSuite("NPC procurement", delegate { return NpcProcurementVerification.Run(catalog); });
            RunSuite("costing", delegate { return ProcurementCostingVerification.Run(catalog); });
            RunSuite("trade planning", delegate {
                return TradePlanningVerification.Run(catalog, TradePlanningData.Load(Path.Combine(data, "trade-planning.json"), catalog));
            });
            RunSuite("acquisition", delegate {
                return new AcquisitionCatalog(Path.Combine(data, "item-acquisition.json")).Verify(catalog);
            });
            RunSuite("auction", delegate { return AuctionVerification.Run(root); });
            RunSuite("proxy client", delegate { return AuctionProxyVerification.Run(root); });
            File.WriteAllText(Path.Combine(root, "report.txt"), String.Join(Environment.NewLine, Reports), new UTF8Encoding(false));
            return failed ? 1 : 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (root != null)
            {
                Directory.CreateDirectory(root);
                Reports.Add("FAIL runner: " + error);
                File.WriteAllText(Path.Combine(root, "report.txt"), String.Join(Environment.NewLine, Reports), new UTF8Encoding(false));
            }
            return 1;
        }
    }
}
