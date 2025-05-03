using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Niobium.EmailNotification
{
    public class SubscribeFromGoogleAdsFunction(ILogger<SubscribeFromGoogleAdsFunction> logger)
    {
        [Function(nameof(SubscribeFromGoogleAds))]
        public IActionResult SubscribeFromGoogleAds(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest req)
        {
            // log all query parameters
            var queryParameters = req.Query;
            foreach (var key in queryParameters.Keys)
            {
                logger.LogInformation($"QUERY {key}: {queryParameters[key]}");
            }

            // log all headers
            var headers = req.Headers;
            foreach (var key in headers.Keys)
            {
                logger.LogInformation($"HEADER {key}: {headers[key]}");
            }

            // log JSON request body
            req.Body.Position = 0;
            using var reader = new StreamReader(req.Body);
            var body = reader.ReadToEnd();
            logger.LogInformation($"Request body: {body}");

            return new OkResult();
        }
    }
}
