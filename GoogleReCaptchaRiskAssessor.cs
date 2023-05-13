using Google.Api.Gax.ResourceNames;
using Google.Cloud.RecaptchaEnterprise.V1;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Niobium.EmailNotification
{
    internal class GoogleReCaptchaRiskAssessor : IVisitorRiskAssessor
    {
        private const string GOOGLE_CLOUD_PROJECT_ID = "GOOGLE_CLOUD_PROJECT_ID";
        private const string GOOGLE_RECAPTCHA_SITE_KEY = "GOOGLE_RECAPTCHA_SITE_KEY";
        private const string GOOGLE_RECAPTCHA_PASS_THRESHOLD = "GOOGLE_RECAPTCHA_PASS_THRESHOLD";
        private readonly ILogger logger;
        private readonly RecaptchaEnterpriseServiceClient client;
        private readonly IConfiguration configuration;

        public GoogleReCaptchaRiskAssessor(RecaptchaEnterpriseServiceClient client, IConfiguration configuration, ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger<GoogleReCaptchaRiskAssessor>();
            this.client = client;
            this.configuration = configuration;
        }

        public async Task<bool> AssessAsync(string token, string action, CancellationToken cancellationToken)
        {
            var projectID = this.configuration[GOOGLE_CLOUD_PROJECT_ID];
            var recaptchaSiteKey = this.configuration[GOOGLE_RECAPTCHA_SITE_KEY];

            var projectName = new ProjectName(projectID);

            var createAssessmentRequest = new CreateAssessmentRequest
            {
                Assessment = new Assessment
                {
                    Event = new Event
                    {
                        SiteKey = recaptchaSiteKey,
                        Token = token,
                        ExpectedAction = action
                    },
                },
                ParentAsProjectName = projectName
            };

            var response = await this.client.CreateAssessmentAsync(createAssessmentRequest, cancellationToken);

            if (!response.TokenProperties.Valid)
            {
                this.logger.LogError($"The CreateAssessment call failed because the token was: {response.TokenProperties.InvalidReason}");
                return false;
            }

            // Check if the expected action was executed.
            if (response.TokenProperties.Action != action)
            {
                this.logger.LogError($"The action attribute in the reCAPTCHA tag does not match the action you are expecting to score as the action attribute in reCAPTCHA tag is: {response.TokenProperties.Action}. ");
                return false;
            }

            var score = (decimal)response.RiskAnalysis.Score;
            return score >= Decimal.Parse(this.configuration[GOOGLE_RECAPTCHA_PASS_THRESHOLD]);
        }
    }
}
