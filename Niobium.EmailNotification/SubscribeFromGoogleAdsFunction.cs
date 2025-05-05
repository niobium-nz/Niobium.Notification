using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Niobium.EmailNotification
{
    public class SubscribeFromGoogleAdsFunction(ILogger<SubscribeFromGoogleAdsFunction> logger)
    {
        private const string Source = "Google";
        private const string Tenant = "www.edennoodleshamilton.co.nz";
        private const string Campaign = "OneDollarVoucher";
        private const string CampaignKey = "CA1CECEC-016D-42D0-B7C2-69AC3805A359";
        private const string FullNameColumnID = "FULL_NAME";
        private const string EmailColumnID = "EMAIL";

        private static readonly JsonSerializerOptions serializationOptions = new(JsonSerializerDefaults.Web);

        [Function(nameof(SubscribeFromGoogleAds))]
        public async Task<IActionResult> SubscribeFromGoogleAds(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest req,
            Func<SubscriptionDomain> domainFactory,
            CancellationToken cancellationToken)
        {
            var request = await JsonSerializer.DeserializeAsync<GoogleAdsLeadForm>(req.Body, options: serializationOptions, cancellationToken: cancellationToken);
            if (request == null)
            {
                return new BadRequestResult();
            }

            logger.LogInformation($"Received lead form from Google: {request.LeadID}");

            if (!request.GoogleKey.Equals(CampaignKey, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError($"Invalid key received form from Google: {JsonSerializer.Serialize(request)}");
                return new BadRequestResult();
            }

            var fullName = request.UserColumnData.SingleOrDefault(d => d.ColumnID == FullNameColumnID);
            var email = request.UserColumnData.SingleOrDefault(d => d.ColumnID == EmailColumnID);
            if (fullName == null || email == null
                || string.IsNullOrWhiteSpace(fullName.StringValue) || string.IsNullOrWhiteSpace(email.StringValue))
            {
                logger.LogError($"Invalid lead received form from Google: {JsonSerializer.Serialize(request)}");
                return new BadRequestResult();
            }

            var emailValue = email.StringValue.Trim().ToLowerInvariant();
            var nameValue = fullName.StringValue.Trim();

            var domain = domainFactory();
            await domain.SubscribeAsync(Tenant, Campaign, emailValue, nameValue, null, Source, cancellationToken);
            logger.LogInformation($"Created subscription: {nameValue} <{emailValue}>");
            return new OkResult();
        }
    }
}
