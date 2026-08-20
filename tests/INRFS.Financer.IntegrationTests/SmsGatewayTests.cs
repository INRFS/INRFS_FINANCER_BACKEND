using System.Net;
using INRFS.Financer.Infrastructure;
using Microsoft.Extensions.Options;
using Xunit;

namespace INRFS.Financer.IntegrationTests;

public sealed class SmsGatewayTests
{
    [Fact]
    public async Task Webhook_gateway_sends_configured_request_and_returns_provider_reference()
    {
        var handler = new RecordingHandler();
        var gateway = new WebhookSmsGateway(new HttpClient(handler), Options.Create(new SmsGatewayOptions
        {
            Provider = "Webhook",
            WebhookUrl = "https://sms.example.invalid/send",
            ApiKey = "test-key",
        }));

        var reference = await gateway.SendAsync("9999999999", "Payment due", "PaymentReminder", default);

        Assert.Equal("provider-123", reference);
        Assert.Equal("test-key", handler.Request?.Headers.GetValues("X-API-Key").Single());
        Assert.Contains("PaymentReminder", handler.Body);
        Assert.Contains("9999999999", handler.Body);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"providerReference\":\"provider-123\"}", System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
