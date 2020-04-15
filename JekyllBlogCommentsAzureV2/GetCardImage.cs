using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Unicode;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;
using Octokit;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.Primitives;
using SixLabors.Shapes;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace JekyllBlogCommentsAzureV2
{
    [UsedImplicitly]
    public static class GetCardImage
    {
        [FunctionName("GetCardImage")]
        public static async Task<IActionResult> RunAsync(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "GetCardImage/{*postPath}")]
            HttpRequest request,
            
            [Blob("twitter-cards/{postPath}.png", FileAccess.ReadWrite, Connection = "AzureWebJobsStorage")]
            ICloudBlob imageBlob,
            
            string postPath,

            ILogger log,
            
            ExecutionContext context)
        {
            // Empty?
            if (string.IsNullOrEmpty(postPath) 
                || !postPath.StartsWith("_posts/")
                || postPath.Contains("../")
                || postPath.Contains("/.."))
            {
                return new NotFoundResult();
            }
            
            // Already generated? Redirect.
            if (await imageBlob.ExistsAsync())
            {
                return new RedirectResult(imageBlob.Uri.ToString());
            }
            
            // Read configuration
            var configuration = new ConfigurationBuilder()
                .SetBasePath(context.FunctionAppDirectory)
                .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();
            
            // Create the Octokit client
            var github = new GitHubClient(new ProductHeaderValue("PostCommentToPullRequest"),
                new Octokit.Internal.InMemoryCredentialStore(new Credentials(configuration["GitHubToken"])));

            // Get a reference to our GitHub repository and try retrieving post data
            var repoOwnerName = configuration["PullRequestRepository"].Split('/');
            var postData = await github.Repository.Content.GetRawContent(repoOwnerName[0], repoOwnerName[1], postPath);
            if (postData == null)
            {
                return new NotFoundResult();
            }
            
            // Parse Yaml
            var frontMatterYaml = Encoding.UTF8.GetString(postData);
            var frontMatterYamlTemp = frontMatterYaml.Split("---");
            if (frontMatterYamlTemp.Length < 2)
            {
                return new NotFoundResult();
            }

            frontMatterYaml = frontMatterYamlTemp[1];
            var frontMatter = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<FrontMatter>(frontMatterYaml);
            
            // Otherwise, generate an image...
            var cardWidth = 876;
            var cardHeight = 438;
            var textPadding = 25;
            var shadowOffset = 10;
            var titleSize = 42;
            var authorSize = 28;
            var titleLocation = new PointF(textPadding, cardHeight / 3);
            var authorLocation = new PointF(textPadding, cardHeight / 3 + authorSize);
            var font = Environment.OSVersion.Platform == PlatformID.Unix
                ? SystemFonts.Find("DejaVu Sans")
                : SystemFonts.Find("Segoe UI");
            
            using var cardImage = new Image<Rgba32>(cardWidth, cardHeight);
            
            // Shadow and box
            DrawRectangle(cardImage, shadowOffset, shadowOffset, cardWidth - shadowOffset, cardHeight - shadowOffset, Color.Gray);
            DrawRectangle(cardImage, 0, 0, cardWidth - shadowOffset, cardHeight - shadowOffset, Color.LightGray);
         
            // Title
            DrawText(cardImage, titleLocation.X, titleLocation.Y, cardWidth - textPadding - textPadding - textPadding - shadowOffset, Color.Black, font.CreateFont(titleSize, FontStyle.Bold), 
                frontMatter.Title);
            
            // Author & date
            DrawText(cardImage, authorLocation.X, authorLocation.Y, cardWidth - textPadding - textPadding - textPadding - shadowOffset, Color.Black, font.CreateFont(authorSize), 
                (frontMatter.Author ?? "") + (frontMatter.Date?.ToString(" | MMMM dd, yyyy") ?? ""));
            
            // Render, and save to blob storage
            var memoryStream = new MemoryStream();
            cardImage.Save(memoryStream, PngFormat.Instance);
            memoryStream.Position = 0;
            
            imageBlob.Properties.ContentType = "image/png";
            await imageBlob.UploadFromStreamAsync(memoryStream);
            
            return new RedirectResult(imageBlob.Uri.ToString());
        }

        private static void DrawRectangle(Image image, float x, float y, int width, int height, Color color)
        {
            image.Mutate(ctx => 
                ctx.Fill(new GraphicsOptions(true), color, 
                    new RectangularPolygon(x, y, width, height)));
        }

        private static void DrawText(Image image, float x, float y, int width, Color color, Font font, string text)
        {
            var textGraphicsOptions = new TextGraphicsOptions(true)
            {
                WrapTextWidth = width
            };

            var location = new PointF(x, y);
            var path = new PathBuilder()
                .SetOrigin(location)
                .AddLine(location, new PointF(y + width, location.Y)).Build();
            
            var glyphs = TextBuilder.GenerateGlyphs(text, path, new RendererOptions(font, textGraphicsOptions.DpiX, textGraphicsOptions.DpiY)
            {
                HorizontalAlignment = textGraphicsOptions.HorizontalAlignment,
                TabWidth = textGraphicsOptions.TabWidth,
                VerticalAlignment = textGraphicsOptions.VerticalAlignment,
                WrappingWidth = textGraphicsOptions.WrapTextWidth,
                ApplyKerning = textGraphicsOptions.ApplyKerning
            });
            
            image.Mutate(ctx => ctx
                .Fill((GraphicsOptions)textGraphicsOptions, color, glyphs));
        }

        private class FrontMatter
        {
            public string Title { get; set; }
            public string Author { get; set; }
            public List<string> Tags { get; set; } = new List<string>();
            public DateTimeOffset? Date { get; set; }
        }
    }
}