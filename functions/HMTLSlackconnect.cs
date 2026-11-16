
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace SlackApprovalFunction.Functions;

public class SlackApprovalFunctions
{
    private readonly HttpClient _httpClient = new HttpClient();

    // -------------------------------------------------
    // 1. SEND APPROVAL MESSAGE TO SLACK
    // POST /api/approval/send
    // -------------------------------------------------

    [Function("SendApprovalRequest")]
    public async Task<HttpResponseData> SendApprovalRequest(
        [HttpTrigger(AuthorizationLevel.Function, "post",
        Route = "approval/send")] HttpRequestData req)
    {
        try
        {
            var data = await JsonSerializer.DeserializeAsync<ApprovalRequest>(
                req.Body,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            if (data == null || string.IsNullOrWhiteSpace(data.DocumentName))
                return await CreateResponse(req, HttpStatusCode.BadRequest,
                    new { error = "DocumentName is required." });

            string token = Environment.GetEnvironmentVariable("SlackBotToken") ?? "";
            string channel = data.Channel ??
                Environment.GetEnvironmentVariable("SlackDefaultChannel") ?? "";

            if (string.IsNullOrWhiteSpace(token) ||
                string.IsNullOrWhiteSpace(channel))
            {
                return await CreateResponse(req,
                    HttpStatusCode.InternalServerError,
                    new { error = "Slack configuration is missing." });
            }

            string requestId = Guid.NewGuid().ToString("N");

            var payload = new
            {
                channel = channel,
                text = $"Approval required: {data.DocumentName}",
                blocks = BuildSlackBlocks(data, requestId)
            };

            using var message = new HttpRequestMessage(
                HttpMethod.Post,
                "https://slack.com/api/chat.postMessage");

            message.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer", token);

            message.Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.SendAsync(message);
            var responseBody = await response.Content.ReadAsStringAsync();

            using var json = JsonDocument.Parse(responseBody);

            bool success =
                response.IsSuccessStatusCode &&
                json.RootElement.TryGetProperty("ok", out var ok) &&
                ok.GetBoolean();

            if (!success)
            {
                return await CreateResponse(req,
                    HttpStatusCode.BadGateway,
                    new { error = "Slack API failed", details = responseBody });
            }

            return await CreateResponse(req, HttpStatusCode.OK, new
            {
                success = true,
                message = "Approval message sent to Slack.",
                requestId = requestId,
                channel = channel,
                slackResponse = responseBody
            });
        }
        catch (Exception ex)
        {
            return await CreateResponse(req,
                HttpStatusCode.InternalServerError,
                new { error = ex.Message });
        }
    }

    // -------------------------------------------------
    // 2. BUILD SLACK APPROVAL MESSAGE
    // -------------------------------------------------

    private static object[] BuildSlackBlocks(
        ApprovalRequest data, string requestId)
    {
        string missingFields = data.MissingFields.Count > 0
            ? string.Join("\n", data.MissingFields.Select(x => "• " + Escape(x)))
            : "None";

        string warning = data.ConfidenceStatus.Equals(
            "Low", StringComparison.OrdinalIgnoreCase)
            ? "⚠️ The document has a low confidence score. Please review the missing information before proceeding with production deployment."
            : "Please review the document before proceeding.";

        return new object[]
        {
            new
            {
                type = "header",
                text = new
                {
                    type = "plain_text",
                    text = "Document Approval Bot"
                }
            },

            new
            {
                type = "section",
                text = new
                {
                    type = "mrkdwn",
                    text = "*Approval required*"
                }
            },

            new
            {
                type = "section",
                text = new
                {
                    type = "mrkdwn",
                    text = $"*Request:* {Escape(data.Request)}"
                }
            },

            new { type = "divider" },

            new
            {
                type = "section",
                text = new
                {
                    type = "mrkdwn",
                    text =
                        "*📄 Document Details*\n\n" +
                        $"• *Document Name:* {Escape(data.DocumentName)}\n" +
                        $"• *Confidence Score:* {data.ConfidenceScore}%\n" +
                        $"• *Confidence Status:* {Escape(data.ConfidenceStatus)}\n" +
                        $"• *Reason:* {Escape(data.Reason)}\n" +
                        $"• *Missing Fields:*\n{missingFields}\n" +
                        $"• *Recommendation:* {Escape(data.Recommendation)}"
                }
            },

            new
            {
                type = "context",
                elements = new object[]
                {
                    new
                    {
                        type = "mrkdwn",
                        text = $"`{data.ConfidenceScore}% confidence — {Escape(data.ConfidenceStatus)}`"
                    }
                }
            },

            new
            {
                type = "section",
                text = new
                {
                    type = "mrkdwn",
                    text = warning
                }
            },

            new { type = "divider" },

            // INTERACTIVE ACCEPT / REJECT BUTTONS

            new
            {
                type = "actions",
                block_id = "approval_actions",
                elements = new object[]
                {
                    new
                    {
                        type = "button",
                        text = new
                        {
                            type = "plain_text",
                            text = "Accept"
                        },
                        style = "primary",
                        action_id = "approval_accept",
                        value = requestId
                    },

                    new
                    {
                        type = "button",
                        text = new
                        {
                            type = "plain_text",
                            text = "Reject"
                        },
                        style = "danger",
                        action_id = "approval_reject",
                        value = requestId
                    }
                }
            }
        };
    }

    // -------------------------------------------------
    // 3. HANDLE SLACK BUTTON CLICK
    // POST /api/slack/interactions
    // -------------------------------------------------

    [Function("SlackInteractions")]
    public async Task<HttpResponseData> SlackInteractions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post",
        Route = "slack/interactions")] HttpRequestData req)
    {
        string body = await new StreamReader(req.Body).ReadToEndAsync();

        string timestamp = GetHeader(req, "X-Slack-Request-Timestamp");
        string signature = GetHeader(req, "X-Slack-Signature");

        string signingSecret =
            Environment.GetEnvironmentVariable("SlackSigningSecret") ?? "";

        if (!VerifySlackSignature(body, timestamp, signature, signingSecret))
        {
            return await CreateResponse(req,
                HttpStatusCode.Unauthorized,
                new { error = "Invalid Slack signature." });
        }

        try
        {
            // Slack sends payload as URL-encoded form data.
            var form = System.Web.HttpUtility.ParseQueryString(body);
            string payloadJson = form["payload"] ?? "";

            using var json = JsonDocument.Parse(payloadJson);

            var root = json.RootElement;

            var action = root.GetProperty("actions")[0];

            string actionId = action.GetProperty("action_id").GetString() ?? "";
            string requestId = action.GetProperty("value").GetString() ?? "";

            string decision = actionId switch
            {
                "approval_accept" => "Accepted",
                "approval_reject" => "Rejected",
                _ => "Unknown"
            };

            string username = "Slack user";

            if (root.TryGetProperty("user", out var user))
            {
                username = user.TryGetProperty("username", out var u)
                    ? u.GetString() ?? username
                    : user.TryGetProperty("name", out var n)
                        ? n.GetString() ?? username
                        : username;
            }

            // Update the original Slack message.
            if (root.TryGetProperty("response_url", out var urlElement))
            {
                string responseUrl = urlElement.GetString() ?? "";

                if (!string.IsNullOrWhiteSpace(responseUrl))
                {
                    var updatePayload = new
                    {
                        replace_original = true,
                        text = $"Request {decision} by {username}.",
                        blocks = Array.Empty<object>()
                    };

                    await _httpClient.PostAsync(
                        responseUrl,
                        new StringContent(
                            JsonSerializer.Serialize(updatePayload),
                            Encoding.UTF8,
                            "application/json"));
                }
            }

            // TODO: Save decision/requestId/user to your database.
            // TODO: Trigger your deployment workflow if accepted.

            return await CreateResponse(req, HttpStatusCode.OK, new
            {
                success = true,
                requestId = requestId,
                decision = decision,
                approvedBy = username,
                message = $"Document {decision}."
            });
        }
        catch (Exception ex)
        {
            return await CreateResponse(req,
                HttpStatusCode.BadRequest,
                new { error = ex.Message });
        }
    }

    // -------------------------------------------------
    // 4. VERIFY SLACK SIGNATURE
    // -------------------------------------------------

    private static bool VerifySlackSignature(
        string body,
        string timestamp,
        string signature,
        string secret)
    {
        if (string.IsNullOrWhiteSpace(secret) ||
            string.IsNullOrWhiteSpace(timestamp) ||
            string.IsNullOrWhiteSpace(signature) ||
            !long.TryParse(timestamp, out long unixTime))
            return false;

        var requestTime =
            DateTimeOffset.FromUnixTimeSeconds(unixTime);

        if (Math.Abs(
            (DateTimeOffset.UtcNow - requestTime).TotalMinutes) > 5)
            return false;

        string baseString = $"v0:{timestamp}:{body}";

        byte[] hash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(baseString));

        string expected = "v0=" +
            Convert.ToHexString(hash).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signature));
    }

    // -------------------------------------------------
    // 5. HELPERS
    // -------------------------------------------------

    private static string GetHeader(HttpRequestData req, string name)
    {
        return req.Headers.TryGetValues(name, out var values)
            ? values.FirstOrDefault() ?? ""
            : "";
    }

    private static string Escape(string? value)
    {
        return (value ?? "")
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }

    private static async Task<HttpResponseData> CreateResponse(
        HttpRequestData req,
        HttpStatusCode status,
        object data)
    {
        var response = req.CreateResponse(status);

        response.Headers.Add(
            "Content-Type",
            "application/json; charset=utf-8");

        await response.WriteStringAsync(
            JsonSerializer.Serialize(data));

        return response;
    }
}

// -------------------------------------------------
// REQUEST MODEL
// -------------------------------------------------

public class ApprovalRequest
{
    public string? Channel { get; set; }

    public string Request { get; set; } =
        "Production deployment";

    public string DocumentName { get; set; } =
        "deployment-document.pdf";

    public int ConfidenceScore { get; set; } = 30;

    public string ConfidenceStatus { get; set; } = "Low";

    public string Reason { get; set; } =
        "Missing necessary mandatory fields required for validation and verification.";

    public List<string> MissingFields { get; set; } = new()
    {
        "Deployment date",
        "Environment details",
        "Business owner",
        "Approval reference"
    };

    public string Recommendation { get; set; } =
        "Complete all missing mandatory fields and revalidate the document.";
}
