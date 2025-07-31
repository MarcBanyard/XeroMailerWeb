// Copyright © 2025 Marc Banyard
//
// This file is part of XeroMailerWeb.
//
// XeroMailerWeb is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// XeroMailerWeb is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with XeroMailerWeb. If not, see <https://www.gnu.org/licenses/>.
//
// This project remains under full copyright by Marc Banyard.
// Redistribution must retain this notice and remain under GPL v3 or
// compatible licensing.

using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using XeroMailerWeb.Models;
using XeroMailerWeb.Services;

namespace XeroMailerWeb.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WebhookController : ControllerBase
{
    private readonly XeroService _xeroService;
    private readonly ILogger<WebhookController> _logger;
    private readonly IConfiguration _config;
    private readonly WebhookQueueService _webhookQueueService;

    public WebhookController(XeroService xeroService, ILogger<WebhookController> logger, IConfiguration config, WebhookQueueService webhookQueueService)
    {
        _xeroService = xeroService;
        _logger = logger;
        _config = config;
        _webhookQueueService = webhookQueueService;
    }

    private static readonly string WebhookLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_XeroWebhook.log");

    private void LogToFile(string message)
    {
        // Check if detailed logging is enabled
        var enableDetailedLogging = _config.GetValue<bool>("XeroMailerWeb:EnableDetailedLogging", false);
        if (!enableDetailedLogging)
            return;
            
        try
        {
            System.IO.File.AppendAllText(WebhookLogPath, $"[{DateTime.UtcNow:O}] {message}\n");
        }
        catch { /* Ignore logging errors */ }
    }

    [HttpPost("xero")]
    public async Task<IActionResult> HandleXeroWebhook()
    {
        string payload;
        try
        {
            using var reader = new StreamReader(Request.Body, Encoding.UTF8);
            payload = await reader.ReadToEndAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to read request body: {ex.Message}");
            LogToFile($"Failed to read request body: {ex.Message}");
            payload = "{}"; // Default to empty JSON
        }

        // Log headers and payload to file
        var headers = string.Join("; ", Request.Headers.Select(h => $"{h.Key}: {h.Value}"));
        LogToFile($"\n   ***** Incoming webhook *****");
        //LogToFile($"Incoming webhook:\nHeaders: {headers}\nPayload: {payload}");

        // Parse payload for ITR detection
        bool isITR = false;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            isITR =
                root.TryGetProperty("events", out var eventsProp) &&
                eventsProp.ValueKind == JsonValueKind.Array &&
                eventsProp.GetArrayLength() == 0 &&
                root.TryGetProperty("firstEventSequence", out var firstSeq) && firstSeq.GetInt32() == 0 &&
                root.TryGetProperty("lastEventSequence", out var lastSeq) && lastSeq.GetInt32() == 0 &&
                root.TryGetProperty("entropy", out var entropyProp) && entropyProp.ValueKind == JsonValueKind.String;
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to parse payload for ITR detection: {ex.Message}");
            LogToFile($"Failed to parse payload for ITR detection: {ex.Message}");
        }

        var webhookKey = _config["Xero:WebhookKey"];
        var signature = Request.Headers["X-Xero-Signature"].FirstOrDefault();
        bool signatureValid = false;
        string signatureValidationMessage = "Signature validation not performed - no webhook key configured";

        if (!string.IsNullOrEmpty(webhookKey) && !string.IsNullOrEmpty(signature))
        {
            try
            {
                signatureValid = _xeroService.VerifyWebhookSignature(payload, signature, webhookKey);
                signatureValidationMessage = signatureValid ? "Signature validation successful" : "Signature validation failed";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during signature validation");
                LogToFile($"Error during signature validation: {ex.Message}");
                signatureValidationMessage = $"Signature validation error: {ex.Message}";
            }
        }
        else
        {
            _logger.LogWarning("No webhook key or signature header configured - signature validation skipped");
            LogToFile("No webhook key or signature header configured - signature validation skipped");
        }
        LogToFile($"Signature validation result: {signatureValidationMessage}");

        // ITR logic: must return 200 (no body) for valid, 401 (no body) for invalid
        if (isITR)
        {
            if (signatureValid)
            {
                return new StatusCodeResult(200); // No body
            }
            else
            {
                _logger.LogWarning("ITR: Invalid signature, returning 401 Unauthorized (no body)");
                LogToFile("ITR: Invalid signature, returning 401 Unauthorized (no body)");
                return new StatusCodeResult(401); // No body
            }
        }

        // For real webhooks: validate signature, always return 200 OK (with body)
        if (signatureValid)
        {
            try
            {
                var webhookData = JsonSerializer.Deserialize<XeroWebhookPayload>(payload);
                if (webhookData?.Events != null)
                {
                    LogToFile($"[HandleXeroWebhook] Parsed {webhookData.Events.Count} events from webhook payload.");
                    foreach (var webhookEvent in webhookData.Events)
                    {
                        LogToFile($"[HandleXeroWebhook] Enqueuing event: Category={webhookEvent.EventCategory}, Type={webhookEvent.EventType}, ResourceId={webhookEvent.ResourceId}, ResourceUri={webhookEvent.ResourceUri}");
                        await _webhookQueueService.EnqueueAsync(webhookEvent);
                    }
                }
                else
                {
                    _logger.LogWarning("[HandleXeroWebhook] Invalid webhook payload format - no events found");
                    LogToFile("[HandleXeroWebhook] Invalid webhook payload format - no events found");
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning($"[HandleXeroWebhook] Failed to parse webhook payload as JSON: {ex.Message}");
                LogToFile($"[HandleXeroWebhook] Failed to parse webhook payload as JSON: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[HandleXeroWebhook] Unexpected error parsing webhook payload: {ex.Message}");
                LogToFile($"[HandleXeroWebhook] Unexpected error parsing webhook payload: {ex.Message}");
            }
        }
        else
        {
            _logger.LogError("[HandleXeroWebhook] Checkpoint: signatureValid is false, skipping event processing block.");
            LogToFile("[HandleXeroWebhook] Checkpoint: signatureValid is false, skipping event processing block.");
        }

        return Ok(new {
            message = "Webhook received successfully",
            timestamp = DateTime.UtcNow,
            signature_valid = signatureValid
        });
    }

    private bool IsIntentToReceiveTest(string payload)
    {
        try
        {
            LogToFile($"Analyzing payload for Intent to Receive test: {payload}");
            
            var payloadLower = payload.ToLower();
            
            // Check for Xero's specific Intent to Receive test indicators
            if (payloadLower.Contains("intent") && payloadLower.Contains("receive"))
            {
                LogToFile("Detected Intent to Receive test payload (intent + receive keywords)");
                return true;
            }
            
            // Check for Xero's actual Intent to Receive pattern (empty events array with entropy)
            // This matches the pattern: {"events":[],"firstEventSequence": 0,"lastEventSequence": 0, "entropy": "..."}
            if (payloadLower.Contains("\"events\":[]") && payloadLower.Contains("\"entropy\":"))
            {
                LogToFile("Detected Xero Intent to Receive pattern (empty events with entropy)");
                return true;
            }
            
            // Check for test-specific event types (case insensitive)
            if (payloadLower.Contains("\"eventtype\":\"test\"") || 
                payloadLower.Contains("\"eventtype\": \"test\"") ||
                payloadLower.Contains("\"eventtype\":\"TEST\"") ||
                payloadLower.Contains("\"eventtype\": \"TEST\""))
            {
                LogToFile("Detected test event type in payload");
                return true;
            }
            
            // Check for test resource IDs (case insensitive)
            if (payloadLower.Contains("\"resourceid\":\"test\"") ||
                payloadLower.Contains("\"resourceid\": \"test\"") ||
                payloadLower.Contains("\"resourceid\":\"TEST\"") ||
                payloadLower.Contains("\"resourceid\": \"TEST\""))
            {
                LogToFile("Detected test resource ID in payload");
                return true;
            }
            
            // Check for entropy field which is common in Xero test payloads
            if (payloadLower.Contains("\"entropy\":\"test\"") ||
                payloadLower.Contains("\"entropy\": \"test\"") ||
                payloadLower.Contains("\"entropy\":\"TEST\"") ||
                payloadLower.Contains("\"entropy\": \"TEST\""))
            {
                LogToFile("Detected test entropy in payload");
                return true;
            }
            
            // Check for any event with TEST category
            if (payloadLower.Contains("\"eventcategory\":\"test\"") ||
                payloadLower.Contains("\"eventcategory\": \"test\"") ||
                payloadLower.Contains("\"eventcategory\":\"TEST\"") ||
                payloadLower.Contains("\"eventcategory\": \"TEST\""))
            {
                LogToFile("Detected test event category in payload");
                return true;
            }
            
            // Check for webhook verification specific patterns
            if (payloadLower.Contains("webhook") && payloadLower.Contains("verification"))
            {
                LogToFile("Detected webhook verification payload");
                return true;
            }
            
            // Check for any payload that contains "test" in multiple places (likely a test)
            var testCount = (payloadLower.Split("test").Length - 1);
            if (testCount >= 2)
            {
                LogToFile($"Detected multiple 'test' occurrences ({testCount}) in payload - likely a test");
                return true;
            }
            
            _logger.LogError("Payload does not appear to be an Intent to Receive test");
            LogToFile("Payload does not appear to be an Intent to Receive test");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error analyzing payload for Intent to Receive test");
            LogToFile($"Error analyzing payload for Intent to Receive test: {ex.Message}");
            return false;
        }
    }

    private async Task<List<string>> GetCustomerEmailsFromInvoice(string invoiceId, string tenantId)
    {
        try
        {
            var invoice = await _xeroService.GetInvoiceDetailsAsync(tenantId, invoiceId);
            if (invoice == null)
            {
                _logger.LogWarning($"Could not fetch invoice details for {invoiceId}");
                LogToFile($"Could not fetch invoice details for {invoiceId}");
                return new List<string>();
            }
            var emails = XeroService.ExtractContactEmails(invoice.Value);
            if (emails.Count == 0)
            {
                _logger.LogWarning($"No contact emails found in invoice {invoiceId}");
                LogToFile($"No contact emails found in invoice {invoiceId}");
            }
            return emails;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error getting customer emails for invoice {invoiceId}");
            LogToFile($"Error getting customer emails for invoice {invoiceId}: {ex.Message}");
            return new List<string>();
        }
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
    }

    [HttpPost("xero/test")]
    public async Task<IActionResult> HandleXeroTestWebhook()
    {
        try
        {
            // Read the request body
            using var reader = new StreamReader(Request.Body, Encoding.UTF8);
            var payload = await reader.ReadToEndAsync();
            
            LogToFile($"Received Xero test webhook payload: {payload}");

            // Verify webhook signature if configured
            var webhookKey = _config["Xero:WebhookKey"];
            if (!string.IsNullOrEmpty(webhookKey))
            {
                var signature = Request.Headers["X-Xero-Signature"].FirstOrDefault();
                if (string.IsNullOrEmpty(signature))
                {
                    _logger.LogWarning("No Xero signature found in test request headers");
                    LogToFile("No Xero signature found in test request headers");
                    return BadRequest("Missing signature");
                }

                if (!_xeroService.VerifyWebhookSignature(payload, signature, webhookKey))
                {
                    _logger.LogWarning("Invalid webhook signature in test");
                    LogToFile("Invalid webhook signature in test");
                    return BadRequest("Invalid signature");
                }
            }

            LogToFile("Intent to Receive test successful");
            return Ok(new { 
                message = "Intent to Receive test successful",
                timestamp = DateTime.UtcNow,
                status = "verified"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing test webhook");
            LogToFile($"Error processing test webhook: {ex}");
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("tenants")]
    public async Task<IActionResult> GetTenants()
    {
        try
        {
            var accessToken = await _xeroService.GetValidAccessTokenAsync();
            var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            
            var response = await client.GetAsync("https://api.xero.com/api.xro/2.0/connections");
            if (!response.IsSuccessStatusCode)
            {
                return BadRequest($"Failed to get tenants: {await response.Content.ReadAsStringAsync()}");
            }
            
            var content = await response.Content.ReadAsStringAsync();
            return Ok(content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting tenants");
            LogToFile($"Error getting tenants: {ex}");
            return StatusCode(500, "Error getting tenants");
        }
    }

    [HttpGet("tenant-id")]
    public async Task<IActionResult> GetTenantId()
    {
        try
        {
            var tenantId = await _xeroService.GetTenantIdAsync();
            if (string.IsNullOrEmpty(tenantId))
            {
                return NotFound("No active tenant found. Please ensure your Xero app has been authorized.");
            }
            
            return Ok(new { 
                tenantId = tenantId,
                message = "Tenant ID retrieved successfully",
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting tenant ID");
            LogToFile($"Error getting tenant ID: {ex}");
            return StatusCode(500, "Error getting tenant ID");
        }
    }

    [HttpGet("invoices/{tenantId}")]
    public async Task<IActionResult> GetInvoices(string tenantId)
    {
        try
        {
            var accessToken = await _xeroService.GetValidAccessTokenAsync();
            var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            client.DefaultRequestHeaders.Add("xero-tenant-id", tenantId);
            
            var response = await client.GetAsync("https://api.xero.com/api.xro/2.0/Invoices?page=1&pageSize=10");
            if (!response.IsSuccessStatusCode)
            {
                return BadRequest($"Failed to get invoices: {await response.Content.ReadAsStringAsync()}");
            }
            
            var content = await response.Content.ReadAsStringAsync();
            return Ok(content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting invoices");
            LogToFile($"Error getting invoices: {ex}");
            return StatusCode(500, "Error getting invoices");
        }
    }
} 