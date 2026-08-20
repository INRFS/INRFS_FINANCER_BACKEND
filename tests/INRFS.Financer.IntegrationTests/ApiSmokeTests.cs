using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace INRFS.Financer.IntegrationTests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration(
            (_, c) =>
                c.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        {
                            "ConnectionStrings:DefaultConnection",
                            "Data Source=integration-tests.db"
                        },
                        { "Database:Initialize", "false" },
                        { "Jwt:Key", "integration-test-signing-key-at-least-32-characters" },
                        { "DataProtection:Key", "integration-test-data-protection-key" },
                    }
                )
        );
        builder.ConfigureServices(s =>
            s.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider())
        );
    }
}

public sealed class ApiSmokeTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private HttpClient Client() =>
        factory.CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") }
        );

    [Fact]
    public async Task Live_health_is_available()
    {
        var response = await Client().GetAsync("/health/live");
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Swagger_document_is_available_in_development()
    {
        var response = await Client().GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("INRFS Financer API", json);
        Assert.Contains("/api/v1/loan-applications", json);
    }

    [Fact]
    public async Task Protected_endpoint_requires_authentication()
    {
        var response = await Client().GetAsync("/api/v1/customers");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
