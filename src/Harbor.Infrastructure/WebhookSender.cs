using System.Net.Http.Headers;
using System.Text;
using Harbor.Domain;
using Harbor.Domain.Entities;

namespace Harbor.Infrastructure;

/// <summary>Outcome of a single delivery attempt.</summary>
public record WebhookSendResult(bool Success, int? StatusCode, string? Error)
{
    public static WebhookSendResult Ok(int statusCode) => new(true, statusCode, null);

    public static WebhookSendResult Rejected(int statusCode) =>
        new(false, statusCode, $"Endpoint responded {statusCode}.");

    public static WebhookSendResult Unreachable(string error) => new(false, null, error);
}

/// <summary>
/// Sends one webhook attempt. Abstracted so the dispatcher's retry and
/// bookkeeping logic can be tested without a live endpoint.
/// </summary>
public interface IWebhookSender
{
    Task<WebhookSendResult> SendAsync(
        WebhookSubscription subscription, WebhookDelivery delivery,
        DateTimeOffset now, CancellationToken cancellationToken = default);
}

/// <summary>Posts the signed payload over HTTP.</summary>
public class HttpWebhookSender(HttpClient client) : IWebhookSender
{
    public async Task<WebhookSendResult> SendAsync(
        WebhookSubscription subscription, WebhookDelivery delivery,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, subscription.Url)
            {
                // The stored payload is sent verbatim: the signature covers
                // these exact bytes, so re-serializing could invalidate it.
                Content = new StringContent(delivery.Payload, Encoding.UTF8),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.TryAddWithoutValidation(
                WebhookSigner.SignatureHeader,
                WebhookSigner.SignatureHeaderValue(subscription.Secret, now, delivery.Payload));
            request.Headers.TryAddWithoutValidation(
                WebhookSigner.EventHeader, delivery.EventType.WireName());
            request.Headers.TryAddWithoutValidation(
                WebhookSigner.DeliveryHeader, delivery.Id.ToString());

            using var response = await client.SendAsync(request, cancellationToken);
            var status = (int)response.StatusCode;

            return response.IsSuccessStatusCode
                ? WebhookSendResult.Ok(status)
                : WebhookSendResult.Rejected(status);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return WebhookSendResult.Unreachable(ex.Message);
        }
    }
}
