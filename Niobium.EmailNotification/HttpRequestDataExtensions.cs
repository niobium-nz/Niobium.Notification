using System.ComponentModel.DataAnnotations;
using System.Net;
using Microsoft.Azure.Functions.Worker.Http;

namespace Niobium.EmailNotification
{
    internal static class HttpRequestDataExtensions
    {
        public static async Task<HttpResponseData?> ValidateAsync(this HttpRequestData request, object obj, CancellationToken cancellationToken)
        {
            var validationResults = new List<ValidationResult>();
            var validates = Validator.TryValidateObject(obj, new ValidationContext(obj), validationResults, true);
            if (!validates)
            {
                var response = request.CreateResponse();
                await response.WriteAsJsonAsync(validationResults, cancellationToken);
                response.StatusCode = HttpStatusCode.BadRequest;
                return response;
            }

            return null;
        }
    }
}
