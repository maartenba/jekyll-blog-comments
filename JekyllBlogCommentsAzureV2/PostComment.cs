using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Octokit;
using YamlDotNet.Serialization;

namespace JekyllBlogCommentsAzureV2
{
    public static class PostComment
    {
        private struct MissingRequiredValue { } // Placeholder for missing required form values

        private static readonly Regex ValidPathChars = new Regex(@"[^a-zA-Z0-9-]"); // Valid characters when mapping from the blog post slug to a file path
        private static readonly Regex ValidEmail = new Regex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$"); // Simplest form of email validation

        [FunctionName("PostComment")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)]HttpRequest request, 
            ILogger log, 
            ExecutionContext context)
        {
            // Read configuration
            var configuration = new ConfigurationBuilder()
                .SetBasePath(context.FunctionAppDirectory)
                .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            // Make sure the site posting the comment is the correct site.
            var allowedSite = configuration["CommentWebsiteUrl"];
            var postedSite = request.Form["comment-site"];
            if (!string.IsNullOrWhiteSpace(allowedSite) && !AreSameSites(allowedSite, postedSite))
            {
                return new BadRequestErrorMessageResult(
                    $"This Jekyll comments receiever does not handle forms for '${postedSite}'. You should point to your own instance.");
            }

            log.LogInformation("Try creating comment from form...");
            if (TryCreateCommentFromForm(request.Form, out var comment, out var errors))
            {
                log.LogInformation("Posting pull request...");
                await CreateCommentAsPullRequest(configuration, comment);
            }

            if (errors.Any())
            {
                log.LogError(string.Join("\n", errors));
                return new BadRequestErrorMessageResult(string.Join("\n", errors));
            }

            log.LogInformation("Success!");

            if (!Uri.TryCreate(configuration["RedirectUrl"], UriKind.Absolute, out var redirectUri))
            {
                return new OkResult();
            }

            return new RedirectResult(redirectUri.ToString());
        }

        private static bool AreSameSites(string commentSite, string postedCommentSite)
        {
            return Uri.TryCreate(commentSite, UriKind.Absolute, out var commentSiteUri)
                && Uri.TryCreate(postedCommentSite, UriKind.Absolute, out var postedCommentSiteUri)
                && commentSiteUri.Host.Equals(postedCommentSiteUri.Host, StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<PullRequest> CreateCommentAsPullRequest(IConfigurationRoot configuration, Comment comment)
        {
            // Create the Octokit client
            var github = new GitHubClient(new ProductHeaderValue("PostCommentToPullRequest"),
                new Octokit.Internal.InMemoryCredentialStore(new Credentials(configuration["GitHubToken"])));

            // Get a reference to our GitHub repository
            var repoOwnerName = configuration["PullRequestRepository"].Split('/');
            var repo = await github.Repository.Get(repoOwnerName[0], repoOwnerName[1]);

            // Create a new branch from the default branch
            var defaultBranch = await github.Repository.Branch.Get(repo.Id, repo.DefaultBranch);
            var newBranch = await github.Git.Reference.Create(repo.Id, new NewReference($"refs/heads/comment-{comment.id}", defaultBranch.Commit.Sha));

            // Create a new file with the comments in it
            var fileRequest = new CreateFileRequest($"Comment by {comment.name} on {comment.post_id}", new SerializerBuilder().Build().Serialize(comment.WithoutEmail()), newBranch.Ref)
            {
                Committer = new Committer(comment.name, comment.email ?? configuration["CommentFallbackCommitEmail"] ?? "redacted@example.com", comment.date)
            };
            await github.Repository.Content.CreateFile(repo.Id, $"_data/comments/{comment.post_id}/{comment.id}.yml", fileRequest);

            // Create a pull request for the new branch and file
            return await github.Repository.PullRequest.Create(repo.Id, new NewPullRequest(fileRequest.Message, newBranch.Ref, defaultBranch.Name)
            {
                Body = $"<img src=\"{comment.avatar}\" width=\"64\" height=\"64\" />\n\n**Comment by {comment.name} on {comment.post_id}:**\n\n{comment.message}"
            });
        }

        private static object ConvertParameter(string parameter, Type targetType)
        {
            return String.IsNullOrWhiteSpace(parameter)
                ? null
                : TypeDescriptor.GetConverter(targetType).ConvertFrom(parameter);
        }

        /// <summary>
        /// Try to create a Comment from the form.  Each Comment constructor argument will be name-matched
        /// against values in the form. Each non-optional arguments (those that don't have a default value)
        /// not supplied will cause an error in the list of errors and prevent the Comment from being created.
        /// </summary>
        /// <param name="form">Incoming form submission as a <see cref="NameValueCollection"/>.</param>
        /// <param name="comment">Created <see cref="Comment"/> if no errors occurred.</param>
        /// <param name="errors">A list containing any potential validation errors.</param>
        /// <returns>True if the Comment was able to be created, false if validation errors occurred.</returns>
        private static bool TryCreateCommentFromForm(IFormCollection form, out Comment comment, out List<string> errors)
        {
            var constructor = typeof(Comment).GetConstructors()[0];
            var values = constructor.GetParameters()
                .ToDictionary(
                    p => p.Name,
                    p => ConvertParameter(form[p.Name], p.ParameterType) ?? (p.HasDefaultValue ? p.DefaultValue : new MissingRequiredValue())
                );

            errors = values.Where(p => p.Value is MissingRequiredValue).Select(p => $"Form value missing for {p.Key}").ToList();
            if (values["email"] is string s && !ValidEmail.IsMatch(s))
            {
                errors.Add("email not in correct format");
            }

            comment = errors.Any() ? null : (Comment)constructor.Invoke(values.Values.ToArray());
            return !errors.Any();
        }

        /// <summary>
        /// Represents a Comment to be written to the repository in YML format.
        /// </summary>
        [SuppressMessage("ReSharper", "InconsistentNaming")]
        [SuppressMessage("ReSharper", "MemberCanBePrivate.Local")]
        [SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Local")]
        private class Comment
        {
            public Comment(string post_id, string message, string name, string email = null, Uri url = null, string avatar = null)
            {
                this.post_id = ValidPathChars.Replace(post_id, "-");
                this.message = message;
                this.name = name;
                this.email = email;
                if (!string.IsNullOrEmpty(email))
                {
                    var md5 = System.Security.Cryptography.MD5.Create();
                    var inputBytes = Encoding.ASCII.GetBytes(email.Trim().ToLowerInvariant());
                    var hash = md5.ComputeHash(inputBytes);

                    var sb = new StringBuilder();
                    for (int i = 0; i < hash.Length; i++)
                    {
                        sb.Append(hash[i].ToString("X2"));
                    }

                    avatar = "https://secure.gravatar.com/avatar/" + sb.ToString().ToLowerInvariant() + "?s=80&r=pg";
                }
                this.url = url;

                date = DateTime.UtcNow;
                id = new { this.post_id, this.name, this.message, date }.GetHashCode().ToString("x8");
                if (Uri.TryCreate(avatar, UriKind.Absolute, out Uri avatarUrl))
                {
                    this.avatar = avatarUrl;
                }
            }

            [YamlIgnore]
            public string post_id { get; private set; }

            public string id { get; private set; }
            public DateTime date { get; private set; }
            public string name { get; private set; }
            public string email { get; private set; }

            [YamlMember(typeof(string))]
            public Uri avatar { get; private set; }

            [YamlMember(typeof(string))]
            public Uri url { get; private set; }

            public string message { get; private set; }

            public Comment WithoutEmail()
            {
                return new Comment(
                    post_id,
                    message,
                    name,
                    null,
                    url,
                    avatar?.ToString());
            }
        }
    }
}
