using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Niobium.Notification.Functions
{
    public class SubscribeFromGoogleAds(Func<SubscriptionDomain> domainFactory, ILogger<SubscribeFromGoogleAds> logger)
    {
        private static readonly Guid Tenant = Guid.Empty; //"www.edennoodleshamilton.co.nz";
        private const string Source = "Google";
        private const string Campaign = "OneDollarVoucher";
        private const string CampaignKey = "CA1CECEC-016D-42D0-B7C2-69AC3805A359";
        private const string FullNameColumnID = "FULL_NAME";
        private const string EmailColumnID = "EMAIL";

        private static readonly JsonSerializerOptions snakeCaseSerializationOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

        [Function(nameof(SubscribeFromGoogleAds))]
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest req,
            CancellationToken cancellationToken)
        {
            var request = await JsonMarshaller.UnmarshallAsync<GoogleAdsLeadForm>(req.Body, JsonMarshallingFormat.SnakeCase, cancellationToken);
            if (request == null)
            {
                return new BadRequestResult();
            }

            logger.LogInformation($"Received lead form from Google: {request.LeadID}");

            if (!request.GoogleKey.Equals(CampaignKey, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError($"Invalid key received form from Google: {JsonMarshaller.Marshall(request)}");
                return new BadRequestResult();
            }

            var fullName = request.UserColumnData.SingleOrDefault(d => d.ColumnID == FullNameColumnID);
            var email = request.UserColumnData.SingleOrDefault(d => d.ColumnID == EmailColumnID);
            if (fullName == null || email == null
                || String.IsNullOrWhiteSpace(fullName.StringValue) || String.IsNullOrWhiteSpace(email.StringValue))
            {
                logger.LogError($"Invalid lead received form from Google: {JsonMarshaller.Marshall(request)}");
                return new BadRequestResult();
            }

            var emailValue = email.StringValue.Trim().ToLowerInvariant();
            var nameValue = fullName.StringValue.Trim();
            var nameParts = nameValue.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string firstName;
            string? lastName = null;
            if (nameParts.Length > 1)
            {
                firstName = nameParts[0];
                lastName = String.Join(' ', nameParts.Skip(1));
            }
            else
            {
                firstName = nameParts[0];
            }

            var domain = domainFactory();
            await domain.SubscribeAsync(Tenant, Campaign, emailValue, firstName, lastName, Source, null, cancellationToken);
            logger.LogInformation($"Created subscription: {nameValue} <{emailValue}>");
            return new OkResult();
        }
    }
}
