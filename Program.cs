using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using OrderProcessing.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Simulated dependencies; replace with real clients in production.
        // Tune the simulation with PaymentGateway__TransientFailureRate and PaymentGateway__DeclineAbove.
        services.AddSingleton(sp =>
        {
            var options = new SimulatedPaymentGatewayOptions();
            sp.GetRequiredService<IConfiguration>().GetSection("PaymentGateway").Bind(options);
            return options;
        });
        services.AddSingleton<IPaymentGateway, SimulatedPaymentGateway>();
        services.AddSingleton<IInventoryService>(_ => new InMemoryInventoryService());
    })
    .Build();

host.Run();
