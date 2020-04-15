using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
#pragma warning disable 1998

namespace JekyllBlogCommentsAzureV2
{
    [UsedImplicitly]
    public static class Ping
    {
        [FunctionName("Ping")]
        public static async Task<IActionResult> RunAsync(
                [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = null)]
                HttpRequest request,
                ILogger log)
        {
            log.LogInformation("Ping request received.");

            return new OkObjectResult("Application is running.");
        }
    }
}
