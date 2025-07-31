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

using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

namespace XeroMailerWeb.Services;

public class XeroService
{
    private readonly IConfiguration _config;
    private readonly ILogger<XeroService> _logger;

    public XeroService(IConfiguration config, ILogger<XeroService> logger)
    {
        _config = config;
        _logger = logger;
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

    // Add a method to force refresh the access token regardless of expiry
    private async Task<string> ForceRefreshAccessTokenAsync()
    {
        var tokenPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_XeroTokens.json");
        if (!File.Exists(tokenPath))
            throw new InvalidOperationException("Xero tokens file not found. Please authorize the app via /xero/connect.");
        var tokenJson = await File.ReadAllTextAsync(tokenPath);
        var tokenObj = System.Text.Json.JsonDocument.Parse(tokenJson).RootElement;
        var refreshToken = tokenObj.GetProperty("refresh_token").GetString();
        var clientId = _config["Xero:XeroClientId"] ?? throw new InvalidOperationException("Xero Client ID not configured");
        var clientSecret = _config["Xero:XeroClientSecret"] ?? throw new InvalidOperationException("Xero Client Secret not configured");
        var client = new HttpClient();
        var tokenRequest = new HttpRequestMessage(System.Net.Http.HttpMethod.Post, "https://identity.xero.com/connect/token")
        {
            Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new KeyValuePair<string, string>("refresh_token", refreshToken),
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("client_secret", clientSecret)
            })
        };
        var tokenResponse = await client.SendAsync(tokenRequest);
        var tokenContent = await tokenResponse.Content.ReadAsStringAsync();
        if (!tokenResponse.IsSuccessStatusCode)
        {
            throw new Exception($"Token refresh failed: {tokenContent}");
        }
        var tokenData = System.Text.Json.JsonDocument.Parse(tokenContent).RootElement;
        var newAccessToken = tokenData.GetProperty("access_token").GetString();
        var newRefreshToken = tokenData.GetProperty("refresh_token").GetString();
        var newExpiresIn = tokenData.GetProperty("expires_in").GetInt32();
        var tokenType = tokenData.GetProperty("token_type").GetString();
        // Save new tokens
        var newTokenObj = new {
            access_token = newAccessToken,
            refresh_token = newRefreshToken,
            expires_in = newExpiresIn,
            token_type = tokenType,
            obtained_at = DateTime.UtcNow
        };
        var newTokenJson = System.Text.Json.JsonSerializer.Serialize(newTokenObj, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(tokenPath, newTokenJson);
        return newAccessToken!;
    }

    // Debounce dictionary to ensure only one retry loop per invoice
    private static readonly ConcurrentDictionary<string, Task<byte[]>> _pdfFetchTasks = new();

    public async Task<byte[]> GetInvoicePdfAsync(string tenantId, string invoiceId)
    {
        // Only allow one retry loop per invoice at a time
        return await _pdfFetchTasks.GetOrAdd(invoiceId, _ => FetchPdfWithRetryAsync(tenantId, invoiceId));
    }

    private async Task<byte[]> FetchPdfWithRetryAsync(string tenantId, string invoiceId)
    {
        try
        {
            var accessToken = await GetValidAccessTokenAsync();
            var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            var url = $"https://api.xero.com/api.xro/2.0/Invoices/{invoiceId}";
            const int maxRetries = 2;
            HttpResponseMessage? response = null;
            string? responseBody = null;
            
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
                request.Headers.Add("xero-tenant-id", tenantId);
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/pdf"));
                
                response = await client.SendAsync(request);
                responseBody = await response.Content.ReadAsStringAsync();
                
                if (response.IsSuccessStatusCode)
                {
                    var pdfBytes = await response.Content.ReadAsByteArrayAsync();
                    return pdfBytes;
                }
                
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    LogToFile($"[FetchPdfWithRetryAsync] Attempt {attempt}: Token expired, refreshing and retrying");
                    accessToken = await ForceRefreshAccessTokenAsync();
                    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                    continue;
                }
                
                if (attempt < maxRetries)
                {
                    _logger.LogWarning($"[FetchPdfWithRetryAsync] Attempt {attempt} failed, waiting before retry");
                    LogToFile($"[FetchPdfWithRetryAsync] Attempt {attempt} failed, waiting before retry");
                    await Task.Delay(1000);
                }
            }
            
            throw new Exception($"Failed to get invoice PDF (attempt {maxRetries}): Status {response?.StatusCode}, Body: {responseBody}");
        }
        catch (Exception ex)
        {
            _logger.LogError($"[FetchPdfWithRetryAsync] Error fetching PDF: {ex.Message}");
            LogToFile($"[FetchPdfWithRetryAsync] Error fetching PDF: {ex.Message}");
            throw;
        }
    }

    // Fetch invoice details to get InvoiceNumber and Reference
    public async Task<JsonElement?> GetInvoiceDetailsAsync(string tenantId, string invoiceId)
    {
        try
        {
            var accessToken = await GetValidAccessTokenAsync();
            var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            var url = $"https://api.xero.com/api.xro/2.0/Invoices/{invoiceId}";
            var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            request.Headers.Add("xero-tenant-id", tenantId);
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            
            var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var doc = System.Text.Json.JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("Invoices")[0];
            }
            
            _logger.LogWarning($"[GetInvoiceDetailsAsync] Failed to get invoice details for {invoiceId}: {response.StatusCode}");
            LogToFile($"[GetInvoiceDetailsAsync] Failed to get invoice details for {invoiceId}: {response.StatusCode}");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError($"[GetInvoiceDetailsAsync] Error getting invoice details for {invoiceId}: {ex.Message}");
            LogToFile($"[GetInvoiceDetailsAsync] Error getting invoice details for {invoiceId}: {ex.Message}");
            return null;
        }
    }

    public static List<string> ExtractContactEmails(JsonElement invoice)
    {
        var emails = new List<string>();
        if (invoice.TryGetProperty("Contact", out var contact))
        {
            if (contact.TryGetProperty("EmailAddress", out var emailProp))
            {
                var email = emailProp.GetString();
                if (!string.IsNullOrWhiteSpace(email))
                    emails.Add(email);
            }
            // Add ContactPersons with IncludeInEmails = true
            if (contact.TryGetProperty("ContactPersons", out var persons) && persons.ValueKind == JsonValueKind.Array)
            {
                foreach (var person in persons.EnumerateArray())
                {
                    if (person.TryGetProperty("IncludeInEmails", out var include) && include.GetBoolean())
                    {
                        if (person.TryGetProperty("EmailAddress", out var personEmail))
                        {
                            var email = personEmail.GetString();
                            if (!string.IsNullOrWhiteSpace(email))
                                emails.Add(email);
                        }
                    }
                }
            }
        }
        return emails.Distinct().ToList();
    }

    public async Task<string> GetValidAccessTokenAsync()
    {
        // Load tokens from file
        var tokenPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_XeroTokens.json");
        if (!File.Exists(tokenPath))
            throw new InvalidOperationException("Xero tokens file not found. Please authorize the app via /xero/connect.");
        var tokenJson = await File.ReadAllTextAsync(tokenPath);
        var tokenObj = System.Text.Json.JsonDocument.Parse(tokenJson).RootElement;
        var accessToken = tokenObj.GetProperty("access_token").GetString();
        var refreshToken = tokenObj.GetProperty("refresh_token").GetString();
        var expiresIn = tokenObj.GetProperty("expires_in").GetInt32();
        var obtainedAt = tokenObj.GetProperty("obtained_at").GetDateTime();
        var expiresAt = obtainedAt.AddSeconds(expiresIn - 60); // 60s buffer
        if (DateTime.UtcNow < expiresAt && !string.IsNullOrEmpty(accessToken))
        {
            return accessToken;
        }
        // Refresh the access token
        var clientId = _config["Xero:XeroClientId"] ?? throw new InvalidOperationException("Xero Client ID not configured");
        var clientSecret = _config["Xero:XeroClientSecret"] ?? throw new InvalidOperationException("Xero Client Secret not configured");
        var client = new HttpClient();
        var tokenRequest = new HttpRequestMessage(System.Net.Http.HttpMethod.Post, "https://identity.xero.com/connect/token")
        {
            Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new KeyValuePair<string, string>("refresh_token", refreshToken),
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("client_secret", clientSecret)
            })
        };
        var tokenResponse = await client.SendAsync(tokenRequest);
        var tokenContent = await tokenResponse.Content.ReadAsStringAsync();
        if (!tokenResponse.IsSuccessStatusCode)
        {
            throw new Exception($"Token refresh failed: {tokenContent}");
        }
        var tokenData = System.Text.Json.JsonDocument.Parse(tokenContent).RootElement;
        var newAccessToken = tokenData.GetProperty("access_token").GetString();
        var newRefreshToken = tokenData.GetProperty("refresh_token").GetString();
        var newExpiresIn = tokenData.GetProperty("expires_in").GetInt32();
        var tokenType = tokenData.GetProperty("token_type").GetString();
        // Save new tokens
        var newTokenObj = new {
            access_token = newAccessToken,
            refresh_token = newRefreshToken,
            expires_in = newExpiresIn,
            token_type = tokenType,
            obtained_at = DateTime.UtcNow
        };
        var newTokenJson = System.Text.Json.JsonSerializer.Serialize(newTokenObj, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(tokenPath, newTokenJson);
        return newAccessToken!;
    }

    public bool VerifyWebhookSignature(string payload, string signature, string webhookKey)
    {
        try
        {
            if (string.IsNullOrEmpty(payload))
            {
                _logger.LogWarning("Payload is null or empty");
                LogToFile("Payload is null or empty");
                return false;
            }

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(webhookKey));
            var computedSignature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));

            var isValid = signature.Equals(computedSignature, StringComparison.OrdinalIgnoreCase);
            return isValid;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying webhook signature");
            LogToFile($"Error verifying webhook signature: {ex}");
            return false;
        }
    }

    public async Task<string?> GetTenantIdAsync(string? extraLogPath = null)
    {
        try
        {
            var accessToken = await GetValidAccessTokenAsync();
            var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            
            // Correct endpoint for connections
            var response = await client.GetAsync("https://api.xero.com/connections");
            var content = await response.Content.ReadAsStringAsync();
            if (!string.IsNullOrEmpty(extraLogPath))
            {
                File.AppendAllText(extraLogPath, $"[{DateTime.UtcNow:O}] /connections status code: {(int)response.StatusCode} {response.StatusCode}\n");
                foreach (var header in response.Headers)
                {
                    File.AppendAllText(extraLogPath, $"[{DateTime.UtcNow:O}] Header: {header.Key}: {string.Join(", ", header.Value)}\n");
                }
                foreach (var header in response.Content.Headers)
                {
                    File.AppendAllText(extraLogPath, $"[{DateTime.UtcNow:O}] Content-Header: {header.Key}: {string.Join(", ", header.Value)}\n");
                }
            }
            if (!response.IsSuccessStatusCode)
            {
                if (!string.IsNullOrEmpty(extraLogPath))
                    File.AppendAllText(extraLogPath, $"[{DateTime.UtcNow:O}] Failed to get connections: {content}\n");
                return null;
            }
            if (!string.IsNullOrEmpty(extraLogPath))
                File.AppendAllText(extraLogPath, $"[{DateTime.UtcNow:O}] /connections response: {content}\n");
            var connections = JsonSerializer.Deserialize<JsonElement>(content);
            // Get the first active connection
            if (connections.ValueKind == JsonValueKind.Array)
            {
                foreach (var connection in connections.EnumerateArray())
                {
                    if (connection.TryGetProperty("tenantId", out var tenantId))
                    {
                        var tenantIdValue = tenantId.GetString();
                        if (!string.IsNullOrEmpty(extraLogPath))
                            File.AppendAllText(extraLogPath, $"[{DateTime.UtcNow:O}] Found tenant ID: {tenantIdValue}\n");
                        return tenantIdValue;
                    }
                }
            }
            LogToFile("No connections found");
            if (!string.IsNullOrEmpty(extraLogPath))
                File.AppendAllText(extraLogPath, $"[{DateTime.UtcNow:O}] No connections found in /connections response.\n");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching tenant ID");
            LogToFile($"Error fetching tenant ID: {ex}");
            if (!string.IsNullOrEmpty(extraLogPath))
                File.AppendAllText(extraLogPath, $"[{DateTime.UtcNow:O}] Exception in GetTenantIdAsync: {ex}\n");
            return null;
        }
    }

    public async Task<List<JsonElement>> GetOutstandingInvoicesAsync(string tenantId)
    {
        var accessToken = await GetValidAccessTokenAsync();
        var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        client.DefaultRequestHeaders.Add("xero-tenant-id", tenantId);
        var uri = "https://api.xero.com/api.xro/2.0/Invoices?Status=AUTHORISED";
        var response = await client.GetAsync(uri);
        if (!response.IsSuccessStatusCode)
            throw new Exception($"Failed to fetch invoices: {await response.Content.ReadAsStringAsync()}");
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("Invoices").EnumerateArray().ToList();
    }

    // Fetch the OnlineInvoiceUrl from the /OnlineInvoice endpoint
    public async Task<string?> GetOnlineInvoiceUrlAsync(string tenantId, string invoiceId)
    {
        var accessToken = await GetValidAccessTokenAsync();
        var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var url = $"https://api.xero.com/api.xro/2.0/Invoices/{invoiceId}/OnlineInvoice";
        var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
        request.Headers.Add("xero-tenant-id", tenantId);
        var response = await client.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        //LogToFile($" - OnlineInvoiceUrl: {json}");
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError($"Failed to get OnlineInvoiceUrl: {json}");
            LogToFile($"Failed to get OnlineInvoiceUrl: {json}");
            return null;
        }
        var doc = JsonDocument.Parse(json);
        string? foundUrl = FindOnlineInvoiceUrlRecursive(doc.RootElement);
        return foundUrl;
    }

    // Recursively search for OnlineInvoiceUrl in any depth
    private string? FindOnlineInvoiceUrlRecursive(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.NameEquals("OnlineInvoiceUrl") && prop.Value.ValueKind == JsonValueKind.String)
                {
                    return prop.Value.GetString();
                }
                var found = FindOnlineInvoiceUrlRecursive(prop.Value);
                if (found != null) return found;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = FindOnlineInvoiceUrlRecursive(item);
                if (found != null) return found;
            }
        }
        return null;
    }

    // State-based deduplication: track last SentToContact state for each invoice, with timestamp, in JSON
    private static readonly string SentToContactStatePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_XeroSentToContactState.json");
    private static readonly Dictionary<string, (bool sentToContact, DateTime lastUpdated)> SentToContactState = new();
    private static readonly object SentToContactLock = new();
    private static readonly TimeSpan SentToContactRetention = TimeSpan.FromDays(7);

    static XeroService()
    {
        CleanupOldSentToContactStates(SentToContactRetention);
    }

    private static void CleanupOldSentToContactStates(TimeSpan maxAge)
    {
        lock (SentToContactLock)
        {
            var now = DateTime.UtcNow;
            SentToContactState.Clear();
            if (File.Exists(SentToContactStatePath))
            {
                var json = File.ReadAllText(SentToContactStatePath);
                try
                {
                    var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, SentToContactStateEntry>>(json);
                    if (dict != null)
                    {
                        foreach (var kvp in dict)
                        {
                            if (now - kvp.Value.lastUpdated < maxAge)
                            {
                                SentToContactState[kvp.Key] = (kvp.Value.sentToContact, kvp.Value.lastUpdated);
                            }
                        }
                    }
                }
                catch { /* Ignore JSON errors */ }
            }
            SaveSentToContactStateJson();
        }
    }

    public static void SaveSentToContactState(string invoiceId, bool sentToContact)
    {
        lock (SentToContactLock)
        {
            if (!sentToContact)
            {
                // Remove the entry if SentToContact is false
                SentToContactState.Remove(invoiceId);
                SaveSentToContactStateJson();
                return;
            }
            var now = DateTime.UtcNow;
            SentToContactState[invoiceId] = (sentToContact, now);
            SaveSentToContactStateJson();
        }
    }

    private static void SaveSentToContactStateJson()
    {
        var dict = SentToContactState.ToDictionary(
            kvp => kvp.Key,
            kvp => new SentToContactStateEntry { sentToContact = kvp.Value.sentToContact, lastUpdated = kvp.Value.lastUpdated }
        );
        var json = System.Text.Json.JsonSerializer.Serialize(dict, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SentToContactStatePath, json);
    }

    private static bool GetLastSentToContactState(string invoiceId)
    {
        lock (SentToContactLock)
        {
            return SentToContactState.TryGetValue(invoiceId, out var state) && state.sentToContact;
        }
    }

    private class SentToContactStateEntry
    {
        public bool sentToContact { get; set; }
        public DateTime lastUpdated { get; set; }
    }

    // Organisation details DTO
    public class OrganisationDetails
    {
        public string? Name { get; set; }
        public string? LegalName { get; set; }
        public string? AddressLine1 { get; set; }
        public string? AddressLine2 { get; set; }
        public string? City { get; set; }
        public string? Region { get; set; }
        public string? PostalCode { get; set; }
        public string? Country { get; set; }
        public string? PhoneNumber { get; set; }
        public string? Email { get; set; }
        public string? Website { get; set; }
    }

    // Fetch organisation details from Xero API
    public async Task<OrganisationDetails?> GetOrganisationDetailsAsync(string tenantId)
    {
        try
        {
            var accessToken = await GetValidAccessTokenAsync();
            var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            var url = "https://api.xero.com/api.xro/2.0/Organisation";
            var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            request.Headers.Add("xero-tenant-id", tenantId);
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning($"[GetOrganisationDetailsAsync] Failed to get organisation details: {response.StatusCode}");
                LogToFile($"[GetOrganisationDetailsAsync] Failed to get organisation details: {response.StatusCode}");
                return null;
            }
            var json = await response.Content.ReadAsStringAsync();
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var org = doc.RootElement.GetProperty("Organisations")[0];

            // Extract address (prefer POBOX, fallback to STREET)
            string? addressLine1 = null, addressLine2 = null, city = null, region = null, postalCode = null, country = null;
            if (org.TryGetProperty("Addresses", out var addresses) && addresses.ValueKind == JsonValueKind.Array)
            {
                JsonElement? bestAddress = null;
                foreach (var addr in addresses.EnumerateArray())
                {
                    if (addr.TryGetProperty("AddressType", out var type) && type.GetString() == "POBOX")
                    {
                        bestAddress = addr;
                        break;
                    }
                    if (bestAddress == null && addr.TryGetProperty("AddressType", out var type2) && type2.GetString() == "STREET")
                    {
                        bestAddress = addr;
                    }
                }
                if (bestAddress.HasValue)
                {
                    var addr = bestAddress.Value;
                    addressLine1 = addr.TryGetProperty("AddressLine1", out var a1) ? a1.GetString() : null;
                    addressLine2 = addr.TryGetProperty("AddressLine2", out var a2) ? a2.GetString() : null;
                    city = addr.TryGetProperty("City", out var c) ? c.GetString() : null;
                    region = addr.TryGetProperty("Region", out var r) ? r.GetString() : null;
                    postalCode = addr.TryGetProperty("PostalCode", out var p) ? p.GetString() : null;
                    country = addr.TryGetProperty("Country", out var co) ? co.GetString() : null;
                }
            }

            // Extract phone (prefer OFFICE)
            string? phoneNumber = null;
            if (org.TryGetProperty("Phones", out var phones) && phones.ValueKind == JsonValueKind.Array)
            {
                foreach (var phone in phones.EnumerateArray())
                {
                    if (phone.TryGetProperty("PhoneType", out var type) && type.GetString() == "OFFICE")
                    {
                        phoneNumber = phone.TryGetProperty("PhoneNumber", out var pn) ? pn.GetString() : null;
                        break;
                    }
                    if (phoneNumber == null)
                    {
                        phoneNumber = phone.TryGetProperty("PhoneNumber", out var pn) ? pn.GetString() : null;
                    }
                }
            }

            // Email and Website are not present in Organisation API response
            string? email = string.Empty;
            string? website = string.Empty;

            // Extract website from ExternalLinks (prefer LinkType == "Website")
            if (org.TryGetProperty("ExternalLinks", out var externalLinks) && externalLinks.ValueKind == JsonValueKind.Array)
            {
                foreach (var link in externalLinks.EnumerateArray())
                {
                    if (link.TryGetProperty("LinkType", out var linkType) && linkType.GetString() == "Website")
                    {
                        website = link.TryGetProperty("Url", out var urlProp) ? urlProp.GetString() : string.Empty;
                        break;
                    }
                }
            }

            // Email fallback: use SharedMailbox from appsettings if not present
            if (string.IsNullOrWhiteSpace(email))
            {
                // Try Entra:SharedMailbox, then Xero:SharedMailbox
                email = _config["Entra:SharedMailbox"] ?? _config["Xero:SharedMailbox"];
            }

            return new OrganisationDetails
            {
                Name = org.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() : null,
                LegalName = org.TryGetProperty("LegalName", out var legalNameProp) ? legalNameProp.GetString() : null,
                AddressLine1 = addressLine1,
                AddressLine2 = addressLine2,
                City = city,
                Region = region,
                PostalCode = postalCode,
                Country = country,
                PhoneNumber = phoneNumber,
                Email = email,
                Website = website
            };
        }
        catch (Exception ex)
        {
            _logger.LogError($"[GetOrganisationDetailsAsync] Error: {ex.Message}");
            LogToFile($"[GetOrganisationDetailsAsync] Error: {ex.Message}");
            return null;
        }
    }

    // Set SentToContact=true for a given invoice
    public async Task<bool> SetInvoiceSentToContactAsync(string tenantId, string invoiceId)
    {
        try
        {
            var accessToken = await GetValidAccessTokenAsync();
            var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            client.DefaultRequestHeaders.Add("xero-tenant-id", tenantId);
            var url = $"https://api.xero.com/api.xro/2.0/Invoices/{invoiceId}";
            var payload = new
            {
                Invoices = new[]
                {
                    new { InvoiceID = invoiceId, SentToContact = true }
                }
            };
            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await client.PostAsync(url, content);
            if (!response.IsSuccessStatusCode)
            {
                var resp = await response.Content.ReadAsStringAsync();
                _logger.LogWarning($"[SetInvoiceSentToContactAsync] Failed to update invoice {invoiceId}: {response.StatusCode} {resp}");
                LogToFile($"[SetInvoiceSentToContactAsync] Failed to update invoice {invoiceId}: {response.StatusCode} {resp}");
                return false;
            }
            LogToFile($"[SetInvoiceSentToContactAsync] Successfully set SentToContact=true for invoice {invoiceId}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError($"[SetInvoiceSentToContactAsync] Error: {ex.Message}");
            LogToFile($"[SetInvoiceSentToContactAsync] Error: {ex.Message}");
            return false;
        }
    }

    public async Task ProcessInvoiceWebhookAsync(string invoiceId, string tenantId, string customerEmail)
    {
        try
        {
            // Get invoice details to extract InvoiceNumber and Reference
            var invoiceDetails = await GetInvoiceDetailsAsync(tenantId, invoiceId);
            string invoiceNumber = invoiceId;
            string reference = "";
            bool sentToContact = false;
            
            if (invoiceDetails.HasValue)
            {
                if (invoiceDetails.Value.TryGetProperty("InvoiceNumber", out var invoiceNumberProp))
                {
                    invoiceNumber = invoiceNumberProp.GetString() ?? invoiceId;
                }
                if (invoiceDetails.Value.TryGetProperty("Reference", out var referenceProp))
                {
                    reference = referenceProp.GetString() ?? "";
                }
                if (invoiceDetails.Value.TryGetProperty("SentToContact", out var sentProp))
                {
                    sentToContact = sentProp.GetBoolean();
                }
            }
            
            // State-based deduplication logic
            bool lastSentToContact = GetLastSentToContactState(invoiceId);
            if (!sentToContact)
            {
                // Always update the state and timestamp when SentToContact is false
                SaveSentToContactState(invoiceId, false);
                LogToFile($"[ProcessInvoiceWebhookAsync] Invoice {invoiceId} SentToContact is false, not sending email, updating state.");
                return;
            }
            if (lastSentToContact)
            {
                LogToFile($"[ProcessInvoiceWebhookAsync] Invoice {invoiceId} SentToContact was already true, not sending email again.");
                return;
            }
            // Only send if transition from false to true
            SaveSentToContactState(invoiceId, true);
            
            var pdfFileName = $"{invoiceNumber}.pdf";
            var emailSubject = string.IsNullOrEmpty(reference) ? $"Invoice {invoiceNumber}" : $"{reference} - ({invoiceNumber})";

            // Try to get the online invoice URL
            var onlineInvoiceUrl = await GetOnlineInvoiceUrlAsync(tenantId, invoiceId);

            // Try to get the PDF
            byte[]? pdfBytes = null;
            try
            {
                pdfBytes = await GetInvoicePdfAsync(tenantId, invoiceId);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ProcessInvoiceWebhookAsync] Failed to fetch PDF for invoice {invoiceId}: {ex.Message}");
                LogToFile($"[ProcessInvoiceWebhookAsync] Failed to fetch PDF for invoice {invoiceId}: {ex.Message}");
            }
            
            // Fetch organisation details for the email footer
            var orgDetails = await GetOrganisationDetailsAsync(tenantId);
            string orgName = orgDetails?.Name ?? orgDetails?.LegalName;
            string orgAddress = string.Join(", ", new[] { orgDetails?.AddressLine1, orgDetails?.AddressLine2, orgDetails?.City, orgDetails?.Region, orgDetails?.PostalCode, orgDetails?.Country }.Where(s => !string.IsNullOrWhiteSpace(s)));
            string orgPhone = orgDetails?.PhoneNumber;
            string orgEmail = orgDetails?.Email;
            string orgWebsite = orgDetails?.Website;
            string orgAddressLine1 = orgDetails?.AddressLine1;
            string orgAddressLine2 = orgDetails?.AddressLine2;
            string orgCity = orgDetails?.City;
            string orgRegion = orgDetails?.Region;
            string orgPostalCode = orgDetails?.PostalCode;
            string orgCountry = orgDetails?.Country;

            string firstName = "";
            List<string> allEmails = new();
            string? primaryEmail = null;
            if (invoiceDetails.HasValue && invoiceDetails.Value.TryGetProperty("Contact", out var contact))
            {
                // FirstName extraction
                if (contact.TryGetProperty("FirstName", out var firstNameProp))
                {
                    firstName = firstNameProp.GetString() ?? "";
                }
                // If not found, try ContactPersons
                if (string.IsNullOrWhiteSpace(firstName) && contact.TryGetProperty("ContactPersons", out var persons) && persons.ValueKind == JsonValueKind.Array)
                {
                    foreach (var person in persons.EnumerateArray())
                    {
                        if (person.TryGetProperty("IncludeInEmails", out var include) && include.GetBoolean())
                        {
                            if (person.TryGetProperty("FirstName", out var personFirstName))
                            {
                                firstName = personFirstName.GetString() ?? "";
                                if (!string.IsNullOrWhiteSpace(firstName)) break;
                            }
                        }
                    }
                }
                // Email extraction
                if (contact.TryGetProperty("EmailAddress", out var emailProp))
                {
                    primaryEmail = emailProp.GetString();
                }
                // Add all emails (including primary) from ExtractContactEmails
                allEmails = ExtractContactEmails(invoiceDetails.Value);
            }
            // Fallback: if no primary, use first in allEmails
            if (string.IsNullOrWhiteSpace(primaryEmail) && allEmails.Count > 0)
                primaryEmail = allEmails[0];
            // Remove primary from CC list
            var ccEmails = allEmails.Where(e => !string.IsNullOrWhiteSpace(e) && e != primaryEmail).Distinct().ToList();
            var toEmails = new List<string>();
            if (!string.IsNullOrWhiteSpace(primaryEmail))
                toEmails.Add(primaryEmail);
            var emailService = new EmailService(_config);
            await emailService.SendInvoiceEmailAsync(
                toEmails,
                ccEmails,
                pdfBytes,
                pdfFileName,
                onlineInvoiceUrl,
                emailSubject,
                orgName,
                orgAddress,
                orgPhone,
                orgEmail,
                orgWebsite,
                firstName,
                invoiceNumber,
                reference,
                orgAddressLine1,
                orgAddressLine2,
                orgCity,
                orgRegion,
                orgPostalCode,
                orgCountry
            );
            
            LogToFile($"[ProcessInvoiceWebhookAsync] Successfully processed invoice webhook for invoice {invoiceId}");
        }
        catch (Exception ex)
        {
            _logger.LogError($"[ProcessInvoiceWebhookAsync] Error processing invoice webhook for invoice {invoiceId}: {ex.Message}");
            LogToFile($"[ProcessInvoiceWebhookAsync] Error processing invoice webhook for invoice {invoiceId}: {ex.Message}");
            throw;
        }
    }
} 