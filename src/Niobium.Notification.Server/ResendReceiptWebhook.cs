using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Options;
using Niobium.Messaging;
using Niobium.Notification.Delivery;
using Niobium.Notification.Server.Delivery;

namespace Niobium.Notification.Server;

public sealed class ResendReceiptWebhook(IOptions<ResendWebhookOptions> options,
    IMessagingBroker<CaptureNotificationReceiptCommand> queue, TimeProvider clock)
{
    [Function(nameof(ResendReceiptWebhook))]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "webhooks/resend")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        ResendWebhookSignature verifier;
        try { verifier = new(options.Value.ReadSigningSecrets(), clock); }
        catch (InvalidOperationException) { return new StatusCodeResult(StatusCodes.Status503ServiceUnavailable); }
        if (request.ContentLength > ResendWebhookSignature.MaximumBodyBytes)
            return new StatusCodeResult(StatusCodes.Status413PayloadTooLarge);
        foreach (string header in new[] { "svix-id", "svix-timestamp", "svix-signature" })
        {
            if (request.Headers[header].Count != 1) return new UnauthorizedResult();
        }
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using MemoryStream buffer = new();
        byte[] chunk = new byte[8192];
        try
        {
            while (true)
            {
                int read = await request.Body.ReadAsync(chunk, deadline.Token);
                if (read == 0) break;
                if (buffer.Length + read > ResendWebhookSignature.MaximumBodyBytes)
                    return new StatusCodeResult(StatusCodes.Status413PayloadTooLarge);
                await buffer.WriteAsync(chunk.AsMemory(0, read), deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new StatusCodeResult(StatusCodes.Status408RequestTimeout);
        }
        byte[] body = buffer.ToArray();
        string id = request.Headers["svix-id"].ToString();
        bool authentic = verifier.Verify(body, id, request.Headers["svix-timestamp"], request.Headers["svix-signature"]);
        if (!authentic) return new UnauthorizedResult();
        CaptureNotificationReceiptCommand? receipt;
        try { receipt = ResendWebhookReader.Read(body, id, clock.GetUtcNow()); }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            return new BadRequestResult();
        }
        if (receipt == null) return new NoContentResult();
        // A successful HTTP acknowledgement follows broker acceptance. A failed
        // or unknown enqueue propagates so the provider can retry this same event.
        await queue.EnqueueAsync(new MessagingEntry<CaptureNotificationReceiptCommand> { ID = receipt.ID, Value = receipt }, cancellationToken);
        return new AcceptedResult();
    }
}
