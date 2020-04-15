using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
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
    public static class GenerateMissingTwitterCards
    {
        [FunctionName(nameof(GenerateMissingTwitterCards))]
        public static async Task<IActionResult> RunAsync(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)]
            HttpRequest request,

            [Blob("locks/" + nameof(GenerateMissingTwitterCards), Connection = "AzureWebJobsStorage")]
            ICloudBlob lockBlob,
            
            ILogger log,
            
            ExecutionContext context)
        {
            // Lock present?
            string lockIdentifier = null;
            if (!await lockBlob.ExistsAsync())
            {
                await lockBlob.UploadFromByteArrayAsync(new byte[1], 0, 1);
            }
            
            try
            {
                lockIdentifier = await lockBlob.AcquireLeaseAsync(TimeSpan.FromMinutes(2));
            }
            catch (StorageException ex) when (ex.RequestInformation?.HttpStatusCode == (int)HttpStatusCode.Conflict || ex.RequestInformation?.HttpStatusCode == (int)HttpStatusCode.PreconditionFailed)
            {
                return new OkObjectResult("Could not acquire lock, skipping execution.");
            }
            
            // Read configuration
            var configuration = new ConfigurationBuilder()
                .SetBasePath(context.FunctionAppDirectory)
                .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();
            
            // Defaults
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

            var yamlDeserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            
            // Create the Octokit client
            var github = new GitHubClient(new ProductHeaderValue("PostCommentToPullRequest"),
                new Octokit.Internal.InMemoryCredentialStore(new Credentials(configuration["GitHubToken"])));
            
            // Get all contents from our repo
            var repoOwnerName = configuration["PullRequestRepository"].Split('/');
            var repo = await github.Repository.Get(repoOwnerName[0], repoOwnerName[1]);
            var defaultBranch = await github.Repository.Branch.Get(repo.Id, repo.DefaultBranch);
            
            var repoContentsPosts = await github.Repository.Content.GetAllContents(repoOwnerName[0], repoOwnerName[1], "_posts");
            var repoContentsCards = await github.Repository.Content.GetAllContents(repoOwnerName[0], repoOwnerName[1], "images/cards");

            Reference newBranch = null;
            var itemsCreated = 0;
            
            foreach (var repoPost in repoContentsPosts)
            {
                // Is there a card?
                if (repoContentsCards.Any(it => it.Name == repoPost.Name + ".png")) continue;
                
                // If not, generate one!
                var postData = await github.Repository.Content.GetRawContent(repoOwnerName[0], repoOwnerName[1], repoPost.Path);
                if (postData == null) continue;
                
                var frontMatterYaml = Encoding.UTF8.GetString(postData);
                var frontMatterYamlTemp = frontMatterYaml.Split("---");
                if (frontMatterYamlTemp.Length >= 2)
                {
                    // Deserialize front matter
                    frontMatterYaml = frontMatterYamlTemp[1];
                    var frontMatter = yamlDeserializer
                        .Deserialize<FrontMatter>(frontMatterYaml);

                    using var cardImage = new Image<Rgba32>(cardWidth, cardHeight);
                
                    // Shadow and box
                    DrawRectangle(cardImage, shadowOffset, shadowOffset, cardWidth - shadowOffset, cardHeight - shadowOffset, Color.Gray);
                    DrawRectangle(cardImage, 0, 0, cardWidth - shadowOffset, cardHeight - shadowOffset, Color.White);
             
                    // Title
                    DrawText(cardImage, titleLocation.X, titleLocation.Y, cardWidth - textPadding - textPadding - textPadding - shadowOffset, Color.Black, font.CreateFont(titleSize, FontStyle.Bold), 
                        frontMatter.Title);
                
                    // Author & date
                    DrawText(cardImage, authorLocation.X, authorLocation.Y, cardWidth - textPadding - textPadding - textPadding - shadowOffset, Color.DarkGray, font.CreateFont(authorSize), 
                        (frontMatter.Author ?? "") + (frontMatter.Date?.ToString(" | MMMM dd, yyyy", CultureInfo.InvariantCulture) ?? ""));
                
                    // Render card image
                    await using var memoryStream = new MemoryStream();
                    cardImage.Save(memoryStream, PngFormat.Instance);
                    memoryStream.Position = 0;
                    
                    // Create a pull request for it
                    if (newBranch == null)
                    {
                        newBranch = await github.Git.Reference.Create(repo.Id, new NewReference($"refs/heads/twitter-cards-" + Guid.NewGuid(), defaultBranch.Commit.Sha));
                    }

                    var latestCommit = await github.Git.Commit.Get(repo.Id, newBranch.Object.Sha);
                    
                    var file = new NewBlob { Encoding = EncodingType.Base64, Content = Convert.ToBase64String(memoryStream.ToArray()) };
                    var blob = await github.Git.Blob.Create(repo.Id, file);

                    var nt = new NewTree { BaseTree = latestCommit.Sha };
                    nt.Tree.Add(new NewTreeItem { Path = $"images/cards/{repoPost.Name}.png", Mode = "100644", Type = TreeType.Blob, Sha = blob.Sha });
                    
                    var newTree = await github.Git.Tree.Create(repo.Id, nt);
                    var newCommit = new NewCommit($"[Automatic] Add a Twitter card for: {frontMatter.Title}", newTree.Sha, latestCommit.Sha);
                    var commit = await github.Git.Commit.Create(repo.Id, newCommit);
                    newBranch = await github.Git.Reference.Update(repo.Id, newBranch.Ref, new ReferenceUpdate(commit.Sha));

                    // Renew lease
                    try
                    {
                        await lockBlob.RenewLeaseAsync(new AccessCondition { LeaseId = lockIdentifier });
                    }
                    catch (StorageException ex) when (ex.RequestInformation?.HttpStatusCode == (int)HttpStatusCode.Conflict || ex.RequestInformation?.HttpStatusCode == (int)HttpStatusCode.PreconditionFailed)
                    {
                        break;
                    }
                    
                    // Stop after X items
                    if (++itemsCreated >= 10) break;
                }
            }

            // Create PR
            if (newBranch != null)
            {
                await github.Repository.PullRequest.Create(repo.Id, new NewPullRequest($"[Automatic] Add missing Twitter cards", newBranch.Ref, defaultBranch.Name)
                {
                    Body = $"Add Twitter cards for various posts"
                });
            }
            
            // Release lock
            await lockBlob.ReleaseLeaseAsync(new AccessCondition { LeaseId = lockIdentifier });
            
            return new OkObjectResult("Done.");
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