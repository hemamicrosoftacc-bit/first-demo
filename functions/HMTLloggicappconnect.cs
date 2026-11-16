using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Text;
using System.Text.Json;

public class LogicAppCaller
{
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;

    public LogicAppCaller(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory)
    {
        _configuration = configuration;
        _httpClient = httpClientFactory.CreateClient();
    }

    [Function("CallLogicApp")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post")]
        HttpRequestData req)
    {
        // Read request body
        string requestBody = await req.ReadAsStringAsync();

        // Get Logic App URL from configuration
        string logicAppUrl =
            _configuration["LogicAppUrl"];

        // Forward request to Logic App
        var content = new StringContent(
            requestBody,
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.PostAsync(
            logicAppUrl,
            content);

        string responseBody =
            await response.Content.ReadAsStringAsync();

        // Return Logic App response
        var functionResponse = req.CreateResponse(
            response.IsSuccessStatusCode
                ? HttpStatusCode.OK
                : response.StatusCode);

        await functionResponse.WriteStringAsync(responseBody);

        return functionResponse;
    }
}