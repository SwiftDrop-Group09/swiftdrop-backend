namespace SwiftDrop.Identity.BrowserHarness;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            string? frontend = null;
            string? mobile = null;
            for (var index = 0; index < args.Length; index += 2)
            {
                if (index + 1 >= args.Length) throw new ArgumentException("An integration path is required.");
                if (args[index] == "--frontend-dist") frontend = Path.GetFullPath(args[index + 1]);
                else if (args[index] == "--mobile-project") mobile = Path.GetFullPath(args[index + 1]);
                else throw new ArgumentException("Unknown harness option.");
            }
            await BrowserHttpsIntegrationTests.RunAsync(frontend, mobile);
            return 0;
        }
        catch (Exception exception)
        {
            // Browser failures can contain request arguments. Do not print raw
            // exception details, credentials, browser traces or application logs.
            Console.Error.WriteLine($"FAIL: Identity HTTPS browser harness ({exception.GetType().Name}).");
            var networkCode = System.Text.RegularExpressions.Regex.Match(exception.Message, @"ERR_[A-Z_]+").Value;
            if (networkCode.Length > 0) Console.Error.WriteLine($"Browser network code: {networkCode}");
            Console.Error.WriteLine("Check the last named assertion and integration/IdentityBrowserHarness setup instructions.");
            return 1;
        }
    }
}
