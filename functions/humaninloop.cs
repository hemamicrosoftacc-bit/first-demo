using System.Text.Json;

using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Graph.Me.SendMail;
namespace Company.Function;

public class humaninloop
{
    private readonly ILogger<humaninloop> _logger;
    private readonly GraphServiceClient _graphClient;

    public humaninloop(ILogger<humaninloop> logger)
    {
        _logger = logger;

        // Replace this with your HTMLconnect Application (client) ID
        var clientId = "f023d69d-e3a6-483f-967f-0b12cf876136";

        // Authentication for a personal Microsoft account
        var credential = new InteractiveBrowserCredential(
            new InteractiveBrowserCredentialOptions
            {
                TenantId = "consumers",
                ClientId = clientId
            });

        // Connect to Microsoft Graph
        _graphClient = new GraphServiceClient(
            credential,
            new[] { "Mail.Send" }
        );
    }

    [Function("humaninloop")]
    public async Task<IActionResult> Run(
        [HttpTrigger(
            AuthorizationLevel.Function,
            "post"
        )] HttpRequest req)
    {
        _logger.LogInformation("File review function started.");

        // 1. Read file details from request body
        FileReviewRequest? fileDetails;

        try
        {
            fileDetails = await JsonSerializer.DeserializeAsync
                <FileReviewRequest>(
                    req.Body,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }
                );
        }
        catch (JsonException)
        {
            return new BadRequestObjectResult(
                "Invalid JSON request body."
            );
        }

        // 2. Validate required details
        if (fileDetails == null ||
            string.IsNullOrWhiteSpace(fileDetails.FileName) ||
            string.IsNullOrWhiteSpace(fileDetails.ReviewerEmail))
        {
            return new BadRequestObjectResult(
                "fileName and reviewerEmail are required."
            );
        }

        // 3. Create the email
        var emailMessage = new Message
        {
            Subject = $"Review Required: {fileDetails.FileName}",

            Body = new ItemBody
            {
                ContentType = BodyType.Text,

                Content =
                    "Hello,\n\n" +
                    "A new file has been submitted for review.\n\n" +

                    $"File Name: {fileDetails.FileName}\n" +
                    $"File Path: {fileDetails.FilePath ?? "Not provided"}\n" +
                    $"Uploaded By: {fileDetails.UploadedBy ?? "Not provided"}\n" +
                    $"Description: {fileDetails.Description ?? "Not provided"}\n\n" +

                    "Please review this file and provide your feedback.\n\n" +
                    "Thank you."
            },

            ToRecipients = new List<Recipient>
            {
                new Recipient
                {
                    EmailAddress = new EmailAddress
                    {
                        Address = fileDetails.ReviewerEmail
                    }
                }
            }
        };

        // 4. Send the email using the signed-in Outlook account
        try
        {
            await _graphClient.Me.SendMail.PostAsync(
                new SendMailPostRequestBody
                {
                    Message = emailMessage,
                    SaveToSentItems = true
                }
            );

            _logger.LogInformation(
                "Email sent for file {FileName}",
                fileDetails.FileName
            );

            return new OkObjectResult(new
            {
                message = "Email sent successfully.",
                fileName = fileDetails.FileName,
                reviewer = fileDetails.ReviewerEmail
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to send Outlook email."
            );

            return new ObjectResult(
                "Failed to send email. Check Outlook authentication and Mail.Send permission."
            )
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }
}

// Request body model
public class FileReviewRequest
{
    public string FileName { get; set; } = string.Empty;

    public string? FilePath { get; set; }

    public string? UploadedBy { get; set; }

    public string? Description { get; set; }

    public string ReviewerEmail { get; set; } = string.Empty;
}