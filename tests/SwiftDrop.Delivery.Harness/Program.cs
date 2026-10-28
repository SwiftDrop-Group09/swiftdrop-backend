using System.Net;
using System.Net.Http.Json;
using System.Text;

using SwiftDrop.Delivery.Tests.ClientHarness;

namespace SwiftDrop.Delivery.Harness;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            if (args.SequenceEqual(["--verify-private-storage"]))
                return await PrivateStorageAcceptance.RunAsync(Console.Out, shutdown.Token);
            var courierWorkflow = args.Contains("--courier-workflow", StringComparer.Ordinal);
            var (actor, port, smoke) = Parse(args.Where(x => x != "--courier-workflow").ToArray());
            await using var harness = await BookingClientHarness.StartAsync(actor, port, courierWorkflow);
            Console.WriteLine(harness.Address);
            if (smoke)
            {
                await SmokeAsync(harness);
                return 0;
            }

            while (!shutdown.IsCancellationRequested)
            {
                // Console input can block synchronously on Windows. Keep the
                // read on a background thread so Ctrl+C can reach disposal.
                var command = await Task.Run(Console.ReadLine).WaitAsync(shutdown.Token);
                if (command is null)
                {
                    await Task.Delay(Timeout.Infinite, shutdown.Token);
                }
                else if (command.Equals("stop", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                else if (command.Equals("verify-courier", StringComparison.OrdinalIgnoreCase))
                {
                    var verification = await harness.VerifyCompletedCourierWorkflowAsync();
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(verification));
                }
            }

            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 0;
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine("Usage: --port 5088 --actor customer-a|customer-b|courier [--smoke] [--courier-workflow], or --verify-private-storage.");
            return 2;
        }
        catch (Exception exception)
        {
            // Never dump connection strings, request bodies or private settings.
            Console.Error.WriteLine($"Client test harness failed ({exception.GetType().Name}).");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private static (BookingHarnessActor Actor, int Port, bool Smoke) Parse(string[] args)
    {
        var actor = BookingHarnessActor.CustomerA;
        var port = 5088;
        var smoke = false;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--smoke") smoke = true;
            else if (args[index] == "--port")
            {
                if (++index >= args.Length || !int.TryParse(args[index], out port) || port is < 0 or > 65535)
                    throw new ArgumentException("Invalid test port.");
            }
            else if (args[index] == "--actor")
            {
                if (++index >= args.Length) throw new ArgumentException("Missing test actor.");
                actor = args[index] switch
                {
                    "customer-a" => BookingHarnessActor.CustomerA,
                    "customer-b" => BookingHarnessActor.CustomerB,
                    "courier" => BookingHarnessActor.Courier,
                    _ => throw new ArgumentException("Unknown test actor.")
                };
            }
            else throw new ArgumentException("Unknown test harness argument.");
        }

        return (actor, port, smoke);
    }

    private static async Task SmokeAsync(BookingClientHarness harness)
    {
        using var client = harness.Host.CreateClient();
        var context = await client.GetFromJsonAsync<BookingHarnessContext>("/__test/booking-context")
            ?? throw new InvalidOperationException("No fixture context.");
        var input = new
        {
            pickupAddress = new { street = "Fictional pickup", city = "Test city", postalCode = "00000", zoneId = context.Zones[0].Id },
            deliveryAddress = new { street = "Fictional delivery", city = "Test city", postalCode = "00000", zoneId = context.Zones[1].Id },
            packageDetails = new { weightKg = 2.5m, size = "Small" },
            recipient = new { name = "Fictional Recipient", phoneNumber = "0000000000" }
        };
        using var quote = await client.PostAsJsonAsync("/api/v1/quotes", input);
        if (!context.Roles.Contains("Customer"))
        {
            if (quote.StatusCode != HttpStatusCode.Forbidden) throw new InvalidOperationException("Unexpected role result.");
            return;
        }

        if (quote.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException("Quote failed.");
        using var document = System.Text.Json.JsonDocument.Parse(await quote.Content.ReadAsStringAsync());
        var confirmation = "{\"quoteId\":\"" + document.RootElement.GetProperty("quoteId").GetGuid().ToString("D")
            + "\",\"acceptTotal\":" + document.RootElement.GetProperty("total").GetRawText() + "}";
        var key = Guid.NewGuid().ToString("D");
        using var first = await ConfirmAsync(client, confirmation, key);
        using var retry = await ConfirmAsync(client, confirmation, key);
        if (first.StatusCode != HttpStatusCode.Created || retry.StatusCode != HttpStatusCode.Created
            || await first.Content.ReadAsStringAsync() != await retry.Content.ReadAsStringAsync()
            || first.Headers.Location != retry.Headers.Location)
        {
            throw new InvalidOperationException("Booking replay failed.");
        }
    }

    private static async Task<HttpResponseMessage> ConfirmAsync(HttpClient client, string body, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/parcels")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }
}
