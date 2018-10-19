using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace JekyllBlogCommentsAzureV2
{
    public static class Ping
    {
        [FunctionName("Ping")]
        public static IActionResult Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = null)]HttpRequest request, ILogger log)
        {
            log.LogInformation("Ping request received.");

            return new OkObjectResult("Application is running.");
        }
    }
}
